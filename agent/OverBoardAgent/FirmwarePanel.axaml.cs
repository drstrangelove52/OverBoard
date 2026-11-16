using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using OverBoardAgent.Core;

namespace OverBoardAgent;

/// <summary>Firmware des Geräts: Version anzeigen und eine Firmware (ESP-IDF-Build-Ordner) über die UART-Buchse installieren.</summary>
public partial class FirmwarePanel : UserControl
{
    private AgentCore? _core;
    private FirmwarePackage? _package;
    private bool _busy;
    private string? _bundledDir;

    public FirmwarePanel()
    {
        InitializeComponent();
    }

    private Window? Owner => TopLevel.GetTopLevel(this) as Window;

    public void Init(AgentCore core)
    {
        _core = core;
        // Die Firmware, die mit dem Programm kommt (Ordner "firmware" neben dem Programm), ist die Voreinstellung
        var bundled = Path.Combine(AppContext.BaseDirectory, "firmware");
        if (File.Exists(Path.Combine(bundled, "flasher_args.json"))) _bundledDir = bundled;
        if (_bundledDir != null && (Settings.FirmwareDir.Length == 0 || !Directory.Exists(Settings.FirmwareDir))) Settings.FirmwareDir = _bundledDir;
        SourceBox.Text = Settings.FirmwareDir;
        // Mit mitgelieferter Firmware ist die Ordnerwahl nur für Entwickler: eingeklappt unter "Erweitert"
        AdvancedExpander.IsExpanded = _bundledDir == null;
        ApplyTexts();
        SourceBox.LostFocus += (_, _) => SourceChanged();
        BrowseButton.Click += async (_, _) => await BrowseAsync();
        InstallButton.Click += async (_, _) => await InstallAsync();
        _core.Link.ConnectionChanged += (_, _) => Dispatcher.UIThread.Post(UpdateInstalled);
        // Kabel einstecken oder abziehen: die Anzeige folgt von selbst (nur solange das Fenster offen ist)
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        timer.Tick += (_, _) => { if (!_busy) UpdateInstalled(); };
        AttachedToVisualTree += (_, _) => timer.Start();
        DetachedFromVisualTree += (_, _) => timer.Stop();
        SourceChanged();
    }

    public void ApplyTexts()
    {
        AdvancedExpander.Header = Strings.Get("fw.advanced");
        SourceLabel.Text = Strings.Get("fw.source");
        BrowseButton.Content = Strings.Get("fw.browse");
        InstallButton.Content = Strings.Get("fw.install");
        UpdateInstalled();
        UpdateAvailable();
    }

    private void UpdateInstalled()
    {
        if (_core == null) return;
        var info = _core.Link.Device;
        if (_core.Link.IsConnected)
        {
            InstalledText.Text = Strings.Format("fw.installed", info?.Firmware ?? "?");
            return;
        }
        // Nicht per USB 2.0 verbunden: zeigen, ob das Display wenigstens an der UART-Buchse hängt (dann lässt sich die Firmware installieren)
        var uart = PortFinder.FindUartPorts();
        InstalledText.Text = uart.Count > 0 ? Strings.Format("fw.installed.uart", uart[0]) : Strings.Get("fw.installed.none");
    }

    /// <summary>Vergleicht Versionen der Form v1.2.3 (ein Zusatz wie -dirty zählt nicht). null, wenn eine davon keine Versionsnummer ist.</summary>
    private static int? CompareVersions(string a, string b)
    {
        static Version? P(string v)
        {
            var m = System.Text.RegularExpressions.Regex.Match(v, @"^v?(\d+)\.(\d+)\.(\d+)");
            return m.Success ? new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value)) : null;
        }
        var x = P(a); var y = P(b);
        return x == null || y == null ? null : x.CompareTo(y);
    }

    private void UpdateAvailable()
    {
        if (_package != null)
        {
            var text = Strings.Format("fw.available", _package.Version ?? "?", _package.BuiltAt ?? "?");
            var installed = _core?.Link.Device?.Firmware;
            if (installed != null && _package.Version != null)
            {
                var cmp = CompareVersions(_package.Version, installed);
                if (cmp is null)
                {
                    if (!string.Equals(installed.TrimStart('v'), _package.Version.TrimStart('v'), StringComparison.OrdinalIgnoreCase))
                        text += "  " + Strings.Get("fw.differs");
                }
                else text += "  " + Strings.Get(cmp > 0 ? "fw.newer" : cmp < 0 ? "fw.older" : "fw.same");
            }
            if (_core?.Link.Device is { ProtocolMatches: false })
                text += "  " + Strings.Get("fw.protocol");
            AvailableText.Text = text;
        }
        InstallButton.IsEnabled = _package != null && !_busy;
    }

    private void SourceChanged()
    {
        var dir = (SourceBox.Text ?? "").Trim();
        Settings.FirmwareDir = dir;
        Settings.Save();
        _package = null;
        if (dir.Length == 0)
        {
            AvailableText.Text = Strings.Get("fw.hint");
        }
        else
        {
            var (pkg, error) = FirmwareFlasher.LoadPackage(dir);
            _package = pkg;
            if (pkg == null) AvailableText.Text = error;
        }
        UpdateAvailable();
    }

    private async Task BrowseAsync()
    {
        if (Owner == null) return;
        var folders = await Owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            AllowMultiple = false,
            Title = Strings.Get("fw.source"),
        });
        if (folders.Count == 0) return;
        SourceBox.Text = folders[0].TryGetLocalPath() ?? "";
        SourceChanged();
    }

    private void Result(string text, bool error = false, bool ok = false)
    {
        ResultText.Text = text;
        ResultText.Foreground = error ? new SolidColorBrush(Color.Parse("#E5484D"))
            : ok ? new SolidColorBrush(Color.Parse("#2E9E5B")) : null;
    }

    private async Task InstallAsync()
    {
        if (_package == null || _busy || Owner == null) return;

        var ports = PortFinder.FindUartPorts();
        if (ports.Count == 0)
        {
            Result(Strings.Get("fw.err.noport"), error: true);
            return;
        }
        var port = ports[0];
        if (!await ConfirmAsync(Strings.Format("fw.confirm", _package.Version ?? "?", port, _core?.Link.Device?.Firmware ?? "?", _package.BuiltAt ?? "?"))) return;

        _busy = true;
        UpdateAvailable();
        Progress.IsVisible = true;
        Progress.Value = 0;
        Result(Strings.Get("fw.running"));
        LogText.Text = "";
        try
        {
            var progress = new Progress<(int Percent, string Line)>(p =>
            {
                if (p.Percent >= 0) Progress.Value = p.Percent;
                LogText.Text = p.Line;
            });
            var (ok, error) = await FirmwareFlasher.FlashAsync(_package, port, progress);
            if (ok)
            {
                Progress.Value = 100;
                Result(Strings.Get("fw.done"), ok: true);
            }
            else
            {
                Result(error ?? "?", error: true);
            }
        }
        catch (Exception ex)
        {
            Result(Strings.Format("fw.err.failed", ex.Message), error: true);
        }
        finally
        {
            _busy = false;
            UpdateAvailable();
        }
    }

    private async Task<bool> ConfirmAsync(string text)
    {
        if (Owner == null) return false;
        var dialog = new Window
        {
            Title = "OverBoard",
            Width = 500,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var yes = new Button { Content = Strings.Get("fw.install"), MinWidth = 120, Classes = { "accent" } };
        var no = new Button { Content = Strings.Get("edit.cancel"), MinWidth = 90 };
        dialog.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(24),
            Spacing = 20,
            Children =
            {
                new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 10,
                    Children = { yes, no },
                },
            },
        };
        yes.Click += (_, _) => dialog.Close(true);
        no.Click += (_, _) => dialog.Close(false);
        return await dialog.ShowDialog<bool>(Owner);
    }
}
