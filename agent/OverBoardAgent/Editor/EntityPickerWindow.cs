using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using OverBoardAgent.Core;

namespace OverBoardAgent.Editor;

/// <summary>Auswahl einer Home-Assistant-Entität mit Suche. Gibt die gewählte Entität zurück, bei Abbruch null.</summary>
public sealed class EntityPickerWindow : Window
{
    private const int MaxShown = 300;
    private readonly TextBox _search;
    private ListBox _list;
    private readonly Grid _grid;
    private readonly TextBlock _info;
    private readonly Button _ok;
    private IReadOnlyList<HaEntity> _all = [];

    public EntityPickerWindow()
    {
        Title = Strings.Get("ent.title");
        Width = 760;
        Height = 640;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _search = new TextBox { Watermark = Strings.Get("ent.search") };
        _info = new TextBlock { Opacity = 0.7, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        _list = MakeList(new List<HaEntity>());
        var refresh = new Button { Content = Strings.Get("ent.refresh") };
        _ok = new Button { Content = Strings.Get("ent.use"), MinWidth = 110, IsEnabled = false };
        var cancel = new Button { Content = Strings.Get("edit.cancel"), MinWidth = 110 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(refresh);
        buttons.Children.Add(_ok);
        buttons.Children.Add(cancel);

        var grid = _grid = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"), Margin = new Thickness(14), RowSpacing = 6 };
        grid.Children.Add(_search);
        Grid.SetRow(_info, 1);
        grid.Children.Add(_info);
        Grid.SetRow(_list, 2);
        grid.Children.Add(_list);
        Grid.SetRow(buttons, 3);
        grid.Children.Add(buttons);
        Content = grid;

        _search.TextChanged += (_, _) => Fill();
        _ok.Click += (_, _) => Close(_list.SelectedItem as HaEntity);
        cancel.Click += (_, _) => Close(null);
        refresh.Click += async (_, _) => await LoadAsync(force: true);
        Opened += async (_, _) => { await LoadAsync(force: false); _search.Focus(); };
    }

    private ListBox MakeList(List<HaEntity> items)
    {
        var list = new ListBox
        {
            ItemsPanel = new FuncTemplate<Panel?>(() => new StackPanel()),
            ItemTemplate = new FuncDataTemplate<HaEntity>((e, _) => new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                Margin = new Thickness(2, 4),
                Children =
                {
                    new StackPanel
                    {
                        Children =
                        {
                            new TextBlock { Text = e.Name.Length > 0 ? e.Name : e.Id, FontWeight = FontWeight.SemiBold },
                            new TextBlock { Text = e.Id, FontSize = 12, Opacity = 0.65 },
                        },
                    },
                    new TextBlock { Text = e.StateText, Opacity = 0.8, VerticalAlignment = VerticalAlignment.Center, [Grid.ColumnProperty] = 1 },
                },
            }),
            ItemsSource = items,
        };
        list.SelectionChanged += (_, _) => _ok.IsEnabled = list.SelectedItem != null;
        list.DoubleTapped += (_, _) => { if (list.SelectedItem is HaEntity e) Close(e); };
        return list;
    }

    private async Task LoadAsync(bool force)
    {
        if (!force && HaClient.Cached is { } cached)
        {
            _all = cached;
            Fill();
            return;
        }
        _info.Text = Strings.Get("ent.loading");
        var (entities, error) = await HaClient.FetchAsync(Settings.HaUrl, Settings.HaToken);
        if (entities == null)
        {
            _info.Text = error + "  " + Strings.Get("ent.hint");
            _info.Foreground = new SolidColorBrush(Color.Parse("#E5484D"));
            return;
        }
        _info.Foreground = null;
        _all = entities;
        Fill();
    }

    private void Fill()
    {
        var q = (_search.Text ?? "").Trim().ToLowerInvariant();
        var matches = q.Length == 0
            ? _all.ToList()
            : _all.Where(e => e.Id.Contains(q, StringComparison.Ordinal) || e.Name.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        var fresh = MakeList(matches.Take(MaxShown).ToList());
        _grid.Children.Remove(_list);
        _list = fresh;
        Grid.SetRow(_list, 2);
        _grid.Children.Add(_list);
        _ok.IsEnabled = false;
        _info.Text = Strings.Format("ent.count", matches.Count) + (matches.Count > MaxShown ? "  " + Strings.Format("ent.more", MaxShown) : "");
    }
}
