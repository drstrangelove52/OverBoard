using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using OverBoardAgent.Core;

namespace OverBoardAgent;

/// <summary>Zeile der Senderliste.</summary>
public sealed record StationRow(string Name, string Summary);

/// <summary>Seite "Radio": Senderliste verwalten, abspielen, Sender suchen oder von Hand eintragen.</summary>
public partial class RadioView : UserControl
{
    private AgentCore? _core;
    private readonly ObservableCollection<StationRow> _rows = [];
    private bool _suppress;

    public RadioView()
    {
        InitializeComponent();
    }

    private Window? Owner => TopLevel.GetTopLevel(this) as Window;

    public void Init(AgentCore core)
    {
        _core = core;
        StationList.ItemsSource = _rows;
        ApplyTexts();
        Reload();

        StationList.SelectionChanged += (_, _) =>
        {
            if (_suppress) return;
            UpdateButtons();
        };
        StationList.DoubleTapped += (_, _) => PlaySelected();
        PlayButton.Click += (_, _) => PlaySelected();
        SearchButton.Click += async (_, _) => await SearchAsync();
        ManualButton.Click += async (_, _) => await ManualAsync();
        EditButton.Click += async (_, _) => await EditAsync();
        UpButton.Click += (_, _) => MoveSelected(-1);
        DownButton.Click += (_, _) => MoveSelected(+1);
        DeleteButton.Click += (_, _) => DeleteSelected();
        _core.Radio.Changed += OnChanged;
    }

    public void Detach()
    {
        if (_core != null) _core.Radio.Changed -= OnChanged;
    }

    private void OnChanged() => Dispatcher.UIThread.Post(Reload);

    public void ApplyTexts()
    {
        RadioLabel.Text = Strings.Get("radio.title");
        HintText.Text = Strings.Get("radio.hint");
        SearchButton.Content = Strings.Get("radio.search");
        ManualButton.Content = Strings.Get("radio.manual");
        EditButton.Content = Strings.Get("radio.edit");
        UpButton.Content = Strings.Get("radio.up");
        DownButton.Content = Strings.Get("radio.down");
        DeleteButton.Content = Strings.Get("win.delete");
        EmptyText.Text = Strings.Get("radio.empty");
        UpdateNow();
        UpdateButtons();
    }

    private void Reload()
    {
        if (_core == null) return;
        var selected = StationList.SelectedIndex;
        _suppress = true;
        try
        {
            StationList.SelectedItem = null;       // Auswahl zuerst aufheben, sonst stürzt die Listenauswahl beim Leeren ab
            _rows.Clear();
            var current = _core.Radio.CurrentIndex;
            var i = 0;
            foreach (var s in _core.Radio.Stations)
            {
                var mark = i == current ? (_core.Radio.Playing ? "▶ " : "● ") : "";
                _rows.Add(new StationRow(mark + s.Name, s.Url));
                i++;
            }
            EmptyText.IsVisible = _rows.Count == 0;
            if (selected >= 0 && selected < _rows.Count) StationList.SelectedIndex = selected;
        }
        finally
        {
            _suppress = false;
        }
        UpdateNow();
        UpdateButtons();
    }

    private void UpdateNow()
    {
        if (_core == null) return;
        var st = _core.Radio.CurrentStation;
        NowStation.Text = st == null ? Strings.Get("radio.none") : (_core.Radio.Playing ? "▶ " : "") + st.Name;
        NowTitle.Text = _core.Radio.Title;
        NowError.Text = _core.Radio.Error ?? "";
        NowError.IsVisible = !string.IsNullOrEmpty(_core.Radio.Error);
    }

    private void UpdateButtons()
    {
        var idx = StationList.SelectedIndex;
        var has = idx >= 0;
        PlayButton.IsEnabled = has || _core?.Radio.CurrentStation != null;
        PlayButton.Content = Strings.Get(has && idx != _core?.Radio.CurrentIndex || _core?.Radio.Playing != true ? "radio.play" : "radio.stop");
        EditButton.IsEnabled = has;
        UpButton.IsEnabled = has && idx > 0;
        DownButton.IsEnabled = has && idx < _rows.Count - 1;
        DeleteButton.IsEnabled = has;
    }

    private void PlaySelected()
    {
        if (_core == null) return;
        var idx = StationList.SelectedIndex;
        if (idx >= 0 && (idx != _core.Radio.CurrentIndex || !_core.Radio.Playing)) _core.Radio.Play(idx);
        else if (_core.Radio.Playing) _core.Radio.Stop();
        else _core.Radio.Play(_core.Radio.CurrentIndex);
    }

    private void MoveSelected(int delta)
    {
        if (_core == null) return;
        var idx = StationList.SelectedIndex;
        if (idx < 0) return;
        _core.Radio.Move(idx, delta);
        StationList.SelectedIndex = idx + delta;
    }

    private void DeleteSelected()
    {
        if (_core == null) return;
        var idx = StationList.SelectedIndex;
        if (idx >= 0) _core.Radio.Remove(idx);
    }

    private async Task SearchAsync()
    {
        if (_core == null || Owner == null) return;
        var dialog = new RadioSearchWindow();
        var station = await dialog.ShowDialog<Station?>(Owner);
        if (station != null) _core.Radio.Add(station);
    }

    private async Task EditAsync()
    {
        if (_core == null || Owner == null) return;
        var idx = StationList.SelectedIndex;
        if (idx < 0 || idx >= _core.Radio.Stations.Count) return;
        var dialog = new RadioSearchWindow(manual: true, existing: _core.Radio.Stations[idx]);
        var station = await dialog.ShowDialog<Station?>(Owner);
        if (station != null) _core.Radio.Update(idx, station);
    }

    private async Task ManualAsync()
    {
        if (_core == null || Owner == null) return;
        var dialog = new RadioSearchWindow(manual: true);
        var station = await dialog.ShowDialog<Station?>(Owner);
        if (station != null) _core.Radio.Add(station);
    }
}
