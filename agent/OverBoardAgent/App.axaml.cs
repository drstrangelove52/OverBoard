using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using OverBoardAgent.Core;
using OverBoardAgent.Platform;

namespace OverBoardAgent;

public partial class App : Application
{
    private AgentCore? _core;
    private TrayIcon? _tray;
    private NativeMenuItem? _statusItem;
    private NativeMenuItem? _openItem;
    private NativeMenuItem? _updateItem;
    private NativeMenuItem? _autostartItem;
    private NativeMenuItem? _quitItem;
    private Editor.EditorWindow? _mainWin;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// Entwicklungshilfe: "--export-preview konfiguration.json ausgabe.png [Seite]" zeichnet die Display-Ansicht in eine Datei
    /// und beendet das Programm. Damit lässt sich die Darstellung ohne Fenster prüfen.
    /// </summary>
    private bool TryExportPreview()
    {
        var args = Environment.GetCommandLineArgs();
        var i = Array.IndexOf(args, "--export-preview");
        if (i < 0 || args.Length < i + 3) return false;
        try
        {
            var config = Editor.ConfigModel.Parse(File.ReadAllText(args[i + 1]));
            var page = args.Length > i + 3 && int.TryParse(args[i + 3], out var p) ? p : 0;
            var canvas = Editor.DisplayRenderer.Build(config, new Editor.PreviewOptions { Page = page });
            canvas.Measure(new Size(1024, 600));
            canvas.Arrange(new Rect(0, 0, 1024, 600));
            var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize(1024, 600), new Vector(96, 96));
            bitmap.Render(canvas);
#pragma warning disable CS0618   // Bitmap.Save(string): die neuere Überladung braucht nur für Qualitätsoptionen mehr
            bitmap.Save(args[i + 2]);
#pragma warning restore CS0618
        }
        catch (Exception ex)
        {
            File.WriteAllText(args[i + 2] + ".fehler.txt", ex.ToString());
        }
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            Dispatcher.UIThread.Post(() => desktop.Shutdown());
        return true;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (TryExportPreview())
        {
            base.OnFrameworkInitializationCompleted();
            return;
        }
        Settings.Load();
        Log.Info("OverBoard Agent startet");
        Log.Info($"Benutzer {Environment.UserDomainName}\\{Environment.UserName}, Daten {Settings.DataDir}, Protokoll {Log.LogFile}, Temp {Path.GetTempPath()}");

        // Ein Tray-Programm darf wegen eines Oberflächenfehlers nicht sterben: protokollieren und weiterlaufen
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            Log.Error($"Unbehandelter Fehler in der Oberfläche: {e.Exception}");
            e.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Warn($"Unbeobachteter Fehler: {e.Exception.GetBaseException().Message}");
            e.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error($"Absturz: {e.ExceptionObject}");

        var version = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
        var link = new DeviceLink(version);
        // Die Audio-Objekte auf einem Hintergrundthread anlegen: sonst gehören sie zum Oberflächen-Thread, und jeder Aufruf
        // von einem anderen Thread (Aktionen vom Gerät) müsste über dessen Nachrichtenschleife laufen
        var platform = Task.Run(() => new WindowsPlatform()).GetAwaiter().GetResult();
        _core = new AgentCore(platform, link, new ActionStore());
        link.ConnectionChanged += (_, _) => Dispatcher.UIThread.Post(RefreshTexts);
        _core.StartCalendar();
        Cli.StartServer(_core);        // nimmt --send und --get von der Kommandozeile an

        BuildTray();
        UpdateService.Changed += () => Dispatcher.UIThread.Post(RefreshTexts);
        _ = Task.Run(UpdateLoopAsync);
        link.Start();
        // Mit "--settings" gestartet öffnet sich gleich das Einstellungsfenster (praktisch zum Testen)
        // Das Hauptfenster öffnet sich beim Start, ausser beim automatischen Start mit Windows ("--tray"): dann nur das Tray-Symbol
        if (!Environment.GetCommandLineArgs().Contains("--tray")) Dispatcher.UIThread.Post(() => OpenMain());

        // Ein zweiter Start des Programms (z. B. über das Startmenü-Symbol) ruft das Hauptfenster
        var showEvent = Program.ShowEvent;
        if (showEvent != null)
            new Thread(() =>
            {
                while (true)
                {
                    try { showEvent.WaitOne(); } catch { return; }
                    Dispatcher.UIThread.Post(() => OpenMain());
                }
            }) { IsBackground = true, Name = "OverBoard-Show" }.Start();

        // Eintrag für den Autostart auffrischen (Pfad nach einem Update, Option "--tray")
        try { if (platform.IsAutostartEnabled()) platform.SetAutostart(true); } catch (Exception ex) { Log.Warn($"Autostart: {ex.Message}"); }
        if (Environment.GetCommandLineArgs().Contains("--editor"))
            Dispatcher.UIThread.Post(() => OpenMain());

        base.OnFrameworkInitializationCompleted();
    }

    private void BuildTray()
    {
        _statusItem = new NativeMenuItem { IsEnabled = false };
        _openItem = new NativeMenuItem();
        _openItem.Click += (_, _) => OpenMain();
        _updateItem = new NativeMenuItem { IsVisible = false };
        _updateItem.Click += (_, _) => OpenMain(5);      // Seite "Programm"
        _autostartItem = new NativeMenuItem { ToggleType = MenuItemToggleType.CheckBox };
        _autostartItem.Click += (_, _) => ToggleAutostart();
        _quitItem = new NativeMenuItem();
        _quitItem.Click += (_, _) => Quit();
        var menu = new NativeMenu
        {
            _statusItem, new NativeMenuItemSeparator(), _openItem, _updateItem, _autostartItem,
            new NativeMenuItemSeparator(), _quitItem,
        };

        _tray = new TrayIcon
        {
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://OverBoardAgent/Assets/tray.png"))),
            Menu = menu,
        };
        _tray.Clicked += (_, _) => OpenMain();   // Klick auf das Symbol öffnet das Hauptfenster
        TrayIcon.SetIcons(this, new TrayIcons { _tray });
        RefreshTexts();
    }

    /// <summary>Aktualisiert Texte und Häkchen im Tray-Menü (nach Verbindungs-, Sprach- oder Autostart-Änderung).</summary>
    public void RefreshTexts()
    {
        if (_core == null || _tray == null) return;
        var connected = _core.Link.IsConnected;
        _statusItem!.Header = Strings.Get(connected ? "tray.connected" : "tray.disconnected");
        _openItem!.Header = Strings.Get("tray.open");
        _updateItem!.IsVisible = UpdateService.UpdateAvailable;
        if (UpdateService.UpdateAvailable) _updateItem.Header = Strings.Format("upd.tray", UpdateService.Latest!.Version);
        _autostartItem!.Header = Strings.Get("tray.autostart");
        _autostartItem.IsChecked = SafeAutostart();
        _quitItem!.Header = Strings.Get("tray.quit");
        _tray.ToolTipText = Strings.Get(connected ? "tray.tooltip.connected" : "tray.tooltip.disconnected");
    }

    private bool SafeAutostart()
    {
        try { return _core!.Platform.IsAutostartEnabled(); }
        catch { return false; }
    }

    private void ToggleAutostart()
    {
        try { _core!.Platform.SetAutostart(!SafeAutostart()); }
        catch (Exception ex) { Log.Warn($"Autostart: {ex.Message}"); }
        RefreshTexts();
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr min, IntPtr max);

    /// <summary>Gibt nicht benutzten Arbeitsspeicher an Windows zurück (-1/-1 = Arbeitssatz leeren).</summary>
    private static void TrimWorkingSet()
    {
        try { SetProcessWorkingSetSize(System.Diagnostics.Process.GetCurrentProcess().Handle, (IntPtr)(-1), (IntPtr)(-1)); }
        catch { /* nur eine Optimierung */ }
    }

    /// <summary>Öffnet das Hauptfenster (oder holt es nach vorn).</summary>
    public void OpenMain(int page = 0)
    {
        if (_core == null) return;
        if (_mainWin == null)
        {
            _mainWin = new Editor.EditorWindow(_core);
            _mainWin.Closed += (_, _) =>
            {
                _mainWin = null;
                // Fenster zu: dessen Speicher freigeben, damit der Agent im Tray schlank bleibt
                Task.Run(async () =>
                {
                    await Task.Delay(1500);
                    GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
                    GC.WaitForPendingFinalizers();
                    TrimWorkingSet();
                });
            };
            _mainWin.Show();
        }
        else if (page != 0)
        {
            _mainWin.ShowPage(page);
        }
        _mainWin.Activate();
        if (page != 0) _mainWin.ShowPage(page);
    }

    /// <summary>Für ein Update: sauber beenden, damit der Installer die Dateien ersetzen kann.</summary>
    public void QuitForUpdate() => Quit();

    /// <summary>Sucht nach dem Start (und danach regelmässig) im Hintergrund nach Updates, höchstens einmal täglich.</summary>
    private async Task UpdateLoopAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(30));
        while (true)
        {
            if (UpdateService.DueForAutoCheck()) await UpdateService.CheckAsync();
            await Task.Delay(TimeSpan.FromHours(6));
        }
    }

    private void Quit()
    {
        Log.Info("OverBoard Agent beendet");
        _core?.Dispose();
        _tray?.Dispose();
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) desktop.Shutdown();
    }
}
