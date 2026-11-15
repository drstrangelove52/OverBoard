using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using OverBoardAgent.Core;

namespace OverBoardAgent;

/// <summary>Zeile der Aktionsliste.</summary>
public sealed record ActionRow(string Id, string Name, string Summary, string Reference);

/// <summary>Seite "Aktionen": eigene PC-Aktionen anlegen, bearbeiten, testen und löschen. Das Gerät ruft sie mit pc:&lt;Kennung&gt; auf.</summary>
public partial class ActionsView : UserControl
{
    private AgentCore? _core;
    private readonly ObservableCollection<ActionRow> _rows = [];

    public ActionsView()
    {
        InitializeComponent();
    }

    /// <summary>Liefert, wo eine Aktion (pc:&lt;Kennung&gt;) verwendet wird. Vom Hauptfenster gesetzt, damit verwendete Aktionen nicht gelöscht werden.</summary>
    public Func<string, IReadOnlyList<string>>? UsedBy { get; set; }

    private Window? Owner => TopLevel.GetTopLevel(this) as Window;

    public void Init(AgentCore core)
    {
        _core = core;
        ActionList.ItemsSource = _rows;
        ApplyTexts();
        Reload();

        ActionList.SelectionChanged += (_, _) => UpdateButtons();
        ActionList.DoubleTapped += async (_, _) => await EditSelectedAsync();
        NewButton.Click += async (_, _) => await EditAsync(null);
        EditButton.Click += async (_, _) => await EditSelectedAsync();
        DeleteButton.Click += async (_, _) => await DeleteSelectedAsync();
        TestButton.Click += async (_, _) => await TestSelectedAsync();

        _core.Actions.Changed += OnActionsChanged;
    }

    /// <summary>Beim Schliessen des Hauptfensters abmelden.</summary>
    public void Detach()
    {
        if (_core != null) _core.Actions.Changed -= OnActionsChanged;
    }

    private void OnActionsChanged() => Dispatcher.UIThread.Post(Reload);

    public void ApplyTexts()
    {
        ActionsLabel.Text = Strings.Get("win.actions");
        EmptyText.Text = Strings.Get("win.empty");
        HintText.Text = Strings.Get("win.actions.hint");
        NewButton.Content = Strings.Get("win.new");
        EditButton.Content = Strings.Get("win.edit");
        TestButton.Content = Strings.Get("win.test");
        DeleteButton.Content = Strings.Get("win.delete");
    }

    private void Reload()
    {
        if (_core == null) return;
        var selected = (ActionList.SelectedItem as ActionRow)?.Id;
        ActionList.SelectedItem = null;   // Auswahl zuerst aufheben, sonst stürzt die Listenauswahl beim Leeren ab
        _rows.Clear();
        foreach (var a in _core.Actions.Items)
        {
            var summary = a.Kind == ActionKind.Open
                ? (string.IsNullOrEmpty(a.Arguments) ? a.Target : $"{a.Target} {a.Arguments}")
                : a.Keys;
            _rows.Add(new ActionRow(a.Id, a.Name, summary, "pc:" + a.Id));
        }
        EmptyText.IsVisible = _rows.Count == 0;
        ActionList.SelectedItem = _rows.FirstOrDefault(r => r.Id == selected);
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        var has = ActionList.SelectedItem != null;
        EditButton.IsEnabled = has;
        TestButton.IsEnabled = has;
        DeleteButton.IsEnabled = has;
    }

    private async Task EditAsync(UserAction? existing)
    {
        if (_core == null || Owner == null) return;
        var locked = existing != null && (UsedBy?.Invoke("pc:" + existing.Id).Count ?? 0) > 0;
        var dialog = new ActionEditWindow(_core.Actions, existing, locked);
        await dialog.ShowDialog<UserAction?>(Owner);
    }

    private async Task EditSelectedAsync()
    {
        if (ActionList.SelectedItem is not ActionRow row) return;
        await EditAsync(_core!.Actions.Find(row.Id));
    }

    private Task TestSelectedAsync()
    {
        if (ActionList.SelectedItem is not ActionRow row) return Task.CompletedTask;
        if (!_core!.Run(row.Id)) return ShowMessageAsync(Strings.Get("win.test.failed"), false);
        return Task.CompletedTask;
    }

    private async Task DeleteSelectedAsync()
    {
        if (ActionList.SelectedItem is not ActionRow row) return;
        var uses = UsedBy?.Invoke("pc:" + row.Id) ?? [];
        if (uses.Count > 0)
        {
            var shown = string.Join("\n", uses.Take(6).Select(u => "• " + u)) + (uses.Count > 6 ? $"\n… (+{uses.Count - 6})" : "");
            await ShowMessageAsync(Strings.Format("act.inuse", row.Name, shown), false);
            return;
        }
        if (await ShowMessageAsync(Strings.Format("win.delete.confirm", row.Name), true))
            _core!.Actions.Remove(row.Id);
    }

    /// <summary>Kleiner Hinweis- oder Rückfragedialog. Gibt bei Rückfragen true für "Ja" zurück.</summary>
    private async Task<bool> ShowMessageAsync(string text, bool question)
    {
        if (Owner == null) return false;
        var dialog = new Window
        {
            Title = "OverBoard",
            Width = 460,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var yes = new Button { Content = Strings.Get(question ? "win.yes" : "win.close"), MinWidth = 90 };
        var no = new Button { Content = Strings.Get("win.no"), MinWidth = 90, IsVisible = question };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10,
            Children = { yes, no },
        };
        dialog.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(24),
            Spacing = 20,
            Children = { new TextBlock { Text = text, TextWrapping = Avalonia.Media.TextWrapping.Wrap }, buttons },
        };
        yes.Click += (_, _) => dialog.Close(true);
        no.Click += (_, _) => dialog.Close(false);
        return await dialog.ShowDialog<bool>(Owner);
    }
}
