using Avalonia.Controls;
using Avalonia.Platform.Storage;
using OverBoardAgent.Core;
using OverBoardAgent.Platform;

namespace OverBoardAgent;

/// <summary>Dialog zum Anlegen und Bearbeiten einer Aktion. Gibt die gespeicherte Aktion zurück, bei Abbruch null.</summary>
public partial class ActionEditWindow : Window
{
    private readonly ActionStore _store;
    private readonly string? _originalId;
    private bool _idEditedByUser;

    public ActionEditWindow() : this(new ActionStore(), null) { }

    /// <param name="idLocked">Die Kennung wird auf Kacheln verwendet und darf nicht geändert werden (sonst zeigen die Kacheln ins Leere).</param>
    public ActionEditWindow(ActionStore store, UserAction? existing, bool idLocked = false)
    {
        InitializeComponent();
        _store = store;
        _originalId = existing?.Id;

        Title = Strings.Get(existing == null ? "edit.title.new" : "edit.title.edit");
        NameLabel.Text = Strings.Get("edit.name");
        IdLabel.Text = Strings.Get("edit.id");
        KindLabel.Text = Strings.Get("edit.kind");
        TargetLabel.Text = Strings.Get("edit.target");
        TargetHint.Text = Strings.Get("edit.target.hint");
        BrowseButton.Content = Strings.Get("edit.browse");
        ArgsLabel.Text = Strings.Get("edit.args");
        KeysLabel.Text = Strings.Get("edit.keys");
        KeysHint.Text = Strings.Get("edit.keys.hint");
        SaveButton.Content = Strings.Get("edit.save");
        CancelButton.Content = Strings.Get("edit.cancel");

        KindBox.Items.Add(Strings.Get("edit.kind.open"));
        KindBox.Items.Add(Strings.Get("edit.kind.keys"));
        KindBox.SelectionChanged += (_, _) => UpdateKindPanels();

        if (existing != null)
        {
            NameBox.Text = existing.Name;
            IdBox.Text = existing.Id;
            _idEditedByUser = true;
            KindBox.SelectedIndex = existing.Kind == ActionKind.Open ? 0 : 1;
            TargetBox.Text = existing.Target;
            ArgsBox.Text = existing.Arguments;
            KeysBox.Text = existing.Keys;
        }
        else
        {
            KindBox.SelectedIndex = 0;
        }
        UpdateKindPanels();
        if (idLocked)
        {
            IdBox.IsEnabled = false;
            IdLabel.Text += "  " + Strings.Get("edit.id.locked");
        }

        // Die Kennung folgt dem Namen, bis sie von Hand geändert wird
        NameBox.TextChanged += (_, _) =>
        {
            if (!_idEditedByUser) IdBox.Text = ActionStore.SuggestId(NameBox.Text ?? "");
        };
        IdBox.TextChanged += (_, _) =>
        {
            if (IdBox.IsFocused) _idEditedByUser = true;
        };

        BrowseButton.Click += async (_, _) => await BrowseAsync();
        CancelButton.Click += (_, _) => Close(null);
        SaveButton.Click += (_, _) => Save();
    }

    private void UpdateKindPanels()
    {
        var open = KindBox.SelectedIndex != 1;
        OpenPanel.IsVisible = open;
        KeysPanel.IsVisible = !open;
    }

    private async Task BrowseAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { AllowMultiple = false });
        if (files.Count > 0) TargetBox.Text = files[0].TryGetLocalPath() ?? files[0].Path.ToString();
    }

    private void Fail(string text)
    {
        ErrorText.Text = text;
        ErrorText.IsVisible = true;
    }

    private void Save()
    {
        var name = (NameBox.Text ?? "").Trim();
        var id = (IdBox.Text ?? "").Trim();
        var kind = KindBox.SelectedIndex == 1 ? ActionKind.Keys : ActionKind.Open;
        var target = (TargetBox.Text ?? "").Trim().Trim('"');   // Anführungszeichen um den Pfad sind unnötig
        var keys = (KeysBox.Text ?? "").Trim();

        if (name.Length == 0) { Fail(Strings.Get("err.name")); return; }
        if (!ActionStore.IsValidId(id)) { Fail(Strings.Get("err.id.invalid")); return; }
        if (ActionStore.IsReserved(id)) { Fail(Strings.Get("err.id.reserved")); return; }
        if (id != _originalId && _store.Find(id) != null) { Fail(Strings.Get("err.id.exists")); return; }
        if (kind == ActionKind.Open && target.Length == 0) { Fail(Strings.Get("err.target")); return; }
        if (kind == ActionKind.Keys)
        {
            if (KeySender.Parse(keys, out var error) == null) { Fail(Strings.Format("err.keys", error ?? "")); return; }
        }

        var action = new UserAction
        {
            Id = id,
            Name = name,
            Kind = kind,
            Target = kind == ActionKind.Open ? target : "",
            Arguments = kind == ActionKind.Open ? (ArgsBox.Text ?? "").Trim() : "",
            Keys = kind == ActionKind.Keys ? keys : "",
        };
        _store.Save(action, _originalId);
        Close(action);
    }
}
