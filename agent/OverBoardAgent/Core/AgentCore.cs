using OverBoardAgent.Platform;

namespace OverBoardAgent.Core;

/// <summary>
/// Verbindet das Gerät mit dem PC: führt Aktionen aus (nur bekannte Kennungen, nie Befehlstexte vom Gerät)
/// und meldet Zustände an das Gerät.
/// </summary>
public sealed class AgentCore : IDisposable
{

    public AgentCore(IPlatform platform, DeviceLink link, ActionStore actions)
    {
        Platform = platform;
        Link = link;
        Actions = actions;
        Platform.StateChanged += (id, value) => Link.SendState(id, value);
        Radio = new RadioService { Send = line => Link.Send(line) };
        Platform.AwayChanged += away => Link.Send(Protocol.Pc(away));   // das Gerät schaltet danach sein Display (Einstellung "Energiesparen")
        Link.ActionReceived += id => Task.Run(() => Run(id));   // nicht auf dem Lesethread: er muss weiter Nachrichten annehmen
        Link.ConnectionChanged += (connected, _) =>
        {
            if (!connected) return;
            RefreshCalendarSoon();
            Radio.OnDeviceConnected();
            foreach (var (id, value) in Platform.GetStates()) Link.SendState(id, value);   // nach hello einmal alles
            Link.Send(Protocol.Pc(Platform.IsAway));
            Task.Run(async () => await PushCredentialsAsync());                      // Home-Assistant-Zugang ans Gerät
        };
    }

    // ---- Kalender: die Termine werden hier geladen und ans Gerät geschickt (die Adresse bleibt im Programm) ----

    private readonly SemaphoreSlim _calKick = new(0);
    private readonly CancellationTokenSource _cts = new();
    private Task? _calLoop;

    /// <summary>Die zuletzt geladenen Termine (für die Vorschau im Editor).</summary>
    public IReadOnlyList<CalLine> CalendarLines { get; private set; } = [];
    public event Action? CalendarChanged;

    /// <summary>Startet das regelmässige Laden (alle 10 Minuten).</summary>
    public void StartCalendar()
    {
        _calLoop ??= Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                try { await RefreshCalendarAsync(); }
                catch (Exception ex) { Log.Warn($"Kalender: {ex.Message}"); }
                try { await _calKick.WaitAsync(TimeSpan.FromMinutes(10), _cts.Token); }
                catch (OperationCanceledException) { return; }
            }
        });
    }

    /// <summary>Lädt den Kalender bald neu (nach geänderter Adresse oder neuer Verbindung zum Gerät).</summary>
    public void RefreshCalendarSoon()
    {
        if (_calKick.CurrentCount == 0) _calKick.Release();
    }

    /// <summary>Lädt den Kalender jetzt und schickt die Termine ans Gerät. Ohne Adresse werden die Termine geleert.</summary>
    public async Task<(bool Ok, string? Error, int Count)> RefreshCalendarAsync()
    {
        var url = Settings.CalendarUrl;
        if (url.Length == 0)
        {
            CalendarLines = [];
            CalendarChanged?.Invoke();
            Link.Send(Protocol.Cal([]));
            return (true, null, 0);
        }
        var (lines, error) = await CalendarService.FetchAsync(url, _cts.Token);
        if (lines == null)
        {
            Log.Warn($"Kalender: {error}");
            return (false, error, CalendarLines.Count);      // die bisherigen Termine bleiben stehen
        }
        CalendarLines = lines;
        Log.Info($"Kalender: {lines.Count} Termine");
        CalendarChanged?.Invoke();
        Link.Send(Protocol.Cal(lines));
        return (true, null, lines.Count);
    }

    /// <summary>Radio-Player: Senderliste, Wiedergabe, Titel.</summary>
    public RadioService Radio { get; }

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

    private readonly object _volumeGate = new();
    private string _lastVolumeId = "";
    private long _lastVolumeAt;

    /// <summary>
    /// Bei gedrückt gehaltener Taste kommt die Aktion zehnmal pro Sekunde. Wer es langsamer will, stellt "Änderungen pro Sekunde" ein:
    /// Aktionen, die schneller hintereinander kommen, werden übersprungen (mit etwas Spielraum für das Zeitraster des Geräts).
    /// </summary>
    private bool VolumeAllowed(string id)
    {
        var gap = 1000 / Math.Clamp(Settings.VolumeRate, 1, 10) - 20;
        var now = Environment.TickCount64;
        lock (_volumeGate)
        {
            if (id == _lastVolumeId && now - _lastVolumeAt < gap) return false;
            _lastVolumeId = id;
            _lastVolumeAt = now;
            return true;
        }
    }

    /// <summary>Führt die Aktion mit dieser Kennung aus (eingebaut oder selbst definiert). Liefert false bei Fehlern.</summary>
    public bool Run(string id)
    {
        Log.Info($"Aktion: {id}");
        try
        {
            switch (id)
            {
                case "volume.up": if (VolumeAllowed(id)) Platform.AdjustVolume(+Settings.VolumeStep); return true;
                case "volume.down": if (VolumeAllowed(id)) Platform.AdjustVolume(-Settings.VolumeStep); return true;
                case "mute.toggle": Platform.ToggleMute(); return true;
                case "mic.toggle": Platform.ToggleMicMute(); return true;
                case "lock": Platform.LockWorkstation(); return true;
                case "radio.toggle": Radio.Toggle(); return true;
                case "radio.next": Radio.Next(); return true;
                case "radio.prev": Radio.Previous(); return true;
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
        _cts.Cancel();
        Radio.Dispose();
        Link.Dispose();
        Platform.Dispose();
    }
}
