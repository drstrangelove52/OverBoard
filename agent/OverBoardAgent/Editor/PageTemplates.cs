namespace OverBoardAgent.Editor;

/// <summary>Fertige Seiten als Ausgangspunkt. Sie brauchen nichts Besonderes (nur den PC, das Radio und den Kalender), damit sie überall sofort funktionieren.</summary>
public static class PageTemplates
{
    public sealed record Template(string Id, string De, string En, string DescDe, string DescEn, Func<string, PageModel> Build);

    private static LText Text(string s) => new(s, s);

    private static TileModel Button(int x, int y, string title, string icon, string action, int w = 1, int h = 1, string trigger = "tap") =>
        new() { Type = "button", X = x, Y = y, W = w, H = h, Title = Text(title), Icon = icon, Trigger = trigger, Action = action };

    private static TileModel Value(int x, int y, int w, int h, string title, string source, string format, string? icon = null) =>
        new() { Type = "value", X = x, Y = y, W = w, H = h, Title = Text(title), Source = source, Format = format, Icon = icon };

    public static IReadOnlyList<Template> All { get; } =
    [
        new("pc", "PC-Steuerung", "PC control", "Lautstärke, Stumm, Mikrofon, Medientasten und Sperren.", "Volume, mute, microphone, media keys and lock.", lang =>
        {
            var en = lang == "en";
            return new PageModel
            {
                Title = Text(en ? "PC" : "PC"), Icon = "monitor", Rows = 3,
                Tiles =
                [
                    Value(0, 0, 2, 1, en ? "Volume" : "Lautstärke", "pc:volume", "percent", "volume-high"),
                    Button(2, 0, en ? "Quieter" : "Leiser", "volume-minus", "pc:volume.down", trigger: "repeat"),
                    Button(3, 0, en ? "Louder" : "Lauter", "volume-plus", "pc:volume.up", trigger: "repeat"),
                    Button(4, 0, en ? "Mute" : "Stumm", "volume-mute", "pc:mute.toggle"),
                    Button(5, 0, en ? "Mic" : "Mikro", "microphone-off", "pc:mic.toggle"),
                    Button(2, 1, en ? "Back" : "Zurück", "skip-previous", "pc:media.prev"),
                    Button(3, 1, en ? "Play" : "Start", "play-pause", "pc:media.playpause"),
                    Button(4, 1, en ? "Next" : "Weiter", "skip-next", "pc:media.next"),
                    Button(7, 2, en ? "Lock" : "Sperren", "lock", "pc:lock", trigger: "confirm"),
                ],
            };
        }),
        new("load", "PC-Auslastung", "PC load", "CPU, Arbeitsspeicher und Netzwerk als Linien.", "CPU, memory and network as lines.", lang =>
        {
            var en = lang == "en";
            return new PageModel
            {
                Title = Text(en ? "Load" : "Auslastung"), Icon = "chart-line", Rows = 3,
                Tiles =
                [
                    new TileModel { Type = "chart", X = 0, Y = 0, W = 4, H = 2, Title = Text("CPU"), Icon = "cpu-64-bit", Source = "pc:cpu", Format = "percent", Span = 120 },
                    new TileModel { Type = "chart", X = 4, Y = 0, W = 4, H = 2, Title = Text(en ? "Memory" : "Arbeitsspeicher"), Icon = "memory", Source = "pc:ram", Format = "percent", Span = 120 },
                    new TileModel { Type = "chart", X = 0, Y = 2, W = 4, H = 1, Title = Text(en ? "Download" : "Download"), Icon = "download", Source = "pc:net_down", Format = "mbit", Span = 120 },
                    new TileModel { Type = "chart", X = 4, Y = 2, W = 4, H = 1, Title = Text("Upload"), Icon = "upload", Source = "pc:net_up", Format = "mbit", Span = 120 },
                ],
            };
        }),
        new("radio", "Radio", "Radio", "Radio-Player mit Senderlogo, Titel und Lautstärke.", "Radio player with station logo, song and volume.", lang =>
        {
            var en = lang == "en";
            return new PageModel
            {
                Title = Text("Radio"), Icon = "radio", Rows = 3,
                Tiles =
                [
                    new TileModel { Type = "radio", X = 0, Y = 0, W = 4, H = 2, Title = Text("Radio"), Icon = "radio", Trigger = "tap", Action = "pc:radio.toggle" },
                    Button(4, 0, en ? "Previous" : "Vorheriger", "skip-previous", "pc:radio.prev"),
                    Button(5, 0, en ? "Next" : "Nächster", "skip-next", "pc:radio.next"),
                    Value(6, 0, 2, 1, en ? "Volume" : "Lautstärke", "pc:volume", "percent", "volume-high"),
                    Button(4, 1, en ? "Quieter" : "Leiser", "volume-minus", "pc:volume.down", trigger: "repeat"),
                    Button(5, 1, en ? "Louder" : "Lauter", "volume-plus", "pc:volume.up", trigger: "repeat"),
                    Button(6, 1, en ? "Mute" : "Stumm", "volume-mute", "pc:mute.toggle"),
                ],
            };
        }),
        new("calendar", "Kalender", "Calendar", "Die nächsten Termine, gross und gut lesbar.", "The next events, large and easy to read.", lang =>
            new PageModel
            {
                Title = Text(lang == "en" ? "Calendar" : "Termine"), Icon = "calendar", Rows = 3,
                Tiles =
                [
                    new TileModel { Type = "calendar", X = 0, Y = 0, W = 6, H = 3, Title = Text(lang == "en" ? "Calendar" : "Kalender"), Icon = "calendar" },
                    Value(6, 0, 2, 1, lang == "en" ? "Volume" : "Lautstärke", "pc:volume", "percent", "volume-high"),
                    Button(6, 1, lang == "en" ? "Mute" : "Stumm", "volume-mute", "pc:mute.toggle", w: 2),
                ],
            }),
    ];
}
