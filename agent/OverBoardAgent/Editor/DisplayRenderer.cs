using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;

namespace OverBoardAgent.Editor;

/// <summary>Farbrollen eines Designs, identisch zu overboard/main/theme.c der Firmware.</summary>
public sealed record ThemeColors(string Name, Color Bg, Color Tile, Color TileOn, Color Title, Color Value,
    Color Accent, Color OnAccent, Color Ok, Color Bad, Color Nav)
{
    private static Color C(uint rgb) => Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    public static readonly ThemeColors Dark = new("dark", C(0x0B0F14), C(0x161C24), C(0x4A3B00), C(0x8A97A6), C(0xF2F5F8),
        C(0xFFC53D), C(0x000000), C(0x3DDC84), C(0xE5484D), C(0x1F2733));

    public static readonly ThemeColors Light = new("light", C(0xF2F4F7), C(0xFFFFFF), C(0xFFE9A6), C(0x5B6672), C(0x111418),
        C(0xC98A00), C(0x000000), C(0x1F9D55), C(0xD13438), C(0xE3E8EE));

    public static ThemeColors Get(string name) => name == "light" ? Light : Dark;
}

/// <summary>Was der Editor zusätzlich zur reinen Display-Ansicht zeigt.</summary>
public sealed class PreviewOptions
{
    public int Page;
    public TileModel? Selected;
    public HashSet<TileModel> On { get; } = [];          // Kacheln, die in der Vorschau "eingeschaltet" aussehen
    public bool ShowGrid = true;                          // gestrichelte Rasterzellen
    /// <summary>Echte Zustände (Quelle → Wert, z. B. aus Home Assistant und vom PC). Null = Beispielwerte zeigen.</summary>
    public IReadOnlyDictionary<string, string>? States;
    /// <summary>Echte Termine für Kalender-Kacheln. Null = Beispieltermine.</summary>
    public IReadOnlyList<OverBoardAgent.Core.CalLine>? Calendar;
    /// <summary>Radio-Kacheln: Sender, Titel, ob es spielt. Null = Beispiel.</summary>
    public (string Station, string Title, bool Playing)? Radio;
    public Rect? Ghost;                                   // Zielposition beim Ziehen
    public bool GhostValid = true;
}

/// <summary>
/// Zeichnet das Display (1024 × 600) so wie die Firmware: gleiche Masse, Farben, Schrift (Montserrat) und Symbole
/// (Material Design Icons). Die Geometrie entspricht overboard/main/ui.c.
/// </summary>
public static class DisplayRenderer
{
    public const int ScreenW = 1024, ScreenH = 600;
    public const int Gap = 10, CellW = 114, Cols = 8, Pad = 12;
    public const int GridX = (ScreenW - (Cols * CellW + (Cols - 1) * Gap)) / 2;      // 21
    public const int GridY = 68;
    public const int GridW = Cols * CellW + (Cols - 1) * Gap;                       // 982
    public const int GridH = 4 * 106 + 3 * Gap;                                     // 454
    public const int NavY = GridY + GridH + 10;                                     // 532
    public const int NavH = 56;

    private static readonly FontFamily TextFont = new("avares://OverBoardAgent/Assets/Fonts#Montserrat Medium");
    private static readonly FontFamily IconFont = new("avares://OverBoardAgent/Assets/Fonts#Material Design Icons");
    private static readonly Color SelectionBlue = Color.Parse("#3B82F6");

    private static readonly string[] WeekDe = ["Sonntag", "Montag", "Dienstag", "Mittwoch", "Donnerstag", "Freitag", "Samstag"];
    private static readonly string[] WeekEn = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];
    private static readonly string[] MonthDe = ["Januar", "Februar", "März", "April", "Mai", "Juni", "Juli", "August", "September", "Oktober", "November", "Dezember"];
    private static readonly string[] MonthEn = ["January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December"];

    // ---- Geometrie ------------------------------------------------------------------------------

    public const int GridHFull = ScreenH - GridY - 12;      // ohne Navigationsleiste bis zum unteren Rand
    /// <summary>Höhe des Rasters der gerade gezeichneten Konfiguration (mit mehreren Seiten kürzer wegen der Navigationsleiste).</summary>
    public static int CurrentGridH { get; private set; } = GridH;

    public static int CellH(int rows)
    {
        rows = Math.Clamp(rows, 1, ConfigValidator.MaxRows);
        return (CurrentGridH - (rows - 1) * Gap) / rows;       // ganzzahlig wie in der Firmware
    }

    public static Rect TileRect(PageModel page, TileModel t)
    {
        var cellH = CellH(page.Rows);
        return new Rect(GridX + t.X * (CellW + Gap), GridY + t.Y * (cellH + Gap),
            t.W * CellW + (t.W - 1) * Gap, t.H * cellH + (t.H - 1) * Gap);
    }

    /// <summary>Rasterzelle (Spalte, Reihe) unter einem Punkt des Displays; ausserhalb wird auf den Rand begrenzt.</summary>
    public static (int Col, int Row) CellAt(PageModel page, Point p)
    {
        var cellH = CellH(page.Rows);
        var col = (int)Math.Floor((p.X - GridX) / (CellW + Gap));
        var row = (int)Math.Floor((p.Y - GridY) / (cellH + Gap));
        return (Math.Clamp(col, 0, Cols - 1), Math.Clamp(row, 0, Math.Clamp(page.Rows, 1, ConfigValidator.MaxRows) - 1));
    }

    public static Rect HandleRect(Rect tile) => new(tile.Right - 24, tile.Bottom - 24, 24, 24);

    public static Rect GridArea => new(GridX, GridY, GridW, CurrentGridH);

    // ---- Text ----------------------------------------------------------------------------------

    private static Size Measure(string text, FontFamily family, double size)
    {
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(family), size, Brushes.White);
        return new Size(ft.Width, ft.Height);
    }

    private static TextBlock Label(string text, FontFamily family, double size, Color color)
    {
        return new TextBlock
        {
            Text = text,
            FontFamily = family,
            FontSize = size,
            Foreground = new SolidColorBrush(color),
            TextWrapping = TextWrapping.NoWrap,
        };
    }

    private static void Put(Canvas canvas, Control c, double x, double y)
    {
        Canvas.SetLeft(c, x);
        Canvas.SetTop(c, y);
        canvas.Children.Add(c);
    }

    /// <summary>Platziert einen Text in einem Rahmen, Position wie von LVGL ausgerichtet (Mass aus der Textmessung).</summary>
    private static Size PutText(Canvas canvas, string text, FontFamily family, double size, Color color, Func<Size, Point> position)
    {
        var s = Measure(text, family, size);
        var p = position(s);
        Put(canvas, Label(text, family, size, color), p.X, p.Y);
        return s;
    }

    // Beispielwerte für Anzeige-Kacheln (der Agent kennt die echten Werte aus Home Assistant nicht)
    private static string Sample(TileModel t, string language, bool on) => t.EffectiveFormat switch
    {
        "power" => "1.52 kW",
        "kwh" => "12.5 kWh",
        "status" => language == "en" ? "Ready" : "Bereit",
        "onoff" => on ? (language == "en" ? "On" : "An") : (language == "en" ? "Off" : "Aus"),
        "percent" => "42 %",
        "time" => "21:39",
        _ => "Text",
    };

    /// <summary>Wie FitFont, aber für umbrochenen Text (Radio-Titel): größte Schrift, in der der Text in die Fläche passt.</summary>
    private static double FitWrapped(string text, double maxW, double maxH)
    {
        foreach (var size in new[] { 48.0, 36, 28, 20 })
        {
            var tb = Label(text, TextFont, size, Colors.White);
            tb.TextWrapping = TextWrapping.Wrap;
            tb.Measure(new Size(Math.Max(10, maxW), double.PositiveInfinity));
            if (tb.DesiredSize.Height <= maxH) return size;
        }
        return 20;
    }

    private static double FitFont(string text, double maxW, double maxH)
    {
        foreach (var size in new[] { 64.0, 48, 36, 28, 20 })
        {
            var m = Measure(text, TextFont, size);
            if (m.Width <= maxW && m.Height <= maxH) return size;
        }
        return 20;
    }

    // ---- Aufbau --------------------------------------------------------------------------------

    public static Canvas Build(ConfigModel cfg, PreviewOptions options)
    {
        CurrentGridH = cfg.Pages.Count > 1 ? GridH : GridHFull;
        var th = ThemeColors.Get(cfg.Theme);
        var root = new Canvas { Width = ScreenW, Height = ScreenH, Background = new SolidColorBrush(th.Bg), ClipToBounds = true };
        AddHeader(root, th, cfg.Language);
        if (cfg.Pages.Count == 0) return root;

        var pageIndex = Math.Clamp(options.Page, 0, cfg.Pages.Count - 1);
        var page = cfg.Pages[pageIndex];
        if (options.ShowGrid) AddGuides(root, page);
        foreach (var tile in page.Tiles) AddTile(root, page, tile, th, cfg.Language, options.On.Contains(tile), options.States, options.Calendar, options.Radio);
        if (cfg.Pages.Count > 1) AddNav(root, cfg, pageIndex, th);

        if (options.Selected != null && page.Tiles.Contains(options.Selected)) AddSelection(root, TileRect(page, options.Selected));
        if (options.Ghost is { } ghost) AddGhost(root, ghost, options.GhostValid);
        return root;
    }

    private static void AddHeader(Canvas root, ThemeColors th, string language)
    {
        var now = DateTime.Now;
        var clock = now.ToString("HH:mm", CultureInfo.InvariantCulture);
        var week = (language == "en" ? WeekEn : WeekDe)[(int)now.DayOfWeek];
        var month = (language == "en" ? MonthEn : MonthDe)[now.Month - 1];
        var date = language == "en" ? $"{week}, {month} {now.Day}, {now.Year}" : $"{week}, {now.Day}. {month} {now.Year}";

        const double headTop = 12, headH = 46;
        var cs = PutText(root, clock, TextFont, 36, th.Value, s => new Point(GridX, headTop + (headH - s.Height) / 2));
        PutText(root, date, TextFont, 20, th.Title, s => new Point(GridX + cs.Width + 18, headTop + (headH - s.Height) / 2));

        // Verbindungssymbole rechts (von links nach rechts: PC, MQTT, HA, WLAN), hier alle "verbunden"
        double x = GridX + GridW;
        foreach (var name in new[] { "wifi", "home-assistant", "access-point-network", "monitor" })
        {
            var g = MdiIcons.Glyph(name);
            var s = Measure(g, IconFont, 28);
            x -= s.Width;
            Put(root, Label(g, IconFont, 28, th.Ok), x, headTop + (headH - s.Height) / 2);
            x -= 20;
        }
    }

    private static void AddGuides(Canvas root, PageModel page)
    {
        var cellH = CellH(page.Rows);
        var rows = Math.Clamp(page.Rows, 1, ConfigValidator.MaxRows);
        for (var r = 0; r < rows; r++)
            for (var c = 0; c < Cols; c++)
            {
                var rect = new Rectangle
                {
                    Width = CellW, Height = cellH, RadiusX = 8, RadiusY = 8,
                    Stroke = new SolidColorBrush(Color.FromArgb(120, 140, 155, 180)), StrokeThickness = 1.5,
                    StrokeDashArray = [4, 4], IsHitTestVisible = false,
                };
                Put(root, rect, GridX + c * (CellW + Gap), GridY + r * (cellH + Gap));
            }
    }

    private static void AddTile(Canvas root, PageModel page, TileModel t, ThemeColors th, string language, bool on,
        IReadOnlyDictionary<string, string>? states, IReadOnlyList<OverBoardAgent.Core.CalLine>? calendar, (string Station, string Title, bool Playing)? radio)
    {
        var rect = TileRect(page, t);
        // Echter Zustand, wenn bekannt; sonst (ohne Quelle oder ohne Verbindung) Beispielwerte
        string? liveState = null;
        var hasLive = false;
        if (states != null && !string.IsNullOrEmpty(t.Source))
        {
            hasLive = true;
            states.TryGetValue(t.Source, out liveState);
        }
        var isOn = on || (hasLive && liveState == "on");
        var showOn = isOn && (t.IsButton || t.EffectiveFormat == "onoff");
        var body = new Border
        {
            Width = rect.Width, Height = rect.Height, CornerRadius = new CornerRadius(16), IsHitTestVisible = false,
            Background = new SolidColorBrush(showOn ? th.TileOn : th.Tile),
        };
        var inner = new Canvas { Width = rect.Width, Height = rect.Height };
        body.Child = inner;
        Put(root, body, rect.X, rect.Y);

        var w = rect.Width;
        var h = rect.Height;
        var title = t.Title.Get(language);

        if (t.IsCalendar)
        {
            AddCalendarTile(inner, t, th, language, w, h, title, calendar);
        }
        else if (t.IsRadio)
        {
            AddRadioTile(inner, t, th, w, h, title, radio);
        }
        else if (!t.IsButton)
        {
            var narrow = w < 150;
            var titleSize = title.Length > 0 ? PutText(inner, title, TextFont, narrow ? 14 : 20, th.Title, _ => new Point(Pad, Pad))
                                             : Measure("Ag", TextFont, narrow ? 14 : 20);
            if (t.Icon != null)
                PutText(inner, MdiIcons.Glyph(t.Icon), IconFont, narrow ? 24 : 28, th.Title, s => new Point(w - Pad - s.Width, Pad));
            var sample = hasLive ? ValueFormatter.Format(t, liveState, language) : Sample(t, language, showOn);
            var size = FitFont(sample, w - 2 * Pad, h - 2 * Pad - (Math.Round(titleSize.Height) + (narrow ? 5 : 8)));
            PutText(inner, sample, TextFont, size, showOn ? th.Accent : th.Value, s => new Point(Pad, h - Pad - s.Height));
        }
        else
        {
            var big = t.W >= 2 && t.H >= 2;
            PutText(inner, MdiIcons.Glyph(t.Icon ?? "help-circle"), IconFont, big ? 64 : 48, showOn ? th.Accent : th.Value,
                s => new Point((w - s.Width) / 2, h / 2 - s.Height / 2 + (big ? -18 : -14)));
            if (title.Length > 0)
            {
                var label = Label(title, TextFont, big ? 28 : 14, th.Title);
                label.Width = w - 2 * Pad;
                label.TextAlignment = TextAlignment.Center;
                label.TextTrimming = TextTrimming.CharacterEllipsis;
                var s = Measure(title, TextFont, big ? 28 : 14);
                Put(inner, label, Pad, h - Pad - s.Height);
            }
            if (t.EffectiveTrigger == "hold")      // Fortschrittsbalken beim Halten, oben in der Kachel
            {
                Put(inner, new Rectangle { Width = w - 2 * Pad, Height = 8, RadiusX = 4, RadiusY = 4, Fill = new SolidColorBrush(th.Nav) }, Pad, Pad);
            }
        }
    }

    /// <summary>Radio-Kachel: links das Logo (hier als Symbol), rechts Sender und Titel, unten rechts Start/Stopp. Masse wie in ui.c.</summary>
    private static void AddRadioTile(Canvas inner, TileModel t, ThemeColors th, double w, double h, string title, (string Station, string Title, bool Playing)? radio)
    {
        var (station, song, playing) = radio ?? ("Radio BOB!", "Mötley Crüe - Kickstart My Heart", true);
        if (station.Length == 0) station = title.Length > 0 ? title : "Radio";
        var logo = Math.Min(Math.Min(h - 2 * Pad, 192), w / 2);
        double x0 = Pad;
        if (logo >= 48)
        {
            var box = new Border { Width = logo, Height = logo, CornerRadius = new CornerRadius(8), Background = new SolidColorBrush(th.Nav) };
            Put(inner, box, Pad, Pad);
            var iconSize = logo >= 100 ? 64 : 36;
            PutText(inner, MdiIcons.Glyph("radio"), IconFont, iconSize, th.Title, s => new Point(Pad + (logo - s.Width) / 2, Pad + (logo - s.Height) / 2));
            x0 = Pad + logo + 16;
        }
        var avail = w - x0 - Pad;
        PutText(inner, station, TextFont, 20, th.Title, _ => new Point(x0, Pad));
        var songText = song.Length > 0 ? song : (playing ? "..." : "");
        var availH = Math.Max(28, h - 2 * Pad - 30 - 40);
        var fontSize = FitWrapped(songText, avail, availH);
        var label = Label(songText, TextFont, fontSize, th.Value);
        label.Width = Math.Max(10, avail);
        label.TextWrapping = TextWrapping.Wrap;
        label.MaxHeight = availH;
        Put(inner, label, x0, Pad + 30);
        PutText(inner, MdiIcons.Glyph(playing ? "stop-circle-outline" : "play-circle-outline"), IconFont, 36,
            playing ? th.Accent : th.Title, s => new Point(w - Pad - s.Width, h - Pad - s.Height));
    }

    private const double CalRowH = 30;     // gleich wie in der Firmware (ui.c)

    private static void AddCalendarTile(Canvas inner, TileModel t, ThemeColors th, string language, double w, double h, string title,
        IReadOnlyList<OverBoardAgent.Core.CalLine>? calendar)
    {
        var narrow = w < 150;
        var titleSize = title.Length > 0 ? PutText(inner, title, TextFont, narrow ? 14 : 20, th.Title, _ => new Point(Pad, Pad))
                                         : Measure("Ag", TextFont, narrow ? 14 : 20);
        if (t.Icon != null)
            PutText(inner, MdiIcons.Glyph(t.Icon), IconFont, narrow ? 24 : 28, th.Title, s => new Point(w - Pad - s.Width, Pad));

        var top = Pad + Math.Round(titleSize.Height) + 8;
        var rows = Math.Max(1, (int)((h - Pad - top) / CalRowH));
        var today = DateTime.Today;
        var lines = calendar ?? SampleCalendar(today);
        var whens = lines.Take(rows).Select(l => OverBoardAgent.Core.CalendarService.When(l, language, today)).ToList();
        if (lines.Count == 0)
        {
            PutText(inner, language == "en" ? "No events" : "Keine Termine", TextFont, 20, th.Title, _ => new Point(Pad, top));
            return;
        }
        var whenW = whens.Select(x => Measure(x, TextFont, 20).Width).DefaultIfEmpty(0).Max();
        for (var i = 0; i < whens.Count; i++)
        {
            var y = top + i * CalRowH;
            PutText(inner, whens[i], TextFont, 20, th.Accent, _ => new Point(Pad, y));
            var label = Label(lines[i].Title, TextFont, 20, th.Value);
            label.Width = Math.Max(10, w - 2 * Pad - whenW - 14);
            label.TextTrimming = TextTrimming.CharacterEllipsis;
            Put(inner, label, Pad + whenW + 14, y);
        }
    }

    private static List<OverBoardAgent.Core.CalLine> SampleCalendar(DateTime today) =>
    [
        new(today.ToString("yyyy-MM-dd"), "18:30", "Elternabend"),
        new(today.AddDays(1).ToString("yyyy-MM-dd"), "", "Geburtstag Oma"),
        new(today.AddDays(2).ToString("yyyy-MM-dd"), "09:00", "Zahnarzt"),
        new(today.AddDays(4).ToString("yyyy-MM-dd"), "19:00", "Training"),
        new(today.AddDays(6).ToString("yyyy-MM-dd"), "", "Müllabfuhr"),
    ];

    private static void AddNav(Canvas root, ConfigModel cfg, int active, ThemeColors th)
    {
        var n = cfg.Pages.Count;
        var total = n * 220 + (n - 1) * 16;
        var x = GridX + (GridW - total) / 2.0;
        for (var i = 0; i < n; i++)
        {
            var page = cfg.Pages[i];
            var isActive = i == active;
            var fg = isActive ? th.OnAccent : th.Value;
            var btn = new Border
            {
                Width = 220, Height = NavH, CornerRadius = new CornerRadius(28), IsHitTestVisible = false,
                Background = new SolidColorBrush(isActive ? th.Accent : th.Nav),
            };
            var c = new Canvas { Width = 220, Height = NavH };
            btn.Child = c;
            var icon = MdiIcons.Glyph(page.Icon ?? "help-circle");
            var title = page.Title.Get(cfg.Language);
            var iconSize = Measure(icon, IconFont, 28);
            var titleSize = Measure(title, TextFont, 20);
            var startX = (220 - (iconSize.Width + 10 + titleSize.Width)) / 2;
            Put(c, Label(icon, IconFont, 28, fg), startX, (NavH - iconSize.Height) / 2);
            Put(c, Label(title, TextFont, 20, fg), startX + iconSize.Width + 10, (NavH - titleSize.Height) / 2);
            Put(root, btn, x, NavY);
            x += 220 + 16;
        }
    }

    private static void AddSelection(Canvas root, Rect r)
    {
        var frame = new Border
        {
            Width = r.Width + 6, Height = r.Height + 6, CornerRadius = new CornerRadius(19), IsHitTestVisible = false,
            BorderBrush = new SolidColorBrush(SelectionBlue), BorderThickness = new Thickness(3),
        };
        Put(root, frame, r.X - 3, r.Y - 3);
        var handle = new Border
        {
            Width = 18, Height = 18, CornerRadius = new CornerRadius(4), IsHitTestVisible = false,
            Background = new SolidColorBrush(SelectionBlue),
            BorderBrush = Brushes.White, BorderThickness = new Thickness(2),
        };
        Put(root, handle, r.Right - 15, r.Bottom - 15);
    }

    private static void AddGhost(Canvas root, Rect r, bool valid)
    {
        var color = valid ? SelectionBlue : Color.Parse("#E5484D");
        var ghost = new Rectangle
        {
            Width = r.Width, Height = r.Height, RadiusX = 16, RadiusY = 16, IsHitTestVisible = false,
            Stroke = new SolidColorBrush(color), StrokeThickness = 3, StrokeDashArray = [4, 3],
            Fill = new SolidColorBrush(Color.FromArgb(48, color.R, color.G, color.B)),
        };
        Put(root, ghost, r.X, r.Y);
    }
}
