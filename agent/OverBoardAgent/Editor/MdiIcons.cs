using Avalonia;
using Avalonia.Platform;

namespace OverBoardAgent.Editor;

/// <summary>Material Design Icons: Name → Unicode-Codepunkt (Liste aus Assets/mdi-icons.tsv, dieselbe wie in der Firmware).</summary>
public static class MdiIcons
{
    private static Dictionary<string, int>? _codes;
    private static List<string>? _names;

    private static void EnsureLoaded()
    {
        if (_codes != null) return;
        var codes = new Dictionary<string, int>(StringComparer.Ordinal);
        var names = new List<string>();
        try
        {
            using var stream = AssetLoader.Open(new Uri("avares://OverBoardAgent/Assets/mdi-icons.tsv"));
            using var reader = new StreamReader(stream);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                var tab = line.IndexOf('\t');
                if (tab <= 0) continue;
                var name = line[..tab];
                if (int.TryParse(line[(tab + 1)..], System.Globalization.NumberStyles.HexNumber, null, out var cp))
                {
                    codes[name] = cp;
                    names.Add(name);
                }
            }
        }
        catch (Exception ex)
        {
            OverBoardAgent.Core.Log.Warn($"Symbolliste nicht lesbar: {ex.Message}");
        }
        _codes = codes;
        _names = names;
    }

    private static string Clean(string name) => name.StartsWith("mdi:", StringComparison.Ordinal) ? name[4..] : name;

    public static IReadOnlyList<string> Names
    {
        get { EnsureLoaded(); return _names!; }
    }

    public static bool Exists(string? name)
    {
        EnsureLoaded();
        return !string.IsNullOrEmpty(name) && _codes!.ContainsKey(Clean(name));
    }

    /// <summary>Das Symbol als Text für die Symbolschrift. Unbekannte Namen ergeben das Fragezeichen-Symbol.</summary>
    public static string Glyph(string? name)
    {
        EnsureLoaded();
        if (!string.IsNullOrEmpty(name) && _codes!.TryGetValue(Clean(name), out var cp)) return char.ConvertFromUtf32(cp);
        return _codes!.TryGetValue("help-circle", out var fallback) ? char.ConvertFromUtf32(fallback) : "?";
    }
}
