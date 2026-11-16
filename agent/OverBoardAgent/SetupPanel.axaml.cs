using Avalonia.Controls;
using Avalonia.Media;
using OverBoardAgent.Core;

namespace OverBoardAgent;

/// <summary>Einrichtung des Geräts: WLAN, Home Assistant und MQTT. Wird verschlüsselt gespeichert und per USB ans Gerät übertragen.</summary>
public partial class SetupPanel : UserControl
{
    private AgentCore? _core;

    public SetupPanel()
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
        SaveButton.Click += async (_, _) => await SaveAsync();
    }

    public void ApplyTexts()
    {
        SetupHint.Text = Strings.Get("setup.hint");
        WifiLabel.Text = Strings.Get("setup.wifi");
        WifiSsidLabel.Text = Strings.Get("setup.wifi.ssid");
        WifiPassLabel.Text = Strings.Get("setup.wifi.pass");
        HaLabel.Text = Strings.Get("ha.title");
        HaUrlLabel.Text = Strings.Get("ha.url");
        HaTokenLabel.Text = Strings.Get("ha.token");
        HaHint.Text = Strings.Get("ha.token.hint");
        MqttLabel.Text = Strings.Get("setup.mqtt");
        MqttHint.Text = Strings.Get("setup.mqtt.hint");
        MqttHostLabel.Text = Strings.Get("setup.mqtt.host");
        MqttUserLabel.Text = Strings.Get("setup.mqtt.user");
        MqttPassLabel.Text = Strings.Get("setup.mqtt.pass");
        CalLabel.Text = Strings.Get("cal.title");
        CalHint.Text = Strings.Get("cal.hint");
        CalUrlLabel.Text = Strings.Get("cal.url");
        CalUrlBox.Watermark = Settings.CalendarUrl.Length > 0 ? Strings.Get("setup.secret.saved") : "";
        SaveButton.Content = Strings.Get("setup.save");
        var saved = Strings.Get("setup.secret.saved");
        // Leere Felder erklären sich selbst: was hier nicht steht, behält das Gerät so, wie es ist
        var keep = Strings.Get("setup.keep");
        WifiSsidBox.Watermark = Settings.WifiSsid.Length == 0 ? keep : "";
        MqttHostBox.Watermark = Settings.MqttHost.Length == 0 ? keep : "";
        HaUrlBox.Watermark = Settings.HaUrl.Length == 0 ? Strings.Get("ha.url") : "";
        WifiPassBox.Watermark = Settings.WifiPassword.Length > 0 ? saved : "";
        HaTokenBox.Watermark = Settings.HaToken.Length > 0 ? saved : "";
        MqttPassBox.Watermark = Settings.MqttPassword.Length > 0 ? saved : "";
    }

    private void Result(string text, bool error = false, bool ok = false)
    {
        ResultText.Text = text;
        ResultText.Foreground = error ? new SolidColorBrush(Color.Parse("#E5484D"))
            : ok ? new SolidColorBrush(Color.Parse("#2E9E5B")) : null;
    }

    /// <summary>Speichert die Eingaben, prüft Home Assistant und überträgt alles ans Gerät.</summary>
    private async Task SaveAsync()
    {
        if (_core == null) return;
        // Leere Passwortfelder bedeuten "unverändert"
        Settings.WifiSsid = (WifiSsidBox.Text ?? "").Trim();
        if (!string.IsNullOrEmpty(WifiPassBox.Text)) Settings.WifiPassword = WifiPassBox.Text;
        Settings.HaUrl = (HaUrlBox.Text ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(HaTokenBox.Text)) Settings.HaToken = HaTokenBox.Text.Trim();
        Settings.MqttHost = (MqttHostBox.Text ?? "").Trim();
        Settings.MqttUser = (MqttUserBox.Text ?? "").Trim();
        if (!string.IsNullOrEmpty(MqttPassBox.Text)) Settings.MqttPassword = MqttPassBox.Text;
        var calChanged = !string.IsNullOrWhiteSpace(CalUrlBox.Text);
        if (calChanged) Settings.CalendarUrl = CalUrlBox.Text!.Trim() == "-" ? "" : CalUrlBox.Text!.Trim();   // "-" entfernt den Kalender
        Settings.Save();
        WifiPassBox.Text = HaTokenBox.Text = MqttPassBox.Text = CalUrlBox.Text = "";
        ApplyTexts();

        SaveButton.IsEnabled = false;
        try
        {
            var lines = new List<string>();
            var failed = false;

            if (Settings.HaUrl.Length > 0 || Settings.HaToken.Length > 0)
            {
                Result(Strings.Get("ha.testing"));
                var (entities, error) = await HaClient.FetchAsync(Settings.HaUrl, Settings.HaToken);
                if (entities != null) lines.Add(Strings.Format("ha.ok", entities.Count));
                else { lines.Add(error ?? "?"); failed = true; }
            }

            if (Settings.CalendarUrl.Length > 0)
            {
                var (calOk, calError, calCount) = await _core.RefreshCalendarAsync();
                if (calOk) lines.Add(Strings.Format("cal.ok", calCount)); else { lines.Add(calError ?? "?"); failed = true; }
            }

            var (pushed, pushError, skipped) = await _core.PushCredentialsAsync();
            if (pushed && !skipped) lines.Add(Strings.Get("setup.pushed"));
            else if (skipped && !pushed) lines.Add(Strings.Get("setup.nodevice"));
            else if (!pushed) { lines.Add(Strings.Format("setup.push.fail", pushError ?? "?")); failed = true; }
            else lines.Add(Strings.Get("setup.nothing"));

            Result(string.Join("  ", lines), error: failed, ok: !failed);
        }
        finally
        {
            SaveButton.IsEnabled = true;
        }
    }
}
