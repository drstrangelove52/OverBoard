using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using OverBoardAgent.Core;

namespace OverBoardAgent;

/// <summary>Dialog zum Hinzufügen eines Senders: Suche im Verzeichnis radio-browser.info oder Eingabe von Hand. Gibt den Sender zurück, bei Abbruch null.</summary>
public sealed class RadioSearchWindow : Window
{
    private readonly TextBox _query = new();
    private readonly ListBox _list = new();
    private readonly TextBlock _status = new() { FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap };
    private readonly Button _ok = new() { IsEnabled = false, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly TextBox _name = new();
    private readonly TextBox _url = new();
    private readonly TextBox _logo = new();
    private List<RadioSearch.Result> _results = [];
    private CancellationTokenSource? _cts;

    public RadioSearchWindow() : this(false) { }

    /// <param name="existing">Ein vorhandener Sender zum Bearbeiten (nur mit manual).</param>
    public RadioSearchWindow(bool manual, Station? existing = null)
    {
        Title = Strings.Get(existing != null ? "radio.edit" : manual ? "radio.manual" : "radio.search");
        Width = 640;
        Height = manual ? 380 : 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var cancel = new Button { Content = Strings.Get("edit.cancel") };
        cancel.Click += (_, _) => Close(null);
        _ok.Content = Strings.Get(existing != null ? "radio.save" : "radio.add");
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, _ok } };

        var root = new DockPanel { Margin = new Thickness(20) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        buttons.Margin = new Thickness(0, 12, 0, 0);
        root.Children.Add(buttons);

        if (manual)
        {
            var form = new StackPanel { Spacing = 6 };
            form.Children.Add(Small(Strings.Get("radio.f.name")));
            form.Children.Add(_name);
            form.Children.Add(Small(Strings.Get("radio.f.url")));
            form.Children.Add(_url);
            form.Children.Add(Small(Strings.Get("radio.f.logo")));
            form.Children.Add(_logo);
            form.Children.Add(_status);
            root.Children.Add(form);
            if (existing != null)
            {
                _name.Text = existing.Name;
                _url.Text = existing.Url;
                _logo.Text = existing.Logo ?? "";
                _ok.IsEnabled = true;
            }
            void Check() => _ok.IsEnabled = (_name.Text ?? "").Trim().Length > 0 && (_url.Text ?? "").Trim().StartsWith("http", StringComparison.OrdinalIgnoreCase);
            _name.TextChanged += (_, _) => Check();
            _url.TextChanged += (_, _) => Check();
            _ok.Click += (_, _) =>
            {
                var logo = (_logo.Text ?? "").Trim();
                Close(new Station(_name.Text!.Trim(), _url.Text!.Trim(), logo.Length > 0 ? logo : null));
            };
        }
        else
        {
            var searchRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
            _query.Watermark = Strings.Get("radio.f.query");
            var go = new Button { Content = Strings.Get("radio.go"), Classes = { "accent" } };
            Grid.SetColumn(go, 1);
            searchRow.Children.Add(_query);
            searchRow.Children.Add(go);
            DockPanel.SetDock(searchRow, Dock.Top);
            root.Children.Add(searchRow);
            DockPanel.SetDock(_status, Dock.Bottom);
            _status.Margin = new Thickness(0, 8, 0, 0);
            root.Children.Add(_status);
            _list.Margin = new Thickness(0, 10, 0, 0);
            root.Children.Add(_list);

            _query.KeyDown += async (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) await SearchAsync(); };
            go.Click += async (_, _) => await SearchAsync();
            _list.SelectionChanged += (_, _) => _ok.IsEnabled = _list.SelectedIndex >= 0;
            _list.DoubleTapped += (_, _) => Accept();
            _ok.Click += (_, _) => Accept();
            Opened += (_, _) => _query.Focus();
        }
        Content = root;
        Closed += (_, _) => _cts?.Cancel();
    }

    private static TextBlock Small(string text) => new() { Text = text, FontSize = 12, Opacity = 0.7 };

    private void Accept()
    {
        if (_list.SelectedIndex < 0 || _list.SelectedIndex >= _results.Count) return;
        var r = _results[_list.SelectedIndex];
        Close(new Station(r.Name, r.Url, r.Logo));
    }

    private async Task SearchAsync()
    {
        var text = (_query.Text ?? "").Trim();
        if (text.Length < 2) return;
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        _status.Text = Strings.Get("radio.searching");
        _status.Foreground = null;
        _list.ItemsSource = null;
        _ok.IsEnabled = false;
        var (results, error) = await RadioSearch.SearchAsync(text, cts.Token);
        if (cts.IsCancellationRequested) return;
        if (results == null)
        {
            _status.Text = Strings.Format("radio.err", error ?? "?");
            _status.Foreground = new SolidColorBrush(Color.Parse("#E5484D"));
            return;
        }
        _results = results;
        // Neue Liste mit einfachem Stapel statt virtualisiertem Panel: so bleiben die Zeilen nach jeder Suche sauber
        var items = new List<Control>();
        foreach (var r in results)
        {
            var sp = new StackPanel { Margin = new Thickness(4, 4) };
            sp.Children.Add(new TextBlock { Text = r.Name, FontWeight = FontWeight.SemiBold });
            sp.Children.Add(new TextBlock { Text = r.Details, FontSize = 12, Opacity = 0.65 });
            items.Add(sp);
        }
        _list.ItemsSource = items;
        _status.Text = results.Count == 0 ? Strings.Get("radio.none.found") : Strings.Format("radio.found", results.Count);
    }
}
