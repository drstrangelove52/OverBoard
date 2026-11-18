using System.Diagnostics;
using Avalonia.Controls;
using OverBoardAgent.Core;

namespace OverBoardAgent;

/// <summary>Seite "Programm": Einstellungen der App selbst (Sprache, Autostart) und Informationen.</summary>
public partial class AppView : UserControl
{
    private AgentCore? _core;
    private Action? _languageChanged;
    private bool _loading;

    public AppView()
    {
        InitializeComponent();
    }

    public void Init(AgentCore core, Action languageChanged)
    {
        _core = core;
        _languageChanged = languageChanged;
        _loading = true;
        // Sprachnamen stehen in der jeweiligen Sprache selbst und ändern sich nicht (so findet man sie immer)
        LanguageBox.Items.Add("Deutsch");
        LanguageBox.Items.Add("English");
        LanguageBox.SelectedIndex = Settings.Language == "en" ? 1 : 0;
        AutostartBox.IsChecked = SafeAutostart();
        _loading = false;
        ApplyTexts();

        LanguageBox.SelectionChanged += (_, _) =>
        {
            if (_loading) return;
            Settings.Language = LanguageBox.SelectedIndex == 1 ? "en" : "de";
            Settings.Save();
            (Avalonia.Application.Current as App)?.RefreshTexts();
            _languageChanged?.Invoke();       // das Hauptfenster übersetzt alle Seiten neu
        };
        AutostartBox.IsCheckedChanged += (_, _) =>
        {
            if (_loading) return;
            try { _core.Platform.SetAutostart(AutostartBox.IsChecked == true); }
            catch (Exception ex) { Log.Warn($"Autostart: {ex.Message}"); }
            (Avalonia.Application.Current as App)?.RefreshTexts();
        };
        UpdateBox.IsChecked = Settings.UpdateCheck;
        UpdateBox.IsCheckedChanged += (_, _) =>
        {
            if (_loading) return;
            Settings.UpdateCheck = UpdateBox.IsChecked == true;
            Settings.Save();
        };
        CheckNowButton.Click += async (_, _) => await CheckNowAsync();
        InstallUpdateButton.Click += async (_, _) => await InstallAsync();
        UpdateService.Changed += OnUpdateChanged;
        UpdateStatusRefresh();
        OpenDataButton.Click += (_, _) => OpenFolder(Settings.DataDir);
        OpenLogButton.Click += (_, _) => OpenFolder(Path.GetDirectoryName(Log.LogFile) ?? Settings.DataDir);
    }

    private void OnUpdateChanged() => Avalonia.Threading.Dispatcher.UIThread.Post(UpdateStatusRefresh);

    private async Task CheckNowAsync()
    {
        CheckNowButton.IsEnabled = false;
        UpdateStatus.Foreground = null;
        UpdateStatus.Text = Strings.Get("upd.checking");
        await UpdateService.CheckAsync();
        CheckNowButton.IsEnabled = true;
        UpdateStatusRefresh();
    }

    private async Task InstallAsync()
    {
        var info = UpdateService.Latest;
        if (info == null) return;
        InstallUpdateButton.IsEnabled = false;
        CheckNowButton.IsEnabled = false;
        UpdateProgress.IsVisible = true;
        UpdateProgress.Value = 0;
        UpdateStatus.Text = Strings.Get("upd.downloading");
        var (ok, error) = await UpdateService.InstallAsync(info, new Progress<int>(p => UpdateProgress.Value = p));
        if (ok)
        {
            UpdateStatus.Text = Strings.Get("upd.installing");
            (Avalonia.Application.Current as App)?.QuitForUpdate();
            return;
        }
        UpdateProgress.IsVisible = false;
        InstallUpdateButton.IsEnabled = true;
        CheckNowButton.IsEnabled = true;
        UpdateStatus.Text = error;
        UpdateStatus.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#E5484D"));
    }

    /// <summary>Zeigt an, welche Version läuft, ob eine neuere da ist und wann zuletzt geprüft wurde.</summary>
    private void UpdateStatusRefresh()
    {
        var current = UpdateService.Current.ToString();
        var latest = UpdateService.Latest;
        InstallUpdateButton.IsVisible = UpdateService.UpdateAvailable;
        if (UpdateService.UpdateAvailable)
        {
            InstallUpdateButton.Content = Strings.Format("upd.install", latest!.Version);
            UpdateStatus.Text = Strings.Format("upd.available", latest.Version, current);
            UpdateStatus.Foreground = null;
            UpdateNotes.Text = latest.Notes.Length > 600 ? latest.Notes[..600] + " …" : latest.Notes;
        }
        else
        {
            UpdateNotes.Text = "";
            if (UpdateService.LastError != null)
            {
                UpdateStatus.Text = UpdateService.LastError;
                UpdateStatus.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#E5484D"));
            }
            else
            {
                UpdateStatus.Foreground = null;
                UpdateStatus.Text = Settings.LastUpdateCheckUtc == 0
                    ? Strings.Format("upd.current", current)
                    : Strings.Format("upd.uptodate", current, new DateTime(Settings.LastUpdateCheckUtc, DateTimeKind.Utc).ToLocalTime().ToString("g"));
            }
        }
    }

    private bool SafeAutostart()
    {
        try { return _core!.Platform.IsAutostartEnabled(); }
        catch { return false; }
    }

    private static void OpenFolder(string path)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warn($"Ordner nicht geöffnet: {ex.Message}"); }
    }

    public void ApplyTexts()
    {
        AppHeader.Text = Strings.Get("nav.app");
        UpdateHeader.Text = Strings.Get("upd.title");
        UpdateHint.Text = Strings.Get("upd.hint");
        UpdateBox.Content = Strings.Get("upd.auto");
        CheckNowButton.Content = Strings.Get("upd.check");
        if (_core != null) UpdateStatusRefresh();
        LanguageLabel.Text = Strings.Get("win.language");
        AutostartBox.Content = Strings.Get("win.autostart");
        AboutHeader.Text = Strings.Get("app.about");
        VersionText.Text = "OverBoard Agent " + (typeof(App).Assembly.GetName().Version?.ToString(3) ?? "");
        FoldersText.Text = Strings.Get("app.folders");
        OpenDataButton.Content = Strings.Get("app.open.data");
        OpenLogButton.Content = Strings.Get("app.open.log");
    }
}
