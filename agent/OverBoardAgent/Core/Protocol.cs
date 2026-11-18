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

    /// <summary>Radio: Sender, laufender Titel und ob es spielt.</summary>
    public static string Radio(string station, string title, bool on)
    {
        var options = new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        // Titel kürzen, damit die Zeile (höchstens 1024 Byte) sicher passt
        static string Cut(string s, int max) => s.Length <= max ? s : s[..max];
        return JsonSerializer.Serialize(new { t = "radio", st = Cut(station, 60), ti = Cut(title, 140), on }, options);
    }

    /// <summary>Senderlogo in Stücken (RGB565, Base64); das Gerät setzt das Bild zusammen, sobald es vollständig ist.</summary>
    public static IEnumerable<string> RadioImage(byte[] rgb565, int size)
    {
        const int chunk = 600;
        for (var off = 0; off < rgb565.Length; off += chunk)
        {
            var n = Math.Min(chunk, rgb565.Length - off);
            yield return JsonSerializer.Serialize(new { t = "img", k = "radio", w = size, h = size, o = off, d = Convert.ToBase64String(rgb565, off, n) });
        }
    }

    /// <summary>Kein Logo: das Gerät zeigt dann sein Standardsymbol.</summary>
    public static string RadioImageClear() => JsonSerializer.Serialize(new { t = "img", k = "radio", w = 0, h = 0, o = 0, d = "" });

    /// <summary>Das Gerät soll diese Seite zeigen (ab 0). Der Editor hält es so auf der Seite, die er gerade bearbeitet.</summary>
    public static string Page(int index, bool click = false) => JsonSerializer.Serialize(new { t = "page", i = index, c = click });

    /// <summary>Vorführmodus: das Gerät soll diese Kachel (Seite, Nummer auf der Seite, ab 0) wie bei einer Berührung auslösen.</summary>
    public static string Press(int page, int tile) => JsonSerializer.Serialize(new { t = "press", p = page, i = tile });

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

/// <summary>Verbindungen des Geräts: WLAN, Home Assistant, MQTT.</summary>
public sealed record DeviceStatus(bool Wifi, bool Ha, bool Mqtt);
