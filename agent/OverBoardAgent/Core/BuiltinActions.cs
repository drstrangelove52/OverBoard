namespace OverBoardAgent.Core;

/// <summary>Eine fest eingebaute Aktion: immer vorhanden, wird mit pc:&lt;Kennung&gt; aufgerufen.</summary>
/// <param name="Keys">Tastenkombination, die gesendet wird (oder null).</param>
/// <param name="System">Systembefehl der Plattform (oder null). Beide null: von Hand in AgentCore umgesetzt.</param>
/// <param name="Confirm">Gefährlich (z. B. Herunterfahren): der Editor stellt die Taste auf "Antippen und bestätigen".</param>
public sealed record BuiltinAction(string Id, string De, string En, string? Keys = null, string? System = null, bool Confirm = false);

public static class BuiltinActions
{
    public static readonly IReadOnlyList<BuiltinAction> All =
    [
        // Audio
        new("volume.up", "Lauter", "Volume up"),
        new("volume.down", "Leiser", "Volume down"),
        new("mute.toggle", "Stumm schalten", "Toggle mute"),
        new("mic.toggle", "Mikrofon stumm", "Toggle microphone mute"),
        // Medien
        new("media.playpause", "Medien: Wiedergabe / Pause", "Media: play / pause", Keys: "MediaPlayPause"),
        new("media.next", "Medien: Nächster Titel", "Media: next track", Keys: "MediaNext"),
        new("media.prev", "Medien: Vorheriger Titel", "Media: previous track", Keys: "MediaPrev"),
        new("media.stop", "Medien: Stopp", "Media: stop", Keys: "MediaStop"),
        // Radio (Radio-Player im Programm)
        new("radio.toggle", "Radio: Start / Stopp", "Radio: play / stop"),
        new("radio.next", "Radio: nächster Sender", "Radio: next station"),
        new("radio.prev", "Radio: vorheriger Sender", "Radio: previous station"),
        // Fenster
        new("desktop.show", "Desktop anzeigen", "Show desktop", Keys: "Win+D"),
        new("taskview", "Aufgabenansicht", "Task view", Keys: "Win+Tab"),
        new("app.switch", "Programm wechseln", "Switch app", Keys: "Alt+Tab"),
        new("window.minimize", "Fenster minimieren", "Minimise window", Keys: "Win+Down"),
        new("window.close", "Fenster schliessen", "Close window", Keys: "Alt+F4", Confirm: true),
        new("taskmgr", "Task-Manager", "Task manager", Keys: "Ctrl+Shift+Esc"),
        // Bearbeiten
        new("edit.copy", "Kopieren", "Copy", Keys: "Ctrl+C"),
        new("edit.paste", "Einfügen", "Paste", Keys: "Ctrl+V"),
        new("edit.undo", "Rückgängig", "Undo", Keys: "Ctrl+Z"),
        new("edit.selectall", "Alles auswählen", "Select all", Keys: "Ctrl+A"),
        // System
        new("lock", "PC sperren", "Lock PC"),
        new("screen.off", "Bildschirm ausschalten", "Turn screen off", System: "screen.off"),
        new("sleep", "Ruhezustand", "Sleep", System: "sleep", Confirm: true),
        new("restart", "Neu starten", "Restart", System: "restart", Confirm: true),
        new("shutdown", "Herunterfahren", "Shut down", System: "shutdown", Confirm: true),
    ];

    public static BuiltinAction? Find(string id) => All.FirstOrDefault(a => a.Id == id);
    public static bool IsBuiltin(string id) => Find(id) != null;
}
