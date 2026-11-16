namespace OverBoardAgent.Platform;

/// <summary>
/// Alles, was vom Betriebssystem abhängt. Windows ist die erste Umsetzung, macOS folgt später
/// (erst wenn Windows vollständig ist).
/// </summary>
public interface IPlatform : IDisposable
{
    /// <summary>Ein Zustand hat sich geändert (id wie im Protokoll: volume, mute, mic_mute; Wert als Text).</summary>
    event Action<string, string>? StateChanged;

    /// <summary>Der PC ist weg (Ruhezustand, gesperrt, abgemeldet, wird heruntergefahren) oder wieder da.</summary>
    event Action<bool>? AwayChanged;

    /// <summary>Der PC ist gerade weg.</summary>
    bool IsAway { get; }

    /// <summary>Alle aktuellen Zustände.</summary>
    IReadOnlyDictionary<string, string> GetStates();

    void AdjustVolume(int deltaPercent);
    void ToggleMute();
    void ToggleMicMute();
    void LockWorkstation();

    /// <summary>Systembefehl: screen.off, sleep, restart, shutdown. Liefert false, wenn unbekannt oder fehlgeschlagen.</summary>
    bool SystemCommand(string command);

    /// <summary>Programm, Datei, Ordner oder Adresse öffnen.</summary>
    void OpenTarget(string target, string arguments);

    /// <summary>Tastenkombination senden (z. B. "Win+Shift+S"). Liefert false, wenn sie unbekannt ist oder nicht gesendet wurde.</summary>
    bool SendKeys(string combo);

    /// <summary>Agent beim Anmelden automatisch starten.</summary>
    bool IsAutostartEnabled();
    void SetAutostart(bool enabled);
}
