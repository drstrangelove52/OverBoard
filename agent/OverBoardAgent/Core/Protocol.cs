using System.Text.Json;

namespace OverBoardAgent.Core;

/// <summary>Nachrichtenformat zwischen Gerät und PC (siehe docs/protocol.md im Firmware-Repo, Protokoll v1).</summary>
public static class Protocol
{
    public const int Version = 1;

    public static string Hello(string pcName, string appVersion) =>
        JsonSerializer.Serialize(new { t = "hello", v = Version, pc = pcName, app = appVersion });

    /// <summary>Zustand des PCs für das Gerät: weg (Ruhezustand, gesperrt, wird beendet) oder da.</summary>
    public static string Pc(bool away) => JsonSerializer.Serialize(new { t = "pc", away });

    /// <summary>Die nächsten Kalendertermine fürs Gerät (Tag, Uhrzeit, Titel).</summary>
    public static string Cal(IReadOnlyList<CalLine> lines) => CalendarService.ToMessage(lines);

    public static string Ping() => "{\"t\":\"ping\"}";

    public static string State(string id, string value) =>
        JsonSerializer.Serialize(new { t = "state", id, v = value });
}

/// <summary>Daten, die das Gerät in seinem hello mitschickt.</summary>
public sealed record DeviceInfo(string Device, string Firmware, int Width, int Height, string Language, int Proto = 1)
{
    /// <summary>Firmware und Programm sprechen dieselbe Protokollversion.</summary>
    public bool ProtocolMatches => Proto == Protocol.Version;
}
