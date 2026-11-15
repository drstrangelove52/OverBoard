using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;

namespace OverBoardAgent.Editor;

/// <summary>Auswahlfenster für Symbole: Suchfeld und Raster mit den Material-Design-Icons. Gibt den Namen zurück, bei Abbruch null.</summary>
public sealed class IconPickerWindow : Window
{
    private const int MaxShown = 300;
    private static readonly FontFamily IconFont = new("avares://OverBoardAgent/Assets/Fonts#Material Design Icons");

    private readonly ListBox _list;
    private readonly TextBlock _info;
    private readonly TextBox _search;

    public IconPickerWindow(string? current)
    {
        Title = Core.Strings.Language == "en" ? "Choose icon" : "Symbol auswählen";
        Width = 760;
        Height = 640;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _search = new TextBox { Watermark = Core.Strings.Language == "en" ? "Search (English names, e.g. light, solar, home)" : "Suchen (englische Namen, z. B. light, solar, home)" };
        _info = new TextBlock { Opacity = 0.65, FontSize = 12, Margin = new Thickness(0, 6, 0, 0) };
        _list = new ListBox
        {
            ItemsPanel = new FuncTemplate<Panel?>(() => new WrapPanel()),
            ItemTemplate = new FuncDataTemplate<string>((name, _) => new StackPanel
            {
                Width = 78, Height = 70, Spacing = 2,
                Children =
                {
                    new TextBlock { Text = MdiIcons.Glyph(name), FontFamily = IconFont, FontSize = 30, HorizontalAlignment = HorizontalAlignment.Center },
                    new TextBlock { Text = name, FontSize = 9, Opacity = 0.7, TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 74 },
                },
            }),
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);

        var ok = new Button { Content = Core.Strings.Get("edit.save") == "Speichern" ? "Übernehmen" : "Apply", MinWidth = 110 };
        var none = new Button { Content = Core.Strings.Language == "en" ? "No icon" : "Kein Symbol", MinWidth = 110 };
        var cancel = new Button { Content = Core.Strings.Get("edit.cancel"), MinWidth = 110 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(none);
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"), Margin = new Thickness(14), RowSpacing = 6 };
        grid.Children.Add(_search);
        Grid.SetRow(_info, 1);
        grid.Children.Add(_info);
        Grid.SetRow(_list, 2);
        grid.Children.Add(_list);
        Grid.SetRow(buttons, 3);
        grid.Children.Add(buttons);
        Content = grid;

        Fill(current);
        _search.TextChanged += (_, _) => Fill(null);
        _list.DoubleTapped += (_, _) => { if (_list.SelectedItem is string n) Close(n); };
        ok.Click += (_, _) => Close(_list.SelectedItem as string);
        none.Click += (_, _) => Close("");
        cancel.Click += (_, _) => Close(null);
        Opened += (_, _) => _search.Focus();
    }

    private void Fill(string? select)
    {
        var q = (_search.Text ?? "").Trim().ToLowerInvariant();
        var all = MdiIcons.Names;
        var matches = (q.Length == 0 ? all : all.Where(n => n.Contains(q, StringComparison.Ordinal))).ToList();
        var shown = matches.Take(MaxShown).ToList();
        if (select != null && select.Length > 0 && !shown.Contains(select) && MdiIcons.Exists(select)) shown.Insert(0, select);
        _list.SelectedItem = null;
        _list.ItemsSource = shown;
        if (select != null && shown.Contains(select)) _list.SelectedItem = select;
        _info.Text = Core.Strings.Language == "en"
            ? $"{matches.Count} icons" + (matches.Count > MaxShown ? $" – showing the first {MaxShown}, refine the search" : "")
            : $"{matches.Count} Symbole" + (matches.Count > MaxShown ? $" – die ersten {MaxShown} werden gezeigt, Suche eingrenzen" : "");
    }
}
