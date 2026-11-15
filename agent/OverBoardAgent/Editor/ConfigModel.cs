using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OverBoardAgent.Editor;

/// <summary>Text in Deutsch und Englisch. Im JSON ein einfacher Text (gleich in beiden Sprachen) oder {"de": ..., "en": ...}.</summary>
[JsonConverter(typeof(LTextConverter))]
public sealed class LText
{
    public string De { get; set; } = "";
    public string En { get; set; } = "";

    public LText() { }
    public LText(string de, string en) { De = de; En = en; }

    public string Get(string language)
    {
        var first = language == "en" ? En : De;
        var second = language == "en" ? De : En;
        return first.Length > 0 ? first : second;
    }

    public LText Clone() => new(De, En);
}

public sealed class LTextConverter : JsonConverter<LText>
{
    public override LText Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var s = reader.GetString() ?? "";
            return new LText(s, s);
        }
        using var doc = JsonDocument.ParseValue(ref reader);
        if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("Titel muss Text oder {\"de\": ..., \"en\": ...} sein");
        var de = doc.RootElement.TryGetProperty("de", out var d) ? d.GetString() : null;
        var en = doc.RootElement.TryGetProperty("en", out var e) ? e.GetString() : null;
        return new LText(de ?? en ?? "", en ?? de ?? "");
    }

    public override void Write(Utf8JsonWriter writer, LText value, JsonSerializerOptions options)
    {
        if (value.De == value.En)
        {
            writer.WriteStringValue(value.De);
            return;
        }
        writer.WriteStartObject();
        writer.WriteString("de", value.De);
        writer.WriteString("en", value.En);
        writer.WriteEndObject();
    }
}

public sealed class TileModel
{
    [JsonPropertyName("type"), JsonPropertyOrder(1)] public string Type { get; set; } = "button";   // value | button
    [JsonPropertyName("x"), JsonPropertyOrder(2)] public int X { get; set; }
    [JsonPropertyName("y"), JsonPropertyOrder(3)] public int Y { get; set; }
    [JsonPropertyName("w"), JsonPropertyOrder(4)] public int W { get; set; } = 1;
    [JsonPropertyName("h"), JsonPropertyOrder(5)] public int H { get; set; } = 1;
    [JsonPropertyName("source"), JsonPropertyOrder(6)] public string? Source { get; set; }
    [JsonPropertyName("title"), JsonPropertyOrder(7)] public LText Title { get; set; } = new();
    [JsonPropertyName("icon"), JsonPropertyOrder(8)] public string? Icon { get; set; }
    [JsonPropertyName("format"), JsonPropertyOrder(9)] public string? Format { get; set; }
    [JsonPropertyName("max"), JsonPropertyOrder(10)] public double? Max { get; set; }
    [JsonPropertyName("trigger"), JsonPropertyOrder(11)] public string? Trigger { get; set; }
    [JsonPropertyName("hold_ms"), JsonPropertyOrder(12)] public int? HoldMs { get; set; }
    [JsonPropertyName("action"), JsonPropertyOrder(13)] public string? Action { get; set; }

    public bool IsButton => Type == "button";
    public string EffectiveFormat => Format ?? (IsButton ? "onoff" : "text");
    public string EffectiveTrigger => Trigger ?? "none";

    /// <summary>Leere Texte als "nicht gesetzt" behandeln, damit das JSON schlank bleibt.</summary>
    public void Normalize()
    {
        // Enter liefert unter Windows \r\n; das Display würde das als zwei Umbrüche zeigen
        Title.De = Title.De.Replace("\r\n", "\n").Replace('\r', '\n');
        Title.En = Title.En.Replace("\r\n", "\n").Replace('\r', '\n');
        if (string.IsNullOrWhiteSpace(Source)) Source = null;
        if (string.IsNullOrWhiteSpace(Icon)) Icon = null;
        if (string.IsNullOrWhiteSpace(Action)) Action = null;
        if (Max is <= 0) Max = null;
        if (HoldMs is <= 0) HoldMs = null;
        if (!IsButton)
        {
            Trigger = null;
            HoldMs = null;
            Action = null;
        }
    }
}

public sealed class PageModel
{
    [JsonPropertyName("title"), JsonPropertyOrder(1)] public LText Title { get; set; } = new("Seite", "Page");
    [JsonPropertyName("icon"), JsonPropertyOrder(2)] public string? Icon { get; set; }
    [JsonPropertyName("rows"), JsonPropertyOrder(3)] public int Rows { get; set; } = 4;
    [JsonPropertyName("tiles"), JsonPropertyOrder(4)] public List<TileModel> Tiles { get; set; } = [];
}

/// <summary>Ein Wert, der im Ruhemodus angezeigt wird (höchstens zwei).</summary>
public sealed class IdleValueModel
{
    [JsonPropertyName("source"), JsonPropertyOrder(1)] public string Source { get; set; } = "";
    [JsonPropertyName("title"), JsonPropertyOrder(2)] public LText Title { get; set; } = new();
    [JsonPropertyName("format"), JsonPropertyOrder(3)] public string Format { get; set; } = "text";
    [JsonPropertyName("max"), JsonPropertyOrder(4)] public double? Max { get; set; }
}

public sealed class IdleModel
{
    [JsonPropertyName("timeout_s")] public int TimeoutS { get; set; } = 60;
    [JsonPropertyName("brightness")] public int Brightness { get; set; } = 8;
    [JsonPropertyName("move_s")] public int MoveS { get; set; } = 60;
    /// <summary>null = nicht festgelegt (das Gerät nimmt Solar und Haus); leere Liste = keine Werte.</summary>
    [JsonPropertyName("values")] public List<IdleValueModel>? Values { get; set; }
}

public sealed class ConfigModel
{
    [JsonPropertyName("v"), JsonPropertyOrder(1)] public int V { get; set; } = 1;
    [JsonPropertyName("language"), JsonPropertyOrder(2)] public string Language { get; set; } = "de";
    [JsonPropertyName("theme"), JsonPropertyOrder(3)] public string Theme { get; set; } = "dark";
    [JsonPropertyName("brightness"), JsonPropertyOrder(4)] public int Brightness { get; set; } = 80;
    [JsonPropertyName("hold_ms"), JsonPropertyOrder(5)] public int HoldMs { get; set; } = 600;
    [JsonPropertyName("idle"), JsonPropertyOrder(6)] public IdleModel Idle { get; set; } = new();
    [JsonPropertyName("pages"), JsonPropertyOrder(7)] public List<PageModel> Pages { get; set; } = [];

    private static readonly JsonSerializerOptions Compact = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,   // Umlaute bleiben lesbar
    };

    private static readonly JsonSerializerOptions Pretty = new(Compact) { WriteIndented = true };

    public static ConfigModel Parse(string json)
    {
        var model = JsonSerializer.Deserialize<ConfigModel>(json, Compact) ?? throw new JsonException("leere Konfiguration");
        foreach (var page in model.Pages)
            foreach (var tile in page.Tiles) tile.Normalize();
        return model;
    }

    public string ToJson(bool pretty = false)
    {
        foreach (var page in Pages)
            foreach (var tile in page.Tiles) tile.Normalize();
        return JsonSerializer.Serialize(this, pretty ? Pretty : Compact);
    }

    public ConfigModel Clone() => Parse(ToJson());
}

/// <summary>Prüft eine Konfiguration nach denselben Regeln wie die Firmware (docs/config.md), damit der Editor nichts Ungültiges sendet.</summary>
public static class ConfigValidator
{
    public const int Cols = 8;
    public const int MaxRows = 6;
    public const int MaxPages = 8;
    public const int MaxTiles = 48;

    private static readonly string[] Types = ["value", "button"];
    private static readonly string[] Formats = ["power", "kwh", "status", "onoff", "text", "percent", "time"];
    private static readonly string[] Triggers = ["none", "tap", "hold", "confirm"];

    public static List<string> Validate(ConfigModel c)
    {
        var errors = new List<string>();
        if (c.Language is not ("de" or "en")) errors.Add("Sprache muss de oder en sein");
        if (c.Theme is not ("dark" or "light")) errors.Add("Design muss dark oder light sein");
        if (c.Brightness is < 1 or > 100) errors.Add("Helligkeit muss zwischen 1 und 100 liegen");
        if (c.HoldMs is < 100 or > 5000) errors.Add("Haltedauer muss zwischen 100 und 5000 ms liegen");
        if (c.Idle.TimeoutS is < 5 or > 3600) errors.Add("Ruhemodus nach: 5 bis 3600 s");
        if (c.Idle.Brightness is < 1 or > 100) errors.Add("Helligkeit im Ruhemodus: 1 bis 100");
        if (c.Idle.MoveS is < 5 or > 3600) errors.Add("Uhr verschieben: 5 bis 3600 s");
        if (c.Idle.Values is { } iv)
        {
            if (iv.Count > 2) errors.Add("Ruhemodus: höchstens 2 Werte");
            for (var i = 0; i < iv.Count; i++)
            {
                var src = iv[i].Source;
                if (string.IsNullOrWhiteSpace(src) || !(src.StartsWith("pc:", StringComparison.Ordinal) || src.Contains('.')))
                    errors.Add($"Ruhemodus, Wert {i + 1}: Quelle fehlt oder ist ungültig (z. B. sensor.xyz)");
                if (!Formats.Contains(iv[i].Format)) errors.Add($"Ruhemodus, Wert {i + 1}: Format unbekannt");
            }
        }
        if (c.Pages.Count is < 1 or > MaxPages) errors.Add($"1 bis {MaxPages} Seiten erlaubt");

        for (var p = 0; p < c.Pages.Count; p++)
        {
            var page = c.Pages[p];
            if (page.Rows is < 1 or > MaxRows) errors.Add($"Seite {p + 1}: Reihen 1 bis {MaxRows}");
            if (page.Tiles.Count > MaxTiles) errors.Add($"Seite {p + 1}: höchstens {MaxTiles} Kacheln");
            var rows = Math.Clamp(page.Rows, 1, MaxRows);
            var taken = new int[MaxRows, Cols];
            for (var t = 0; t < page.Tiles.Count; t++)
            {
                var tile = page.Tiles[t];
                var where = $"Seite {p + 1}, Kachel {t + 1}";
                if (!Types.Contains(tile.Type)) errors.Add($"{where}: Art unbekannt");
                if (tile.Format != null && !Formats.Contains(tile.Format)) errors.Add($"{where}: Format unbekannt");
                if (tile.Trigger != null && !Triggers.Contains(tile.Trigger)) errors.Add($"{where}: Bedienung unbekannt");
                if (tile.W < 1 || tile.H < 1) { errors.Add($"{where}: Grösse mindestens 1×1"); continue; }
                if (tile.X < 0 || tile.Y < 0 || tile.X + tile.W > Cols) { errors.Add($"{where}: ragt seitlich aus dem Raster"); continue; }
                if (tile.Y + tile.H > rows) { errors.Add($"{where}: ragt unten aus dem Raster ({rows} Reihen)"); continue; }
                if (tile.Icon != null && !MdiIcons.Exists(tile.Icon)) errors.Add($"{where}: Symbol '{tile.Icon}' unbekannt");
                if (tile.Action != null && !tile.Action.StartsWith("pc:", StringComparison.Ordinal)) errors.Add($"{where}: Aktion muss mit pc: beginnen");
                if (tile.IsButton && tile.EffectiveTrigger != "none" && tile.Action == null &&
                    (string.IsNullOrEmpty(tile.Source) || tile.Source.StartsWith("pc:", StringComparison.Ordinal)))
                    errors.Add($"{where}: Button braucht eine Aktion oder eine HA-Entität");
                for (var y = tile.Y; y < tile.Y + tile.H; y++)
                    for (var x = tile.X; x < tile.X + tile.W; x++)
                    {
                        if (taken[y, x] != 0) { errors.Add($"{where}: überlappt mit Kachel {taken[y, x]}"); y = tile.Y + tile.H; break; }
                        taken[y, x] = t + 1;
                    }
            }
        }
        return errors;
    }

    /// <summary>Würde diese Kachel mit Position und Grösse gültig im Raster der Seite liegen (ohne andere zu überlappen)?</summary>
    public static bool Fits(PageModel page, TileModel tile, int x, int y, int w, int h)
    {
        var rows = Math.Clamp(page.Rows, 1, MaxRows);
        if (w < 1 || h < 1 || x < 0 || y < 0 || x + w > Cols || y + h > rows) return false;
        foreach (var other in page.Tiles)
        {
            if (ReferenceEquals(other, tile)) continue;
            if (x < other.X + other.W && other.X < x + w && y < other.Y + other.H && other.Y < y + h) return false;
        }
        return true;
    }

    /// <summary>Erste freie Stelle für eine Kachel der Grösse w×h, oder null.</summary>
    public static (int X, int Y)? FindFreeSpot(PageModel page, int w, int h)
    {
        var rows = Math.Clamp(page.Rows, 1, MaxRows);
        for (var y = 0; y + h <= rows; y++)
            for (var x = 0; x + w <= Cols; x++)
                if (Fits(page, null!, x, y, w, h)) return (x, y);
        return null;
    }
}
