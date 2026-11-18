using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using OverBoardAgent.Core;
using OverBoardAgent.Editor;

namespace OverBoardAgent;

/// <summary>
/// Startseite: auf einen Blick, was läuft (Display, WLAN, Home Assistant, MQTT, Kalender, Radio) und was als Nächstes zu tun ist.
/// Die Werte kommen vom Gerät selbst (Verbindungsstatus) und aus den Einstellungen des Programms.
/// </summary>
public partial class StartView : UserControl
{
    private static readonly IBrush Ok = new SolidColorBrush(Color.Parse("#2E9E5B"));
    private static readonly IBrush Bad = new SolidColorBrush(Color.Parse("#E5484D"));
    private static readonly IBrush Idle = new SolidColorBrush(Color.Parse("#8A8F98"));

    private AgentCore? _core;
    private DispatcherTimer? _timer;

    /// <summary>Die Person möchte zu einem Bereich (Nummer wie in der Navigation).</summary>
    public event Action<int>? NavigateRequested;

    /// <summary>Wie viele Kacheln hat die Konfiguration im Editor? (für den Schritt "Display gestalten"; null = unbekannt)</summary>
    public Func<int?>? TileCount { get; set; }

    public StartView()
    {
        InitializeComponent();
    }

    public void Init(AgentCore core)
    {
        _core = core;
        ApplyTexts();
        core.Link.StatusChanged += () => Dispatcher.UIThread.Post(Refresh);
        core.Link.ConnectionChanged += (_, _) => Dispatcher.UIThread.Post(Refresh);
        core.CalendarChanged += () => Dispatcher.UIThread.Post(Refresh);
        core.Radio.Changed += () => Dispatcher.UIThread.Post(Refresh);
        // Solange die Seite sichtbar ist, alle paar Sekunden auffrischen (Kabel ein- und ausstecken ...)
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _timer.Tick += (_, _) => Refresh();
        AttachedToVisualTree += (_, _) => { _timer.Start(); Refresh(); };
        DetachedFromVisualTree += (_, _) => _timer.Stop();
    }

    public void ApplyTexts() => Refresh();

    private record Card(string Icon, string Title, string Text, IBrush Brush, int Target);

    private void Refresh()
    {
        if (_core == null) return;
        var link = _core.Link;
        var status = link.Status;
        var connected = link.IsConnected;

        StartHeader.Text = Strings.Get("start.title");

        var cards = new List<Card>();

        // Display
        if (connected) cards.Add(new Card("monitor-dashboard", Strings.Get("start.display"), Strings.Format("start.display.ok", link.Device?.Firmware ?? "?"), Ok, 4));
        else
        {
            var uart = PortFinder.FindUartPorts();
            cards.Add(new Card("monitor-dashboard", Strings.Get("start.display"), uart.Count > 0 ? Strings.Get("start.display.uart") : Strings.Get("start.display.none"), Bad, 4));
        }

        // WLAN, Home Assistant, MQTT: vom Gerät gemeldet
        string Net(bool? value) => !connected || value == null ? Strings.Get("conn.nodevice") : Strings.Get(value.Value ? "conn.connected" : "conn.notconnected");
        IBrush NetBrush(bool? value) => !connected || value == null ? Idle : value.Value ? Ok : Bad;
        cards.Add(new Card("wifi", "WLAN", Net(status?.Wifi), NetBrush(status?.Wifi), 3));
        cards.Add(new Card("home-assistant", "Home Assistant",
            Settings.HaUrl.Length == 0 || Settings.HaToken.Length == 0 ? Strings.Get("conn.notset") : Net(status?.Ha),
            Settings.HaUrl.Length == 0 || Settings.HaToken.Length == 0 ? Idle : NetBrush(status?.Ha), 3));
        cards.Add(new Card("bell-alert", "MQTT",
            Settings.MqttHost.Length == 0 ? Strings.Get("conn.notset") : Net(status?.Mqtt),
            Settings.MqttHost.Length == 0 ? Idle : NetBrush(status?.Mqtt), 3));
        cards.Add(new Card("calendar", Strings.Get("cal.title"),
            Settings.CalendarUrl.Length == 0 ? Strings.Get("conn.notset") : Strings.Format("cal.ok", _core.CalendarLines.Count),
            Settings.CalendarUrl.Length == 0 ? Idle : Ok, 3));
        var station = _core.Radio.CurrentStation;
        cards.Add(new Card("radio", "Radio",
            station == null ? Strings.Get("conn.notset") : (_core.Radio.Playing ? "▶ " : "") + station.Name, station == null ? Idle : _core.Radio.Playing ? Ok : Idle, 2));

        StatusPanel.Children.Clear();
        foreach (var c in cards) StatusPanel.Children.Add(MakeCard(c));

        // Nächste Schritte
        var wifiAndHa = connected && status is { Wifi: true, Ha: true };
        var haSet = Settings.HaUrl.Length > 0 && Settings.HaToken.Length > 0;
        var tiles = TileCount?.Invoke();
        var steps = new List<(bool Done, string Text, string? Button, int Target)>
        {
            (connected, Strings.Get("start.step.fw"), connected ? null : Strings.Get("start.go.device"), 4),
            (wifiAndHa || (connected && status is { Wifi: true } && !haSet), Strings.Get("start.step.net"), Strings.Get("start.go.conn"), 3),
            (Settings.MqttHost.Length > 0, Strings.Get("start.step.mqtt"), Settings.MqttHost.Length > 0 ? null : Strings.Get("start.go.setup"), 3),
            (tiles is > 0, Strings.Get("start.step.display"), tiles == 0 ? Strings.Get("start.go.template") : Strings.Get("start.go.display"), tiles == 0 ? 10 : 1),
        };
        StepsHeader.Text = Strings.Get("start.steps");
        var open = steps.Count(s => !s.Done);
        StartSub.Text = open == 0 ? Strings.Get("start.sub.ok") : Strings.Format("start.sub.open", open);
        StepsPanel.Children.Clear();
        for (var i = 0; i < steps.Count; i++)
        {
            var (done, text, button, target) = steps[i];
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(14, 10) };
            var mark = new TextBlock { Text = MdiIcons.Glyph(done ? "check-circle" : "circle-outline"), FontFamily = MdiIcons.Font, FontSize = 20, Foreground = done ? Ok : Idle, VerticalAlignment = VerticalAlignment.Center };
            var label = new TextBlock { Text = text, Margin = new Thickness(12, 0), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
            Grid.SetColumn(label, 1);
            row.Children.Add(mark);
            row.Children.Add(label);
            if (button != null)
            {
                var go = new Button { Content = button, VerticalAlignment = VerticalAlignment.Center };
                var t = target;
                go.Click += (_, _) => NavigateRequested?.Invoke(t);
                Grid.SetColumn(go, 2);
                row.Children.Add(go);
            }
            StepsPanel.Children.Add(new Border { Child = row, BorderThickness = new Thickness(0, i == 0 ? 0 : 1, 0, 0), BorderBrush = new SolidColorBrush(Color.Parse("#30808080")) });
        }
    }

    private Control MakeCard(Card c)
    {
        var icon = new TextBlock { Text = MdiIcons.Glyph(c.Icon), FontFamily = MdiIcons.Font, FontSize = 18, Foreground = c.Brush, VerticalAlignment = VerticalAlignment.Center };
        var title = new TextBlock { Text = c.Title, FontWeight = FontWeight.SemiBold };
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { icon, title } };
        var text = new TextBlock { Text = c.Text, FontSize = 12, Opacity = 0.8, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
        var card = new Border
        {
            Width = 250, MinHeight = 66, Margin = new Thickness(0, 0, 10, 10), Padding = new Thickness(12),
            CornerRadius = new CornerRadius(8), Background = new SolidColorBrush(Color.Parse("#10808080")),
            BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.Parse("#30808080")),
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            Child = new StackPanel { Children = { head, text } },
        };
        var target = c.Target;
        card.PointerPressed += (_, _) => NavigateRequested?.Invoke(target);
        return card;
    }
}
