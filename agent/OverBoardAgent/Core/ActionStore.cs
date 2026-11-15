using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace OverBoardAgent.Core;

public enum ActionKind
{
    /// <summary>Programm, Datei, Ordner oder Adresse öffnen (wie "Ausführen" in Windows).</summary>
    Open,
    /// <summary>Tastenkombination senden, z. B. Win+Shift+S.</summary>
    Keys,
}

/// <summary>Eine selbst definierte Aktion. Das Gerät ruft sie nur über die Kennung auf (pc:&lt;Kennung&gt;).</summary>
public sealed class UserAction
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public ActionKind Kind { get; set; } = ActionKind.Open;
    public string Target { get; set; } = "";
    public string Arguments { get; set; } = "";
    public string Keys { get; set; } = "";
}

/// <summary>Speichert die Aktionen in %APPDATA%\OverBoardAgent\actions.json.</summary>
public sealed partial class ActionStore
{
    public static IEnumerable<string> ReservedIds => BuiltinActions.All.Select(a => a.Id);

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path = Path.Combine(Settings.DataDir, "actions.json");
    private readonly List<UserAction> _items = [];
    private readonly object _gate = new();

    public event Action? Changed;

    public ActionStore() => Load();

    public IReadOnlyList<UserAction> Items
    {
        get { lock (_gate) return _items.ToList(); }
    }

    public UserAction? Find(string id)
    {
        lock (_gate) return _items.FirstOrDefault(a => a.Id == id);
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,22}$")]
    private static partial Regex IdPattern();

    public static bool IsValidId(string id) => IdPattern().IsMatch(id);
    public static bool IsReserved(string id) => BuiltinActions.IsBuiltin(id);

    /// <summary>Kennungsvorschlag aus dem Namen: Kleinbuchstaben, Umlaute umgeschrieben, Rest wird "-".</summary>
    public static string SuggestId(string name)
    {
        var s = name.Trim().ToLowerInvariant()
            .Replace("ä", "ae").Replace("ö", "oe").Replace("ü", "ue").Replace("ß", "ss");
        s = Regex.Replace(s, "[^a-z0-9]+", "-").Trim('-');
        return s.Length > 23 ? s[..23].TrimEnd('-') : s;
    }

    /// <summary>Speichert eine neue oder geänderte Aktion. <paramref name="originalId"/> ist die bisherige Kennung beim Bearbeiten.</summary>
    public void Save(UserAction action, string? originalId)
    {
        lock (_gate)
        {
            var index = originalId == null ? -1 : _items.FindIndex(a => a.Id == originalId);
            if (index >= 0) _items[index] = action;
            else _items.Add(action);
            Write();
        }
        Changed?.Invoke();
    }

    public void Remove(string id)
    {
        lock (_gate)
        {
            _items.RemoveAll(a => a.Id == id);
            Write();
        }
        Changed?.Invoke();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var loaded = JsonSerializer.Deserialize<List<UserAction>>(File.ReadAllText(_path), Json);
            if (loaded != null) _items.AddRange(loaded.Where(a => IsValidId(a.Id) && !IsReserved(a.Id)));
            Log.Info($"{_items.Count} Aktionen geladen");
        }
        catch (Exception ex)
        {
            Log.Warn($"actions.json nicht lesbar: {ex.Message}");
        }
    }

    private void Write()
    {
        try
        {
            Directory.CreateDirectory(Settings.DataDir);
            File.WriteAllText(_path, JsonSerializer.Serialize(_items, Json));
        }
        catch (Exception ex)
        {
            Log.Error($"actions.json nicht speicherbar: {ex.Message}");
        }
    }
}
