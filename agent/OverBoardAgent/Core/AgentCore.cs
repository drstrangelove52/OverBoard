using OverBoardAgent.Platform;

namespace OverBoardAgent.Core;

/// <summary>
/// Verbindet das Gerät mit dem PC: führt Aktionen aus (nur bekannte Kennungen, nie Befehlstexte vom Gerät)
/// und meldet Zustände an das Gerät.
/// </summary>
public sealed class AgentCore : IDisposable
{
    private const int VolumeStep = 5;   // Prozentpunkte pro Tastendruck

    public AgentCore(IPlatform platform, DeviceLink link, ActionStore actions)
    {
        Platform = platform;
        Link = link;
        Actions = actions;
        Platform.StateChanged += (id, value) => Link.SendState(id, value);
        Link.ActionReceived += id => Run(id);
        Link.ConnectionChanged += (connected, _) =>
        {
            if (!connected) return;
            foreach (var (id, value) in Platform.GetStates()) Link.SendState(id, value);   // nach hello einmal alles
            Task.Run(async () => await PushCredentialsAsync());                      // Home-Assistant-Zugang ans Gerät
        };
    }

    public IPlatform Platform { get; }
    public DeviceLink Link { get; }
    public ActionStore Actions { get; }

    /// <summary>
    /// Überträgt WLAN, Home Assistant und MQTT ans Gerät (damit sie nur im Agent verwaltet werden). Nur eingerichtete Teile werden
    /// gesendet; das Gerät ignoriert unveränderte. Skipped=true: nichts gesendet (nichts eingerichtet oder kein Gerät verbunden).
    /// </summary>
    public async Task<(bool Ok, string? Error, bool Skipped)> PushCredentialsAsync()
    {
        var payload = new Dictionary<string, object> { ["t"] = "creds_set" };

        if (Settings.WifiSsid.Length > 0)
            payload["wifi"] = new { ssid = Settings.WifiSsid, pass = Settings.WifiPassword };

        var ha = HaClient.ToSocketUri(Settings.HaUrl);
        if (ha != null && Settings.HaToken.Trim().Length > 0)
        {
            if (ha.Scheme != "ws") return (false, Strings.Get("ha.push.wss"), false);
            payload["ha"] = new { uri = ha.ToString().TrimEnd('/'), token = Settings.HaToken.Trim() };
        }

        if (Settings.MqttHost.Length > 0)
        {
            var host = Settings.MqttHost.Trim();
            var uri = host.Contains("://", StringComparison.Ordinal) ? host : "mqtt://" + (host.Contains(':') ? host : host + ":1883");
            payload["mqtt"] = new { uri, user = Settings.MqttUser, pass = Settings.MqttPassword };
        }

        if (payload.Count == 1) return (true, null, true);                  // nichts eingerichtet
        if (!Link.IsConnected) return (false, null, true);
        var (ok, error) = await Link.SendCredentialsAsync(System.Text.Json.JsonSerializer.Serialize(payload));
        Log.Info(ok ? "Zugangsdaten ans Gerät übertragen" : $"Zugangsdaten nicht übertragen: {error}");
        return (ok, error, false);
    }

    /// <summary>Führt die Aktion mit dieser Kennung aus (eingebaut oder selbst definiert). Liefert false bei Fehlern.</summary>
    public bool Run(string id)
    {
        Log.Info($"Aktion: {id}");
        try
        {
            switch (id)
            {
                case "volume.up": Platform.AdjustVolume(+VolumeStep); return true;
                case "volume.down": Platform.AdjustVolume(-VolumeStep); return true;
                case "mute.toggle": Platform.ToggleMute(); return true;
                case "mic.toggle": Platform.ToggleMicMute(); return true;
                case "lock": Platform.LockWorkstation(); return true;
            }

            if (BuiltinActions.Find(id) is { } builtin)
            {
                if (builtin.Keys != null) return Platform.SendKeys(builtin.Keys);
                if (builtin.System != null) return Platform.SystemCommand(builtin.System);
            }

            var action = Actions.Find(id);
            if (action == null)
            {
                Log.Warn($"Unbekannte Aktion '{id}' ignoriert");
                return false;
            }
            switch (action.Kind)
            {
                case ActionKind.Open:
                    Platform.OpenTarget(action.Target, action.Arguments);
                    return true;
                case ActionKind.Keys:
                    if (!Platform.SendKeys(action.Keys))
                    {
                        Log.Warn($"Tasten '{action.Keys}' konnten nicht gesendet werden");
                        return false;
                    }
                    return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            Log.Error($"Aktion '{id}' fehlgeschlagen: {ex.Message}");
            return false;
        }
    }

    public void Dispose()
    {
        Link.Dispose();
        Platform.Dispose();
    }
}
