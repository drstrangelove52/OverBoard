using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using Microsoft.Win32;
using NAudio.CoreAudioApi.Interfaces;
using OverBoardAgent.Core;

namespace OverBoardAgent.Platform;

/// <summary>Windows: Lautstärke und Stumm über Core Audio (NAudio), PC sperren über user32.</summary>
public sealed class WindowsPlatform : IPlatform
{
    [DllImport("user32.dll")]
    private static extern bool LockWorkStation();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly object _gate = new();
    private readonly Timer _watch;
    private MMDevice? _render;
    private MMDevice? _capture;
    private string? _renderId;
    private string? _captureId;

    // Zwischenspeicher der Zustände: GetStates() greift nie auf die Audio-Objekte zu (COM-Aufrufe aus der Oberfläche
    // stauten sich sonst hinter Aktionen und liessen Stumm und Anzeige hinterherhinken)
    private readonly Dictionary<string, string> _cache = [];
    private readonly object _cacheGate = new();

    /// <summary>Meldet eine Zustandsänderung an alle Empfänger einzeln: eine Ausnahme in einem stoppt die anderen nicht.</summary>
    private void Raise(string id, string value)
    {
        var handlers = StateChanged;
        if (handlers == null) return;
        foreach (var h in handlers.GetInvocationList().Cast<Action<string, string>>())
        {
            try { h(id, value); }
            catch (Exception ex) { Log.Warn($"Zustandsmeldung '{id}': {ex.Message}"); }
        }
    }

    private void SetCache(string id, string value)
    {
        lock (_cacheGate) _cache[id] = value;
    }

    /// <summary>Liest die Zustände einmal von den Audio-Objekten (nur von Attach aufgerufen, nie von der Oberfläche).</summary>
    private void ReadCache()
    {
        lock (_cacheGate)
        {
            _cache.Clear();
            try
            {
                if (_render != null)
                {
                    _cache["volume"] = ((int)Math.Round(_render.AudioEndpointVolume.MasterVolumeLevelScalar * 100)).ToString();
                    _cache["mute"] = _render.AudioEndpointVolume.Mute ? "on" : "off";
                }
                if (_capture != null) _cache["mic_mute"] = _capture.AudioEndpointVolume.Mute ? "on" : "off";
            }
            catch (Exception ex)
            {
                Log.Warn($"Zustand lesen: {ex.Message}");
            }
        }
    }

    public event Action<string, string>? StateChanged;
    public event Action<bool>? AwayChanged;

    private volatile bool _away;
    public bool IsAway => _away;

    private void SetAway(bool away)
    {
        if (_away == away) return;
        _away = away;
        Log.Info(away ? "PC weg (Ruhezustand, gesperrt oder wird beendet)" : "PC wieder da");
        var handlers = AwayChanged;
        if (handlers == null) return;
        foreach (var h in handlers.GetInvocationList().Cast<Action<bool>>())
        {
            try { h(away); }
            catch (Exception ex) { Log.Warn($"Anwesenheitsmeldung: {ex.Message}"); }
        }
    }

    private void OnPowerMode(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend) SetAway(true);
        else if (e.Mode == PowerModes.Resume) SetAway(false);
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        switch (e.Reason)
        {
            case SessionSwitchReason.SessionLock:
            case SessionSwitchReason.SessionLogoff:
            case SessionSwitchReason.ConsoleDisconnect:
            case SessionSwitchReason.RemoteDisconnect:
                SetAway(true);
                break;
            case SessionSwitchReason.SessionUnlock:
            case SessionSwitchReason.SessionLogon:
            case SessionSwitchReason.ConsoleConnect:
            case SessionSwitchReason.RemoteConnect:
                SetAway(false);
                break;
        }
    }

    private void OnSessionEnding(object? sender, SessionEndingEventArgs e) => SetAway(true);   // Herunterfahren oder Abmelden

    public WindowsPlatform()
    {
        Attach();
        SystemEvents.PowerModeChanged += OnPowerMode;
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.SessionEnding += OnSessionEnding;
        // Standardgeräte können wechseln (Kopfhörer, Headset): alle 2 s prüfen und neu anbinden
        _watch = new Timer(_ => CheckDevices(), null, 2000, 2000);
    }

    private void Attach()
    {
        lock (_gate)
        {
            Detach();
            try
            {
                _render = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                _renderId = _render.ID;
                _render.AudioEndpointVolume.OnVolumeNotification += OnRenderChanged;
            }
            catch (Exception ex)
            {
                _render = null;
                _renderId = null;
                Log.Warn($"Kein Wiedergabegerät: {ex.Message}");
            }
            try
            {
                _capture = _enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
                _captureId = _capture.ID;
                _capture.AudioEndpointVolume.OnVolumeNotification += OnCaptureChanged;
            }
            catch (Exception ex)
            {
                _capture = null;
                _captureId = null;
                Log.Info($"Kein Mikrofon: {ex.Message}");
            }
            ReadCache();
        }
    }

    private void Detach()
    {
        try { if (_render != null) _render.AudioEndpointVolume.OnVolumeNotification -= OnRenderChanged; } catch { /* Gerät weg */ }
        try { if (_capture != null) _capture.AudioEndpointVolume.OnVolumeNotification -= OnCaptureChanged; } catch { /* Gerät weg */ }
        _render?.Dispose();
        _capture?.Dispose();
        _render = null;
        _capture = null;
    }

    private void CheckDevices()
    {
        try
        {
            string? r = null, c = null;
            try { r = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID; } catch { /* keins */ }
            try { c = _enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications).ID; } catch { /* keins */ }
            if (r == _renderId && c == _captureId) return;
            Log.Info("Standard-Audiogerät gewechselt");
            Attach();
            foreach (var (id, value) in GetStates()) Raise(id, value);
        }
        catch (Exception ex)
        {
            Log.Warn($"Geräteprüfung: {ex.Message}");
        }
    }

    private void OnRenderChanged(AudioVolumeNotificationData d)
    {
        var volume = ((int)Math.Round(d.MasterVolume * 100)).ToString();
        var mute = d.Muted ? "on" : "off";
        SetCache("volume", volume);
        SetCache("mute", mute);
        Raise("volume", volume);
        Raise("mute", mute);
    }

    private void OnCaptureChanged(AudioVolumeNotificationData d)
    {
        var mute = d.Muted ? "on" : "off";
        SetCache("mic_mute", mute);
        Raise("mic_mute", mute);
    }

    public IReadOnlyDictionary<string, string> GetStates()
    {
        lock (_cacheGate) return new Dictionary<string, string>(_cache);
    }

    public void AdjustVolume(int deltaPercent)
    {
        lock (_gate)
        {
            if (_render == null) return;
            var volume = _render.AudioEndpointVolume;
            volume.MasterVolumeLevelScalar = Math.Clamp(volume.MasterVolumeLevelScalar + deltaPercent / 100f, 0f, 1f);
            if (deltaPercent > 0 && volume.Mute) volume.Mute = false;   // lauter heisst: wieder hörbar
        }
    }

    public void ToggleMute()
    {
        lock (_gate)
        {
            if (_render != null) _render.AudioEndpointVolume.Mute = !_render.AudioEndpointVolume.Mute;
        }
    }

    public void ToggleMicMute()
    {
        lock (_gate)
        {
            if (_capture != null) _capture.AudioEndpointVolume.Mute = !_capture.AudioEndpointVolume.Mute;
        }
    }

    public void LockWorkstation()
    {
        if (!LockWorkStation()) Log.Warn("PC sperren fehlgeschlagen");
    }

    public bool SystemCommand(string command)
    {
        switch (command)
        {
            case "screen.off":
                SendMessage(new IntPtr(0xFFFF), 0x0112, new IntPtr(0xF170), new IntPtr(2));   // WM_SYSCOMMAND, SC_MONITORPOWER, aus
                return true;
            case "sleep":
                Process.Start(new ProcessStartInfo("rundll32.exe", "powrprof.dll,SetSuspendState 0,1,0") { CreateNoWindow = true, UseShellExecute = false });
                return true;
            case "restart":
                Process.Start(new ProcessStartInfo("shutdown.exe", "/r /t 0") { CreateNoWindow = true, UseShellExecute = false });
                return true;
            case "shutdown":
                Process.Start(new ProcessStartInfo("shutdown.exe", "/s /t 0") { CreateNoWindow = true, UseShellExecute = false });
                return true;
            default:
                Log.Warn($"Unbekannter Systembefehl '{command}'");
                return false;
        }
    }

    public void OpenTarget(string target, string arguments)
    {
        // UseShellExecute: wie "Ausfuehren" in Windows, startet Programme, oeffnet Dateien, Ordner und Adressen
        target = target.Trim().Trim('"');   // Pfade aus der Zwischenablage haben oft Anführungszeichen
        Process.Start(new ProcessStartInfo(target) { Arguments = arguments, UseShellExecute = true })?.Dispose();
        Log.Info($"Gestartet: {target}");
    }

    public bool SendKeys(string combo) => KeySender.Send(combo);

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "OverBoardAgent";

    public bool IsAutostartEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(RunValue) is string;
    }

    public void SetAutostart(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
        {
            var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Programmpfad unbekannt");
            key.SetValue(RunValue, "\"" + exe + "\" --tray");
        }
        else
        {
            key.DeleteValue(RunValue, throwOnMissingValue: false);
        }
    }

    public void Dispose()
    {
        SystemEvents.PowerModeChanged -= OnPowerMode;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.SessionEnding -= OnSessionEnding;
        _watch.Dispose();
        lock (_gate) Detach();
        _enumerator.Dispose();
    }
}
