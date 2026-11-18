using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using OverBoardAgent.Core;
using OverBoardAgent.Editor;

namespace OverBoardAgent;

/// <summary>
/// Seite "Verbindungen": WLAN, Home Assistant, MQTT und Kalender. Jede Verbindung hat ihre eigene Karte mit Status,
/// eigener Speichern-Taste und (wo es sinnvoll ist) einem Test. Die Zugangsdaten liegen verschlüsselt im Programm und gehen per USB ans Gerät.
/// </summary>
public partial class ConnectionsView : UserControl
{
    private static readonly IBrush Ok = new SolidColorBrush(Color.Parse("#2E9E5B"));
    private static readonly IBrush Bad = new SolidColorBrush(Color.Parse("#E5484D"));
    private static readonly IBrush Idle = new SolidColorBrush(Color.Parse("#8A8F98"));

    private AgentCore? _core;
    private string _haTest = "";          // Ergebnis des letzten Tests von Home Assistant ("" = nicht getestet)
    private bool _haTestOk;
    private string _calText = "";
    private bool _calOk;

    public ConnectionsView()
    {
        InitializeComponent();
    }

    public void Init(AgentCore core)
    {
        _core = core;
        WifiSsidBox.Text = Settings.WifiSsid;
        HaUrlBox.Text = Settings.HaUrl;
        MqttHostBox.Text = Settings.MqttHost;
        MqttUserBox.Text = Settings.MqttUser;
        ApplyTexts();

        WifiSave.Click += async (_, _) => await SaveWifiAsync();
        HaSave.Click += async (_, _) => await SaveHaAsync();
        HaTest.Click += async (_, _) => await TestHaAsync();
        MqttSave.Click += async (_, _) => await SaveMqttAsync();
        CalSave.Click += async (_, _) => await SaveCalendarAsync(remove: false);
        CalRemove.Click += async (_, _) => await SaveCalendarAsync(remove: true);

        _core.Link.StatusChanged += () => Dispatcher.UIThread.Post(RefreshStatus);
        _core.Link.ConnectionChanged += (_, _) => Dispatcher.UIThread.Post(RefreshStatus);
        _core.CalendarChanged += () => Dispatcher.UIThread.Post(() =>
        {
            _calText = Strings.Format("cal.ok", _core.CalendarLines.Count);
            _calOk = true;
            RefreshStatus();
        });
        RefreshStatus();
    }

    public void ApplyTexts()
    {
        ConnHeader.Text = Strings.Get("nav.connections");
        ConnHint.Text = Strings.Get("conn.hint");
        WifiIcon.FontFamily = HaIcon.FontFamily = MqttIcon.FontFamily = CalIcon.FontFamily = MdiIcons.Font;
        WifiIcon.Text = MdiIcons.Glyph("wifi");
        HaIcon.Text = MdiIcons.Glyph("home-assistant");
        MqttIcon.Text = MdiIcons.Glyph("bell-alert");
        CalIcon.Text = MdiIcons.Glyph("calendar");

        WifiTitle.Text = Strings.Get("setup.wifi");
        WifiSsidLabel.Text = Strings.Get("setup.wifi.ssid");
        WifiPassLabel.Text = Strings.Get("setup.wifi.pass");
        WifiSave.Content = Strings.Get("conn.save");

        HaTitle.Text = Strings.Get("ha.title");
        HaUrlLabel.Text = Strings.Get("ha.url");
        HaTokenLabel.Text = Strings.Get("ha.token");
        HaHint.Text = Strings.Get("ha.token.hint");
        HaTest.Content = Strings.Get("conn.test");
        HaSave.Content = Strings.Get("conn.save");

        MqttTitle.Text = Strings.Get("conn.mqtt.title");
        MqttHint.Text = Strings.Get("conn.mqtt.hint");
        MqttHostLabel.Text = Strings.Get("setup.mqtt.host");
        MqttUserLabel.Text = Strings.Get("setup.mqtt.user");
        MqttPassLabel.Text = Strings.Get("setup.mqtt.pass");
        MqttSave.Content = Strings.Get("conn.save");

        CalTitle.Text = Strings.Get("cal.title");
        CalHint.Text = Strings.Get("cal.hint");
        CalUrlLabel.Text = Strings.Get("cal.url");
        CalSave.Content = Strings.Get("conn.save");
        CalRemove.Content = Strings.Get("conn.remove");

        var saved = Strings.Get("setup.secret.saved");
        var keep = Strings.Get("setup.keep");
        WifiSsidBox.Watermark = Settings.WifiSsid.Length == 0 ? keep : "";
        MqttHostBox.Watermark = Settings.MqttHost.Length == 0 ? keep : "";
        HaUrlBox.Watermark = Settings.HaUrl.Length == 0 ? Strings.Get("ha.url") : "";
        WifiPassBox.Watermark = Settings.WifiPassword.Length > 0 ? saved : "";
        HaTokenBox.Watermark = Settings.HaToken.Length > 0 ? saved : "";
        MqttPassBox.Watermark = Settings.MqttPassword.Length > 0 ? saved : "";
        CalUrlBox.Watermark = Settings.CalendarUrl.Length > 0 ? saved : "";
        CalRemove.IsEnabled = Settings.CalendarUrl.Length > 0;
        RefreshStatus();
    }

    // ---- Status --------------------------------------------------------------------------------

    private static void Show(TextBlock block, string text, IBrush brush)
    {
        block.Text = text;
        block.Foreground = brush;
    }

    private void RefreshStatus()
    {
        if (_core == null) return;
        var link = _core.Link;
        var status = link.Status;

        // WLAN: kommt vom Gerät
        if (!link.IsConnected) Show(WifiStatus, Strings.Get("conn.nodevice"), Idle);
        else if (status == null) Show(WifiStatus, Strings.Get("conn.checking"), Idle);
        else if (status.Wifi) Show(WifiStatus, Strings.Get("conn.connected"), Ok);
        else Show(WifiStatus, Strings.Get(Settings.WifiSsid.Length == 0 ? "conn.wifi.chip" : "conn.notconnected"), Bad);

        // Home Assistant: Gerät meldet die Anmeldung, der Test prüft Adresse und Token im Programm
        if (Settings.HaUrl.Length == 0 || Settings.HaToken.Length == 0) Show(HaStatus, Strings.Get("conn.notset"), Idle);
        else if (link.IsConnected && status != null) Show(HaStatus, Strings.Get(status.Ha ? "conn.loggedin" : "conn.notconnected"), status.Ha ? Ok : Bad);
        else if (_haTest.Length > 0) Show(HaStatus, _haTest, _haTestOk ? Ok : Bad);
        else Show(HaStatus, Strings.Get("conn.saved"), Idle);

        // MQTT: gar nicht eingerichtet ist kein Fehler (grau), eingerichtet aber getrennt schon (rot)
        if (Settings.MqttHost.Length == 0) Show(MqttStatus, Strings.Get("conn.notset"), Idle);
        else if (link.IsConnected && status != null) Show(MqttStatus, Strings.Get(status.Mqtt ? "conn.connected" : "conn.notconnected"), status.Mqtt ? Ok : Bad);
        else Show(MqttStatus, Strings.Get("conn.saved"), Idle);

        // Kalender
        if (Settings.CalendarUrl.Length == 0) Show(CalStatus, Strings.Get("conn.notset"), Idle);
        else Show(CalStatus, _calText.Length > 0 ? _calText : Strings.Get("conn.saved"), _calText.Length == 0 ? Idle : _calOk ? Ok : Bad);
    }

    private static void Result(TextBlock block, string text, bool error = false, bool ok = false)
    {
        block.Text = text;
        block.Foreground = error ? Bad : ok ? Ok : null;
    }

    // ---- WLAN ----------------------------------------------------------------------------------

    private async Task SaveWifiAsync()
    {
        if (_core == null) return;
        Settings.WifiSsid = (WifiSsidBox.Text ?? "").Trim();
        if (!string.IsNullOrEmpty(WifiPassBox.Text)) Settings.WifiPassword = WifiPassBox.Text;
        Settings.Save();
        WifiPassBox.Text = "";
        ApplyTexts();
        await PushAsync(WifiSave, WifiResult);
    }

    // ---- Home Assistant ------------------------------------------------------------------------

    private async Task TestHaAsync()
    {
        var url = (HaUrlBox.Text ?? "").Trim();
        var token = !string.IsNullOrWhiteSpace(HaTokenBox.Text) ? HaTokenBox.Text.Trim() : Settings.HaToken;
        if (url.Length == 0 || token.Length == 0)
        {
            Result(HaResult, Strings.Get("ha.err.token"), error: true);
            return;
        }
        HaTest.IsEnabled = false;
        try
        {
            Result(HaResult, Strings.Get("ha.testing"));
            var (entities, error) = await HaClient.FetchAsync(url, token);
            _haTestOk = entities != null;
            _haTest = entities != null ? Strings.Format("ha.ok", entities.Count) : (error ?? "?");
            Result(HaResult, _haTest, error: entities == null, ok: entities != null);
            RefreshStatus();
        }
        finally
        {
            HaTest.IsEnabled = true;
        }
    }

    private async Task SaveHaAsync()
    {
        if (_core == null) return;
        Settings.HaUrl = (HaUrlBox.Text ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(HaTokenBox.Text)) Settings.HaToken = HaTokenBox.Text.Trim();
        Settings.Save();
        HaTokenBox.Text = "";
        ApplyTexts();
        await TestHaAsync();
        await PushAsync(HaSave, HaResult, append: true);
    }

    // ---- MQTT ----------------------------------------------------------------------------------

    private async Task SaveMqttAsync()
    {
        if (_core == null) return;
        Settings.MqttHost = (MqttHostBox.Text ?? "").Trim();
        Settings.MqttUser = (MqttUserBox.Text ?? "").Trim();
        if (!string.IsNullOrEmpty(MqttPassBox.Text)) Settings.MqttPassword = MqttPassBox.Text;
        Settings.Save();
        MqttPassBox.Text = "";
        ApplyTexts();
        await PushAsync(MqttSave, MqttResult);
    }

    // ---- Kalender ------------------------------------------------------------------------------

    private async Task SaveCalendarAsync(bool remove)
    {
        if (_core == null) return;
        if (remove) Settings.CalendarUrl = "";
        else if (!string.IsNullOrWhiteSpace(CalUrlBox.Text)) Settings.CalendarUrl = CalUrlBox.Text.Trim();
        Settings.Save();
        CalUrlBox.Text = "";
        ApplyTexts();
        CalSave.IsEnabled = false;
        try
        {
            var (ok, error, count) = await _core.RefreshCalendarAsync();
            if (Settings.CalendarUrl.Length == 0)
            {
                _calText = "";
                Result(CalResult, Strings.Get("conn.removed"), ok: true);
            }
            else
            {
                _calOk = ok;
                _calText = ok ? Strings.Format("cal.ok", count) : (error ?? "?");
                Result(CalResult, _calText, error: !ok, ok: ok);
            }
            RefreshStatus();
        }
        finally
        {
            CalSave.IsEnabled = true;
        }
    }

    // ---- Übertragung ans Gerät -----------------------------------------------------------------

    private async Task PushAsync(Button button, TextBlock result, bool append = false)
    {
        if (_core == null) return;
        button.IsEnabled = false;
        try
        {
            var (pushed, error, skipped) = await _core.PushCredentialsAsync();
            string text;
            var failed = false;
            if (pushed && !skipped) text = Strings.Get("setup.pushed");
            else if (skipped && !pushed) text = Strings.Get("setup.nodevice");
            else if (!pushed) { text = Strings.Format("setup.push.fail", error ?? "?"); failed = true; }
            else text = Strings.Get("setup.nothing");
            Result(result, append && result.Text is { Length: > 0 } old ? old + "  " + text : text, error: failed, ok: !failed && pushed && !skipped);
        }
        finally
        {
            button.IsEnabled = true;
        }
    }
}
