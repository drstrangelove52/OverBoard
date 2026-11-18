using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using OverBoardAgent.Core;

namespace OverBoardAgent.Editor;

/// <summary>
/// Der Display-Editor: zeigt das Display genau so wie auf dem Gerät. Kacheln lassen sich anklicken, verschieben,
/// vergrössern, anlegen und löschen. Änderungen gehen (wenn "Live" aktiv ist) sofort an das Gerät.
/// </summary>
public partial class EditorWindow : Window
{
    private static readonly FontFamily IconFont = new("avares://OverBoardAgent/Assets/Fonts#Material Design Icons");

    private static readonly string[] FormatIds = ["power", "kwh", "status", "onoff", "text", "percent", "time", "mbit"];
    private static readonly (string De, string En)[] FormatNames =
    [
        ("Leistung (kW)", "Power (kW)"), ("Energie (kWh)", "Energy (kWh)"), ("Status", "Status"),
        ("An / Aus", "On / Off"), ("Text", "Text"), ("Prozent", "Percent"), ("Uhrzeit", "Time"), ("Netzwerk (Mbit/s)", "Network (Mbit/s)"),
    ];

    private static readonly string[] TriggerIds = ["none", "tap", "hold", "confirm", "repeat"];
    private static readonly (string De, string En)[] TriggerNames =
    [
        ("Keine Aktion", "No action"), ("Antippen", "Tap"), ("Gedrückt halten", "Press and hold"), ("Antippen und bestätigen", "Tap and confirm"), ("Gedrückt halten wiederholt", "Hold to repeat"),
    ];

    // Werte im Ruhemodus: zwei Plätze
    private readonly TextBox[] _idleSrc = new TextBox[2];
    private readonly TextBox[] _idleDe = new TextBox[2];
    private readonly ComboBox[] _idleFmt = new ComboBox[2];
    private readonly TextBlock[] _idleHead = new TextBlock[2];
    private TextBlock? _idleIntro;

    // Echte Werte in der Vorschau: Home Assistant (alle 30 s geholt) und Zustände vom PC
    private Dictionary<string, string> _haStates = [];
    private readonly DispatcherTimer _valuesTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    // Zustände vom PC (Lautstärke, Stumm …) kommen sofort als Ereignis; mehrere in kurzer Zeit gibt eine Neuzeichnung
    private readonly DispatcherTimer _pcRedraw = new() { Interval = TimeSpan.FromMilliseconds(120) };

    private readonly AgentCore? _core;
    private ConfigModel _cfg = new();
    private int _page;
    private TileModel? _sel;
    private readonly HashSet<TileModel> _on = [];
    private readonly List<string> _undo = [];
    private readonly List<string> _redo = [];
    private string? _lastTouchKey;
    private DateTime _lastTouch;
    private bool _suppress;
    private bool _sending;
    private bool _sendAgain;
    private bool _dirty;
    private readonly DispatcherTimer _sendTimer = new() { Interval = TimeSpan.FromMilliseconds(450) };

    // Ziehen
    private enum Drag { None, Move, Resize }
    private Drag _drag;
    private int _grabCol, _grabRow;
    private Rect? _ghost;
    private bool _ghostValid;
    private (int X, int Y, int W, int H) _ghostCells;
    private bool _moved;

    public EditorWindow() : this(null!) { }

    public EditorWindow(AgentCore core)
    {
        InitializeComponent();
        _core = core;
        if (core == null) return;   // nur für den Designer

        IconBox.ItemsSource = MdiIcons.Names;
        PageIcon.ItemsSource = MdiIcons.Names;
        IconPreview.FontFamily = IconFont;
        BuildIdleValues();
        BuildNav();
        ActionsPage.Init(core);
        RadioPage.Init(core);
        ActionsPage.UsedBy = ActionUsage;
        StartPage.Init(core);
        StartPage.NavigateRequested += async i =>
        {
            if (i == 10) { ShowPage(1); await AddFromTemplateAsync(); }      // 10 = "Aus Vorlage starten"
            else ShowPage(i);
        };
        StartPage.TileCount = () => _cfg.Pages.Count == 0 ? null : _cfg.Pages.Sum(p => p.Tiles.Count);
        ConnectionsPage.Init(core);
        FirmwarePage.Init(core);
        AppPage.Init(core, OnAppLanguageChanged);
        ApplyTexts();
        WireEvents();
        ShowPage(0);
        UpdateConnection();
        _core.Link.ConnectionChanged += OnConnectionChanged;

        _sendTimer.Tick += async (_, _) =>
        {
            _sendTimer.Stop();
            await SendAsync(auto: true);
        };

        _pcRedraw.Tick += (_, _) => { _pcRedraw.Stop(); Rebuild(); };
        _core.Platform.StateChanged += OnPcStateChanged;
        _valuesTimer.Tick += async (_, _) => await RefreshValuesAsync();
        _valuesTimer.Start();
        Opened += async (_, _) => { FitToScreen(); await LoadInitialAsync(); await RefreshValuesAsync(); };
        Closing += (_, _) =>
        {
            // Grösse merken (nur im normalen Zustand, nicht maximiert)
            if (WindowState == WindowState.Normal)
            {
                Settings.WindowWidth = Width;
                Settings.WindowHeight = Height;
                Settings.Save();
            }
        };
        Closed += (_, _) =>
        {
            _sendTimer.Stop();
            _valuesTimer.Stop();
            _pcRedraw.Stop();
            _core.Platform.StateChanged -= OnPcStateChanged;
            _core.Actions.Changed -= OnActionsChanged;
            _core.Link.ConnectionChanged -= OnConnectionChanged;
            ActionsPage.Detach();
            RadioPage.Detach();
        };
        _core.Actions.Changed += OnActionsChanged;
    }

    /// <summary>
    /// Passt die Fenstergrösse an den Bildschirm an: die zuletzt benutzte Grösse, sonst die Standardgrösse, jedenfalls höchstens
    /// 92 % des nutzbaren Bereichs (ohne Taskleiste). So ist das Fenster auf einem Notebook nicht zu gross.
    /// </summary>
    private void FitToScreen()
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen == null) return;
        var scale = screen.Scaling <= 0 ? 1.0 : screen.Scaling;
        var maxW = screen.WorkingArea.Width / scale * 0.92;
        var maxH = screen.WorkingArea.Height / scale * 0.92;
        var wantW = Settings.WindowWidth >= MinWidth ? Settings.WindowWidth : Width;
        var wantH = Settings.WindowHeight >= MinHeight ? Settings.WindowHeight : Height;
        Width = Math.Max(MinWidth > maxW ? maxW : MinWidth, Math.Min(wantW, maxW));
        Height = Math.Max(MinHeight > maxH ? maxH : MinHeight, Math.Min(wantH, maxH));
        // in der Mitte des nutzbaren Bereichs
        var x = screen.WorkingArea.X + (int)((screen.WorkingArea.Width - Width * scale) / 2);
        var y = screen.WorkingArea.Y + (int)((screen.WorkingArea.Height - Height * scale) / 2);
        Position = new PixelPoint(x, y);
    }

    private static string T(string de, string en) => Strings.Language == "en" ? en : de;

    // Zeiten stehen im Gerät in Sekunden, in der Oberfläche überall in Minuten (0,5 = 30 Sekunden)
    private static decimal Minutes(int seconds) => Math.Round(seconds / 60m, 2);
    private static int Seconds(decimal minutes) => (int)Math.Round(minutes * 60m);

    // Das Radio-Logo wird auf die Kachelfarbe des gewählten Designs gesetzt
    private void SyncRadioTheme()
    {
        if (_core == null || _core.Radio.Theme == _cfg.Theme) return;
        _core.Radio.Theme = _cfg.Theme;
        _core.Radio.PushToDevice();
    }

    // Ablauf als Zeitstrahl, immer ab der letzten Berührung
    private void UpdatePowerSummary()
    {
        static string M(int s) => Math.Round(s / 60m, 2).ToString("0.##");
        var idle = _cfg.Idle.TimeoutS;
        var de = $"Ablauf ab der letzten Berührung:  nach {M(idle)} Min Bildschirmschoner";
        var en = $"Sequence from the last touch:  after {M(idle)} min screen saver";
        if (_cfg.Idle.OffS > 0) { de += $"  →  nach {M(idle + _cfg.Idle.OffS)} Min Display aus"; en += $"  →  after {M(idle + _cfg.Idle.OffS)} min display off"; }
        else { de += "  →  Display bleibt an"; en += "  →  display stays on"; }
        if (_cfg.Power.PcAway)
        {
            de += $"{Environment.NewLine}Ist der PC weg: schon nach {M(_cfg.Power.PcAwayDelayS)} Min Display aus";
            en += $"{Environment.NewLine}If the PC is away: display off after {M(_cfg.Power.PcAwayDelayS)} min";
        }
        PowerSummary.Text = T(de, en);
    }

    private PageModel Page => _cfg.Pages[Math.Clamp(_page, 0, Math.Max(0, _cfg.Pages.Count - 1))];

    // ---- Texte ---------------------------------------------------------------------------------

    private void ApplyTexts()
    {
        Title = "OverBoard";
        AppTitle.Text = "OverBoard";
        for (var i = 0; i < _navLabels.Length; i++) _navLabels[i].Text = Strings.Get(NavKeys[i]);
        ContentTabActions.Header = Strings.Get("tab.actions");
        ContentTabRadio.Header = Strings.Get("tab.radio");
        DevTabDevice.Header = Strings.Get("tab.device");
        DevTabLook.Header = Strings.Get("tab.look");
        DevTabPower.Header = Strings.Get("tab.power");
        ConnectionHeader.Text = T("Verbindung", "Connection");
        FirmwareHeader.Text = T("Firmware", "Firmware");
        DisplayCardHeader.Text = T("Anzeige des Geräts", "Device display");
        DisplayCardHint.Text = T("Änderungen hier gehen sofort aufs Gerät (wie im Display-Editor). Die Sprache gilt nur für das Display, nicht für dieses Programm.",
                                 "Changes here are sent to the device immediately (as in the display editor). The language applies to the display only, not to this program.");
        IdleHeader.Text = T("Bildschirmschoner und Energiesparen", "Screen saver and power saving");
        IdleHint.Text = T("Alle Zeiten sind in Minuten (0,5 = 30 Sekunden). Der Ablauf: Ohne Berührung startet der Bildschirmschoner, danach geht das Display ganz aus. Berührung und Hinweise (Banner, Alarm) wecken es.",
                          "All times are in minutes (0.5 = 30 seconds). The sequence: without touch the screen saver starts, then the display switches off completely. Touch and notifications (banners, alarms) wake it.");
        SaverHeader.Text = T("1 · Bildschirmschoner", "1 · Screen saver");
        PowerHeader.Text = T("2 · Display ganz ausschalten (Strom sparen)", "2 · Switch the display off completely (save power)");
        PowerHint.Text = T("Sinnvoll, wenn das Gerät an einem USB-Anschluss mit dauerndem Strom hängt (Hub, Dockingstation). Das Gerät läuft weiter und empfängt Hinweise, nur die Hintergrundbeleuchtung ist aus.",
                           "Useful if the device is on a USB port that is always powered (hub, docking station). The device keeps running and receives notifications, only the backlight is off.");
        OffMinLabel.Text = T("Display ausschalten nach weiteren … Minuten (0 = nie)", "Switch display off after a further … minutes (0 = never)");
        PcAwayBox.Content = T("Wenn der PC schläft, gesperrt oder aus ist, Display schon früher ausschalten", "When the PC sleeps, is locked or is off, switch the display off sooner");
        AwayDelayLabel.Text = T("… und zwar nach … Minuten ohne Berührung (statt Bildschirmschoner)", "… namely after … minutes without touch (instead of the screen saver)");
        SoundHeader.Text = T("Töne", "Sounds");
        SoundHint.Text = T("Die Lautstärke gilt für das Gerät. Mit den Testknöpfen hörst du das Ergebnis sofort (das Gerät muss verbunden sein).",
                           "The volume applies to the device. Use the test buttons to hear the result immediately (the device must be connected).");
        ClickBox.Content = T("Tastenklick beim Berühren", "Key click when touching");
        ClickVolumeLabel.Text = T("Lautstärke des Tastenklicks (%)", "Key click volume (%)");
        SoundVolumeLabel.Text = T("Lautstärke der Hinweistöne (%, 0 = aus)", "Notification sound volume (%, 0 = off)");
        ClickTestButton.Content = T("Klick testen", "Test click");
        SoundTestButton.Content = T("Hinweiston testen", "Test notification");
        UpdateConnection();
        UndoButton.Content = T("↶ Rückgängig", "↶ Undo");
        RedoButton.Content = T("↷ Wiederholen", "↷ Redo");
        SendButton.Content = T("Jetzt senden", "Send now");
        LiveBox.Content = T("Live auf das Gerät", "Live to device");
        DemoBox.Content = T("Vorführmodus (Kacheln bedienen)", "Demo mode (operate tiles)");
        ToolTip.SetTip(DemoBox, T("Ein Klick auf eine Taste löst sie aus, wie eine Berührung am Display. Das Raster und die Auswahl sind ausgeblendet.", "A click on a button triggers it, like touching the display. The grid and the selection are hidden."));
        RealValuesBox.Content = T("Echte Werte anzeigen", "Show real values");
        LoadButton.Content = T("Vom Gerät laden", "Load from device");
        OpenFileButton.Content = T("Laden …", "Load …");
        SaveFileButton.Content = T("Speichern unter …", "Save as …");
        ResetButton.Content = T("Auf Standard", "Reset to default");
        JsonButton.Content = T("JSON …", "JSON …");

        PagesLabel.Text = T("Seiten", "Pages");
        AddPageButton.Content = T("+ Seite", "+ Page");
        PageSettingsButton.Content = Strings.Get("ed.page.edit");
        TemplateButton.Content = Strings.Get("tpl.button");
        ModeDesignBtn.Content = Strings.Get("ed.design");
        ModeDemoBtn.Content = Strings.Get("ed.demo");
        ToolTip.SetTip(MoreButton, Strings.Get("ed.more"));
        TypeValueBtn.Content = Strings.Get("ed.type.value");
        TypeChartBtn.Content = Strings.Get("ed.type.chart");
        SpanLabel.Text = Strings.Get("ed.span");
        FillCombo(SpanBox, SpanNames());
        AddChartButton.Content = Strings.Get("ed.add.chart");
        CopyTileButton.Content = Strings.Get("ed.copy");
        CutTileButton.Content = Strings.Get("ed.cut");
        UpdatePasteButton();
        TypeButtonBtn.Content = Strings.Get("ed.type.button");
        TypeCalBtn.Content = Strings.Get("ed.type.cal");
        TypeRadioBtn.Content = Strings.Get("ed.type.radio");
        ContentLabel.Text = Strings.Get("ed.content");
        VolStepLabel.Text = Strings.Get("win.volstep");
        VolRateLabel.Text = Strings.Get("win.volrate");
        VolNote.Text = Strings.Get("ed.vol.note");
        ModePcBtn.Content = Strings.Get("ed.mode.pc");
        ModeHaBtn.Content = Strings.Get("ed.mode.ha");
        AdvancedExpander.Header = Strings.Get("ed.advanced");
        AddCalendarButton.Content = Strings.Get("ed.add.cal");
        AddRadioButton.Content = Strings.Get("ed.add.radio");
        UpdateLiveStatus();
        DeletePageButton.Content = T("Seite löschen", "Delete page");
        PageTitleDeLabel.Text = T("Titel der Seite", "Page title");
        PageIconLabel.Text = T("Symbol der Seite", "Page icon");
        PageRowsLabel.Text = T("Reihen auf dieser Seite", "Rows on this page");
        AddLabel.Text = Strings.Get("ed.add");
        AddValueButton.Content = Strings.Get("ed.add.value");
        AddButtonButton.Content = Strings.Get("ed.add.button");

        LanguageLabel.Text = T("Sprache des Displays", "Display language");
        ThemeLabel.Text = T("Farbdesign", "Colour theme");
        BrightnessLabel.Text = T("Helligkeit (%)", "Brightness (%)");
        HoldLabel.Text = T("Haltedauer für „Gedrückt halten“ (ms)", "Hold time for “press and hold” (ms)");
        IdleLabel.Text = T("Starten nach … Minuten ohne Berührung", "Start after … minutes without touch");
        IdleBrightLabel.Text = T("Helligkeit (%)", "Brightness (%)");
        IdleMoveLabel.Text = T("Uhr wechselt die Stelle alle … Minuten (gegen Einbrennen)", "Clock changes position every … minutes (against burn-in)");

        TileHeader.Text = T("Kachel", "Tile");
        NoTileText.Text = T("Kachel auf dem Display anklicken. Ziehen verschiebt sie, das blaue Quadrat unten rechts vergrössert sie. Entf löscht.",
                            "Click a tile on the display. Drag to move it, the blue square at the bottom right resizes it. Del deletes.");
        TypeLabel.Text = T("Art", "Type");
        TitleDeLabel.Text = T("Titel", "Title");
        IconLabel.Text = T("Symbol (Name tippen, z. B. lightbulb)", "Icon (type a name, e.g. lightbulb)");
        SourceKindLabel.Text = T("Datenquelle", "Data source");
        ButtonModeLabel.Text = Strings.Get("ed.what");
        FillCombo(ButtonModeBox, [T("Aktion auf dem PC ausführen", "Run an action on the PC"), T("Home-Assistant-Entität umschalten", "Toggle a Home Assistant entity")]);
        AdvLightBox.Content = T("Erweitert: leuchtet nach einem anderen Zustand", "Advanced: lights up for a different state");
        SourceLabel.Text = T("Home-Assistant-Entität (z. B. sensor.xyz)", "Home Assistant entity (e.g. sensor.xyz)");
        FillCombo(SourceKindBox, [T("PC", "PC"), T("Home Assistant", "Home Assistant")]);
        FillCombo(PcSourceBox, PcSources.Select(p => T(p.De, p.En)).ToList());
        FormatLabel.Text = T("Darstellung", "Format");
        MaxLabel.Text = T("Höchstwert (z. B. für Balken, 0 = keiner)", "Maximum (0 = none)");
        TriggerLabel.Text = T("Bedienung", "Operation");
        ActionLabel.Text = T("Dann ausführen (Aktion auf dem PC)", "Then run (action on the PC)");
        NewActionButton.Content = T("Neue eigene Aktion …", "New custom action …");
        TileHoldLabel.Text = T("Haltedauer dieser Taste (ms, 0 = allgemeine)", "Hold time of this button (ms, 0 = global)");
        GeometryLabel.Text = T("Position und Grösse: Spalte, Reihe, Breite, Höhe", "Position and size: column, row, width, height");
        GeometryHint.Text = T("Spalten 1–8; die Kachel muss im Raster liegen und darf keine andere überlappen.",
                              "Columns 1–8; the tile must fit the grid and must not overlap another.");
        OnBox.Content = T("In der Vorschau als „eingeschaltet“ zeigen", "Show as “on” in the preview");
        DeleteTileButton.Content = T("Kachel löschen", "Delete tile");

        FillCombo(TypeBox, [T("Anzeige (Wert)", "Display (value)"), T("Taste", "Button"), T("Kalender (Termine)", "Calendar (events)"), T("Radio (Player)", "Radio (player)"), T("Verlauf (Linie)", "Chart (line)")]);
        FillCombo(FormatBox, FormatNames.Select(f => T(f.De, f.En)).ToList());
        FillCombo(TriggerBox, TriggerNames.Select(f => T(f.De, f.En)).ToList());
        FillCombo(LanguageBox, [T("Deutsch", "German"), T("Englisch", "English")]);
        FillCombo(ThemeBox, [T("Dunkel", "Dark"), T("Hell", "Light")]);
        for (var i = 0; i < 2; i++)
        {
            FillCombo(_idleFmt[i], FormatNames.Select(f => T(f.De, f.En)).ToList());
            _idleHead[i].Text = T($"Wert {i + 1} unter der Uhr", $"Value {i + 1} below the clock");
            _idleSrc[i].Watermark = T("Quelle, z. B. sensor.xyz (leer = nicht anzeigen)", "Source, e.g. sensor.xyz (empty = hidden)");
            _idleDe[i].Watermark = T("Titel", "Title");
        }
        _idleIntro!.Text = T("Im Ruhemodus werden unter der Uhr bis zu zwei Werte angezeigt.", "In idle mode up to two values are shown below the clock.");
    }

    // Werte des PCs, die eine Anzeige-Kachel zeigen kann (Kennung ohne "pc:", Namen, passende Darstellung)
    private static readonly (string Id, string De, string En, string Format)[] PcSources =
    [
        ("volume", "Lautstärke", "Volume", "percent"),
        ("mute", "Stumm", "Muted", "onoff"),
        ("mic_mute", "Mikrofon stumm", "Microphone muted", "onoff"),
        ("cpu", "CPU-Auslastung", "CPU load", "percent"),
        ("ram", "Arbeitsspeicher", "Memory", "percent"),
        ("net_down", "Netzwerk: Download", "Network: download", "mbit"),
        ("net_up", "Netzwerk: Upload", "Network: upload", "mbit"),
    ];

    // Arten der Datenquelle: Tasten haben zusätzlich "Keine" (die Taste löst nur etwas aus und leuchtet nie)
    private string[] _srcKinds = ["pc", "ha"];

    private static string[] KindsFor(TileModel t) => t.IsButton ? ["none", "pc", "ha"] : ["pc", "ha"];

    private static string KindText(string kind) => kind switch
    {
        "none" => T("Keine", "None"),
        "pc" => T("PC", "PC"),
        _ => T("Home Assistant", "Home Assistant"),
    };

    // Tasten, bei denen "Erweitert: leuchtet nach einem anderen Zustand" aufgeklappt ist
    private readonly HashSet<TileModel> _advTiles = [];

    // Tasten, die gerade auf "Home-Assistant-Entität umschalten" stehen, aber noch keine Entität haben
    private readonly HashSet<TileModel> _haModeTiles = [];

    // Stumm-Tasten leuchten von selbst nach dem Zustand, den sie umschalten
    private static string? AutoSource(string? action) => action switch
    {
        "pc:mute.toggle" => "pc:mute",
        "pc:mic.toggle" => "pc:mic_mute",
        _ => null,
    };

    private static readonly int[] SpanSeconds = [60, 120, 300, 900, 3600, 21600, 86400];
    private static string[] SpanNames() => [T("1 Minute", "1 minute"), T("2 Minuten", "2 minutes"), T("5 Minuten", "5 minutes"), T("15 Minuten", "15 minutes"), T("1 Stunde", "1 hour"), T("6 Stunden", "6 hours"), T("24 Stunden", "24 hours")];

    private static bool IsPcSource(string? source) => source != null && source.StartsWith("pc:", StringComparison.Ordinal);

    private static string? GuessFormat(HaEntity e) => e.Id is "pc:mute" or "pc:mic_mute" ? "onoff" : e.DeviceClass == "timestamp" ? "time" : e.Unit switch
    {
        "W" or "kW" => "power",
        "kWh" or "Wh" => "kwh",
        "%" => "percent",
        _ => null,
    };

    /// <summary>Öffnet die Entitäten-Auswahl, oder weist auf die fehlenden Zugangsdaten hin.</summary>
    private async Task<HaEntity?> PickEntityAsync(bool withPc = true)
    {
        if (!withPc && (string.IsNullOrWhiteSpace(Settings.HaToken) || string.IsNullOrWhiteSpace(Settings.HaUrl)))
        {
            Status(Strings.Get("ha.err.token"), error: true);
            ShowPage(3);                     // Seite "Verbindungen": dort sind Adresse und Token
            return null;
        }
        // Werte des PCs stehen immer zur Wahl, Home-Assistant-Entitäten zusätzlich, wenn Home Assistant eingerichtet ist
        var states = _core?.Platform.GetStates() ?? new Dictionary<string, string>();
        string S(string id) => states.TryGetValue(id, out var v) ? v : "";
        var pc = new List<HaEntity>
        {
            new("pc:volume", T("PC: Lautstärke", "PC: volume"), S("volume"), "%", ""),
            new("pc:mute", T("PC: Stumm", "PC: muted"), S("mute"), "", ""),
            new("pc:mic_mute", T("PC: Mikrofon stumm", "PC: microphone muted"), S("mic_mute"), "", ""),
            new("pc:cpu", T("PC: CPU-Auslastung", "PC: CPU load"), S("cpu"), "%", ""),
            new("pc:ram", T("PC: Arbeitsspeicher", "PC: memory"), S("ram"), "%", ""),
            new("pc:net_down", T("PC: Netzwerk Download", "PC: network download"), S("net_down"), "Mbit/s", ""),
            new("pc:net_up", T("PC: Netzwerk Upload", "PC: network upload"), S("net_up"), "Mbit/s", ""),
        };
        return await new EntityPickerWindow(withPc ? pc : null).ShowDialog<HaEntity?>(this);
    }

    private void BuildIdleValues()
    {
        _idleIntro = new TextBlock { FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap };
        IdleValuesPanel.Children.Add(_idleIntro);
        for (var i = 0; i < 2; i++)
        {
            _idleHead[i] = new TextBlock { FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 6, 0, 0) };
            _idleSrc[i] = new TextBox();
            _idleDe[i] = new TextBox();
            _idleFmt[i] = new ComboBox { HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
            IdleValuesPanel.Children.Add(_idleHead[i]);
            var pick = new Button { Content = "…" };
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
            row.Children.Add(_idleSrc[i]);
            Grid.SetColumn(pick, 1);
            row.Children.Add(pick);
            var slot = i;
            pick.Click += async (_, _) =>
            {
                var e = await PickEntityAsync();
                if (e == null) return;
                if (string.IsNullOrWhiteSpace(_idleDe[slot].Text) && e.Name.Length > 0) _idleDe[slot].Text = e.Name;
                var guess = GuessFormat(e);
                if (guess != null) _idleFmt[slot].SelectedIndex = Array.IndexOf(FormatIds, guess);
                _idleSrc[slot].Text = e.Id;
            };
            IdleValuesPanel.Children.Add(row);
            IdleValuesPanel.Children.Add(_idleDe[i]);
            IdleValuesPanel.Children.Add(_idleFmt[i]);
            _idleSrc[i].TextChanged += (_, _) => ApplyIdleValues();
            _idleDe[i].TextChanged += (_, _) => ApplyIdleValues();
            _idleFmt[i].SelectionChanged += (_, _) => ApplyIdleValues();
        }
    }

    /// <summary>Standardwerte des Geräts, wenn die Konfiguration keine festlegt.</summary>
    private static List<IdleValueModel> DefaultIdleValues() =>
    [
        new() { Source = "sensor.inverter_active_power", Title = new LText("Solar", "Solar"), Format = "power" },
        new() { Source = "sensor.power_consumption_house", Title = new LText("Haus", "House"), Format = "power" },
    ];

    private void ApplyIdleValues()
    {
        if (_suppress || _cfg.Pages.Count == 0) return;
        Touch("idlevals");
        var list = new List<IdleValueModel>();
        for (var i = 0; i < 2; i++)
        {
            if (string.IsNullOrWhiteSpace(_idleSrc[i].Text)) continue;
            var fmt = _idleFmt[i].SelectedIndex >= 0 ? FormatIds[_idleFmt[i].SelectedIndex] : "text";
            // bestehendes Höchstmass der Quelle beibehalten
            var old = _cfg.Idle.Values?.FirstOrDefault(v => v.Source == _idleSrc[i].Text!.Trim());
            list.Add(new IdleValueModel
            {
                Source = _idleSrc[i].Text!.Trim(),
                Title = new LText(_idleDe[i].Text ?? "", _idleDe[i].Text ?? ""),
                Format = fmt,
                Max = old?.Max,
            });
        }
        _cfg.Idle.Values = list;
        Changed();
    }

    private static void FillCombo(ComboBox box, IReadOnlyList<string> items)
    {
        var selected = box.SelectedIndex;
        box.SelectedIndex = -1;
        box.Items.Clear();
        foreach (var i in items) box.Items.Add(i);
        box.SelectedIndex = selected < items.Count ? selected : -1;
    }

    // ---- Ereignisse verdrahten ----------------------------------------------------------------

    private void WireEvents()
    {
        UndoButton.Click += (_, _) => DoUndo();
        RedoButton.Click += (_, _) => DoRedo();
        SendButton.Click += async (_, _) => await SendAsync(auto: false);
        RealValuesBox.IsCheckedChanged += async (_, _) => { _realValuesOn = RealValuesBox.IsChecked == true; await RefreshValuesAsync(); Rebuild(); };
        LiveBox.IsCheckedChanged += (_, _) => { if (LiveBox.IsChecked == true && _dirty) ScheduleSend(); };
        DemoBox.IsCheckedChanged += (_, _) => { _drag = Drag.None; SyncMode(); Rebuild(); };
        ModeDesignBtn.Click += (_, _) => { DemoBox.IsChecked = false; SyncMode(); };
        ModeDemoBtn.Click += (_, _) => { DemoBox.IsChecked = true; SyncMode(); };
        LiveBox.IsCheckedChanged += (_, _) => UpdateLiveStatus();
        SyncMode();
        LoadButton.Click += async (_, _) => await LoadFromDeviceAsync();
        OpenFileButton.Click += async (_, _) => await OpenFileAsync();
        SaveFileButton.Click += async (_, _) => await SaveFileAsync();
        ResetButton.Click += async (_, _) => await ResetAsync();
        JsonButton.Click += async (_, _) => await ShowJsonAsync();
        SourcePickButton.Click += async (_, _) =>
        {
            var e = await PickEntityAsync(withPc: false);
            if (e == null || _sel == null) return;
            SourceBox.Text = e.Id;      // löst die normale Übernahme aus
            // leere Felder sinnvoll vorbelegen: Titel aus dem Namen, Darstellung aus der Einheit
            if (string.IsNullOrWhiteSpace(TitleDe.Text) && e.Name.Length > 0)
            {
                TitleDe.Text = e.Name;
            }
            var guess = GuessFormat(e);
            if (guess != null && !_sel.IsButton) FormatBox.SelectedIndex = Array.IndexOf(FormatIds, guess);
        };
        IconPickButton.Click += async (_, _) =>
        {
            var name = await new IconPickerWindow(_sel?.Icon).ShowDialog<string?>(this);
            if (name != null) IconBox.Text = name;     // löst die normale Übernahme aus
        };
        PageIconPick.Click += async (_, _) =>
        {
            var name = await new IconPickerWindow(Page.Icon).ShowDialog<string?>(this);
            if (name != null) PageIcon.Text = name;
        };

        // Display: Zeiger
        Host.AddHandler(PointerPressedEvent, OnHostPressed, handledEventsToo: true);
        Host.AddHandler(PointerMovedEvent, OnHostMoved, handledEventsToo: true);
        Host.AddHandler(PointerReleasedEvent, OnHostReleased, handledEventsToo: true);
        KeyDown += OnKeyDown;

        // Seiten
        PageList.SelectionChanged += (_, _) =>
        {
            if (_suppress || PageList.SelectedIndex < 0) return;
            _page = PageList.SelectedIndex;
            _sel = null;
            Refresh(panels: true);
            if (LiveBox.IsChecked == true) { Log.Info($"Seite {_page + 1} ans Gerät"); _core?.Link.Send(Protocol.Page(_page)); }     // das Display zeigt dieselbe Seite
        };
        AddPageButton.Click += (_, _) => AddPage();
        TemplateButton.Click += async (_, _) => await AddFromTemplateAsync();
        DeletePageButton.Click += (_, _) => DeletePage();
        // Titel sind ein einfacher Text (gilt in jeder Sprache); mehrsprachig sind nur die eingebauten Texte des Programms und des Geräts
        PageTitleDe.TextChanged += (_, _) => EditPage("pt", p => p.Title = new LText(PageTitleDe.Text ?? "", PageTitleDe.Text ?? ""), listToo: true);
        PageIcon.TextChanged += (_, _) => EditPage("pi", p => p.Icon = string.IsNullOrWhiteSpace(PageIcon.Text) ? null : PageIcon.Text, validIcon: PageIcon.Text);
        PageRows.ValueChanged += (_, _) =>
        {
            if (_suppress || _cfg.Pages.Count == 0 || PageRows.Value is not { } v) return;
            var rows = (int)v;
            if (Page.Tiles.Any(t => t.Y + t.H > rows))
            {
                Status(T("Mit weniger Reihen würden Kacheln aus dem Raster ragen. Zuerst Kacheln nach oben schieben oder löschen.",
                         "Fewer rows would push tiles out of the grid. Move or delete tiles first."), error: true);
                _suppress = true;
                PageRows.Value = Page.Rows;
                _suppress = false;
                return;
            }
            Touch("rows");
            Page.Rows = rows;
            Changed();
        };
        AddValueButton.Click += (_, _) => AddTile("value");
        AddButtonButton.Click += (_, _) => AddTile("button");
        AddCalendarButton.Click += (_, _) => AddTile("calendar");
        AddRadioButton.Click += (_, _) => AddTile("radio");
        AddChartButton.Click += (_, _) => AddTile("chart");

        // Geräteeinstellungen
        LanguageBox.SelectionChanged += (_, _) => EditCfg("lang", c => c.Language = LanguageBox.SelectedIndex == 1 ? "en" : "de");
        ThemeBox.SelectionChanged += (_, _) => EditCfg("theme", c => c.Theme = ThemeBox.SelectedIndex == 1 ? "light" : "dark");
        OffMinBox.ValueChanged += (_, _) => EditCfg("offmin", c => c.Idle.OffS = OffMinBox.Value is { } m && m > 0 ? Math.Max(10, Seconds(m)) : 0);
        PcAwayBox.IsCheckedChanged += (_, _) => { AwayDelayBox.IsEnabled = PcAwayBox.IsChecked == true; EditCfg("pcaway", c => c.Power.PcAway = PcAwayBox.IsChecked == true); };
        AwayDelayBox.ValueChanged += (_, _) => EditCfg("awaydelay", c => c.Power.PcAwayDelayS = AwayDelayBox.Value is { } m ? Math.Max(5, Seconds(m)) : c.Power.PcAwayDelayS);
        ClickBox.IsCheckedChanged += (_, _) => EditCfg("click", c => c.Sound.Click = ClickBox.IsChecked == true);
        ClickVolumeBox.ValueChanged += (_, _) => EditCfg("clickvol", c => c.Sound.ClickVolume = (int)(ClickVolumeBox.Value ?? c.Sound.ClickVolume));
        SoundVolumeBox.ValueChanged += (_, _) => EditCfg("soundvol", c => c.Sound.Volume = (int)(SoundVolumeBox.Value ?? c.Sound.Volume));
        // Testen: das Gerät spielt den Ton mit der gerade gesendeten Lautstärke (bei Live-Senden ist sie schon dort)
        ClickTestButton.Click += async (_, _) => await TestSoundAsync("click");
        SoundTestButton.Click += async (_, _) => await TestSoundAsync("info");
        BrightnessBox.ValueChanged += (_, _) => EditCfg("bright", c => c.Brightness = (int)(BrightnessBox.Value ?? c.Brightness));
        HoldBox.ValueChanged += (_, _) => EditCfg("hold", c => c.HoldMs = (int)(HoldBox.Value ?? c.HoldMs));
        IdleBox.ValueChanged += (_, _) => EditCfg("idle", c => c.Idle.TimeoutS = IdleBox.Value is { } m ? Math.Max(5, Seconds(m)) : c.Idle.TimeoutS);
        IdleBrightBox.ValueChanged += (_, _) => EditCfg("idleb", c => c.Idle.Brightness = (int)(IdleBrightBox.Value ?? c.Idle.Brightness));
        IdleMoveBox.ValueChanged += (_, _) => EditCfg("idlem", c => c.Idle.MoveS = IdleMoveBox.Value is { } m ? Math.Max(5, Seconds(m)) : c.Idle.MoveS);

        // Kachel-Eigenschaften
        TypeValueBtn.Click += (_, _) => { if (_sel != null) TypeBox.SelectedIndex = 0; SyncTypeButtons(); };
        TypeButtonBtn.Click += (_, _) => { if (_sel != null) TypeBox.SelectedIndex = 1; SyncTypeButtons(); };
        TypeCalBtn.Click += (_, _) => { if (_sel != null) TypeBox.SelectedIndex = 2; SyncTypeButtons(); };
        TypeRadioBtn.Click += (_, _) => { if (_sel != null) TypeBox.SelectedIndex = 3; SyncTypeButtons(); };
        TypeChartBtn.Click += (_, _) => { if (_sel != null) TypeBox.SelectedIndex = 4; SyncTypeButtons(); };
        ModePcBtn.Click += (_, _) => { if (_sel != null) ButtonModeBox.SelectedIndex = 0; SyncTypeButtons(); };
        ModeHaBtn.Click += (_, _) => { if (_sel != null) ButtonModeBox.SelectedIndex = 1; SyncTypeButtons(); };
        SpanBox.SelectionChanged += (_, _) => EditTile("span", t => { if (SpanBox.SelectedIndex >= 0) t.Span = SpanSeconds[SpanBox.SelectedIndex]; });
        VolStepBox.ValueChanged += (_, _) =>
        {
            if (_suppress || VolStepBox.Value is not { } v) return;
            Settings.VolumeStep = Math.Clamp((int)v, 1, 20);
            Settings.Save();
        };
        VolRateBox.ValueChanged += (_, _) =>
        {
            if (_suppress || VolRateBox.Value is not { } v) return;
            Settings.VolumeRate = Math.Clamp((int)v, 1, 10);
            Settings.Save();
        };
        InfoGoButton.Click += (_, _) => ShowPage(_sel?.IsCalendar == true ? 3 : 2);
        TypeBox.SelectionChanged += (_, _) => EditTile("type", t =>
        {
            t.Type = TypeBox.SelectedIndex switch { 1 => "button", 2 => "calendar", 3 => "radio", 4 => "chart", _ => "value" };
            if (t.IsButton && t.Format == null) t.Format = "onoff";
            if (t.IsCalendar && t.Icon == null) t.Icon = "calendar";
            if (t.IsChart)
            {
                t.Icon ??= "chart-line";
                t.Span ??= 120;
                var cw = Math.Max(t.W, 2);
                var chh = Math.Max(t.H, 2);
                if ((cw != t.W || chh != t.H) && ConfigValidator.Fits(Page, t, t.X, t.Y, cw, chh)) { t.W = cw; t.H = chh; }
            }
            if (t.IsRadio)
            {
                t.Trigger = "tap";
                t.Action ??= "pc:radio.toggle";
                // Eine Radio-Kachel braucht Platz für Logo und Titel: wenn möglich auf 4 x 2 vergrössern
                var rw = Math.Max(t.W, 4);
                var rh = Math.Max(t.H, 2);
                if ((rw != t.W || rh != t.H) && ConfigValidator.Fits(Page, t, t.X, t.Y, rw, rh)) { t.W = rw; t.H = rh; }
            }
        }, panels: true);
        TitleDe.TextChanged += (_, _) => EditTile("td", t => t.Title = new LText(TitleDe.Text ?? "", TitleDe.Text ?? ""));
        IconBox.TextChanged += (_, _) => EditTile("icon", t => t.Icon = string.IsNullOrWhiteSpace(IconBox.Text) ? null : IconBox.Text, icon: true);
        SourceBox.TextChanged += (_, _) => EditTile("src", t => t.Source = SourceBox.Text);
        ButtonModeBox.SelectionChanged += (_, _) => EditTile("btnmode", t =>
        {
            if (ButtonModeBox.SelectedIndex == 1)
            {
                t.Action = null;                                     // die Entität selbst wird umgeschaltet
                if (IsPcSource(t.Source)) t.Source = null;
                _advTiles.Remove(t);
                _haModeTiles.Add(t);
            }
            else
            {
                _haModeTiles.Remove(t);
                t.Source = AutoSource(t.Action);                     // PC-Aktion: eine Home-Assistant-Entität gehört nicht mehr dazu
            }
        }, panels: true);
        AdvLightBox.IsCheckedChanged += (_, _) => EditTile("adv", t =>
        {
            if (AdvLightBox.IsChecked == true) _advTiles.Add(t);
            else
            {
                _advTiles.Remove(t);
                t.Source = AutoSource(t.Action);
            }
        }, panels: true);
        SourceKindBox.SelectionChanged += (_, _) => EditTile("srckind", t =>
        {
            if (SourceKindBox.SelectedIndex < 0 || SourceKindBox.SelectedIndex >= _srcKinds.Length) return;
            switch (_srcKinds[SourceKindBox.SelectedIndex])
            {
                case "pc":
                    var p = PcSources[Math.Max(0, PcSourceBox.SelectedIndex)];
                    t.Source = "pc:" + p.Id;
                    if (!t.IsButton) t.Format = p.Format;
                    break;
                case "ha":
                    if (IsPcSource(t.Source)) t.Source = null;       // zurück zu Home Assistant: die PC-Quelle fällt weg
                    break;
                default:
                    t.Source = null;                                  // keine Quelle: die Taste leuchtet nicht
                    break;
            }
        }, panels: true);
        PcSourceBox.SelectionChanged += (_, _) => EditTile("pcsrc", t =>
        {
            if (PcSourceBox.SelectedIndex < 0) return;
            var p = PcSources[PcSourceBox.SelectedIndex];
            t.Source = "pc:" + p.Id;
            if (!t.IsButton) t.Format = p.Format;
            if (string.IsNullOrWhiteSpace(t.Title.De) && string.IsNullOrWhiteSpace(t.Title.En)) t.Title = new LText(T(p.De, p.En), T(p.De, p.En));
        }, panels: true);
        FormatBox.SelectionChanged += (_, _) => EditTile("fmt", t => { if (FormatBox.SelectedIndex >= 0) t.Format = FormatIds[FormatBox.SelectedIndex]; });
        MaxBox.ValueChanged += (_, _) => EditTile("max", t => t.Max = (double?)MaxBox.Value);
        TriggerBox.SelectionChanged += (_, _) => EditTile("trig", t => { if (TriggerBox.SelectedIndex >= 0) t.Trigger = TriggerIds[TriggerBox.SelectedIndex]; });
        ActionBox.SelectionChanged += (_, _) => EditTile("act", t =>
        {
            if (ActionBox.SelectedItem is not ActionChoice c) return;
            t.Action = c.Id;
            // Stumm-Tasten leuchten von selbst nach dem Zustand, den sie umschalten; bei anderen Aktionen fällt er wieder weg
            if (t.IsButton && !_advTiles.Contains(t) && (string.IsNullOrWhiteSpace(t.Source) || t.Source is "pc:mute" or "pc:mic_mute"))
                t.Source = AutoSource(c.Id);
            // Lauter und Leiser: bei gedrückt gehaltener Taste weiter ändern
            if (c.Id is "pc:volume.up" or "pc:volume.down" && t.EffectiveTrigger is "none" or "tap")
            {
                t.Trigger = "repeat";
                TriggerBox.SelectedIndex = Array.IndexOf(TriggerIds, "repeat");
            }
            // Gefährliche Aktionen (Herunterfahren …) nur mit Bestätigung auf dem Display
            if (c.Id.StartsWith("pc:", StringComparison.Ordinal) && BuiltinActions.Find(c.Id[3..]) is { Confirm: true } && t.EffectiveTrigger is "none" or "tap")
            {
                t.Trigger = "confirm";
                TriggerBox.SelectedIndex = Array.IndexOf(TriggerIds, "confirm");
            }
        }, panels: true);
        TileHoldBox.ValueChanged += (_, _) => EditTile("thold", t => t.HoldMs = (int?)TileHoldBox.Value);
        OnBox.IsCheckedChanged += (_, _) =>
        {
            if (_suppress || _sel == null) return;
            if (OnBox.IsChecked == true) _on.Add(_sel); else _on.Remove(_sel);
            Rebuild();
        };
        NewActionButton.Click += async (_, _) =>
        {
            await new ActionEditWindow(_core!.Actions, null).ShowDialog<UserAction?>(this);
        };
        DeleteTileButton.Click += (_, _) => DeleteSelected();
        CopyTileButton.Click += (_, _) => CopySelected(cut: false);
        CutTileButton.Click += (_, _) => CopySelected(cut: true);
        PasteButton.Click += (_, _) => PasteTile();

        foreach (var box in new[] { XBox, YBox, WBox, HBox })
            box.ValueChanged += (_, _) => EditGeometry();
    }

    private sealed record ActionChoice(string Id, string Label)
    {
        public override string ToString() => Label;
    }

    // ---- Echte Werte ------------------------------------------------------------------------------

    /// <summary>Zustände für die Vorschau: Home Assistant und PC. Null, wenn "Echte Werte anzeigen" aus ist oder nichts bekannt ist.</summary>
    private IReadOnlyDictionary<string, string>? CurrentStates()
    {
        if (RealValuesBox.IsChecked != true || _core == null) return null;
        var states = new Dictionary<string, string>(_haStates);
        foreach (var (id, value) in _core.Platform.GetStates()) states["pc:" + id] = value;
        return states;
    }

    // Wird auf einem Audio-Thread aufgerufen: nie auf Oberflächen-Elemente zugreifen (das wirft dort eine Ausnahme)
    private volatile bool _realValuesOn = true;

    private void OnPcStateChanged(string id, string value)
    {
        if (!_realValuesOn) return;
        Dispatcher.UIThread.Post(() => { if (!_pcRedraw.IsEnabled) _pcRedraw.Start(); });
    }

    private async Task RefreshValuesAsync()
    {
        if (RealValuesBox.IsChecked != true || _core == null) return;
        if (string.IsNullOrWhiteSpace(Settings.HaUrl) || string.IsNullOrWhiteSpace(Settings.HaToken)) { Rebuild(); return; }
        var (entities, _) = await HaClient.FetchAsync(Settings.HaUrl, Settings.HaToken);
        if (entities != null) _haStates = entities.ToDictionary(e => e.Id, e => e.State);
        Rebuild();
    }

    // ---- Navigation ----------------------------------------------------------------------------

    private static readonly string[] NavKeys = ["nav.start", "nav.display", "nav.content", "nav.connections", "nav.device", "nav.app"];
    private static readonly string[] NavIcons = ["view-dashboard", "monitor-dashboard", "bookshelf", "connection", "cellphone-link", "cog"];
    private readonly TextBlock[] _navLabels = new TextBlock[6];
    private bool _navBusy;

    private void BuildNav()
    {
        for (var i = 0; i < NavKeys.Length; i++)
        {
            _navLabels[i] = new TextBlock { VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, FontSize = 15 };
            var row = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 12, Margin = new Thickness(6, 8) };
            row.Children.Add(new TextBlock { Text = MdiIcons.Glyph(NavIcons[i]), FontFamily = IconFont, FontSize = 22, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
            row.Children.Add(_navLabels[i]);
            NavList.Items.Add(row);
        }
        NavList.SelectionChanged += (_, _) =>
        {
            if (_navBusy || NavList.SelectedIndex < 0) return;
            ShowPage(NavList.SelectedIndex);
        };
    }

    public void ShowPage(int index)
    {
        _navBusy = true;
        NavList.SelectedIndex = index;
        _navBusy = false;
        StartPage.IsVisible = index == 0;
        PageDisplay.IsVisible = index == 1;
        PageContent.IsVisible = index == 2;
        ConnectionsPage.IsVisible = index == 3;
        PageDevice.IsVisible = index == 4;
        AppPage.IsVisible = index == 5;
    }

    /// <summary>Welche Kacheln verwenden diese Aktion? (für die Sperre beim Löschen)</summary>
    private IReadOnlyList<string> ActionUsage(string action)
    {
        var list = new List<string>();
        for (var p = 0; p < _cfg.Pages.Count; p++)
            foreach (var t in _cfg.Pages[p].Tiles)
                if (t.Action == action)
                {
                    var title = t.Title.Get(_cfg.Language).Replace('\n', ' ');
                    list.Add(T("Seite", "Page") + $" {p + 1} «{_cfg.Pages[p].Title.Get(_cfg.Language)}»: " + (title.Length > 0 ? title : T("Kachel ohne Titel", "tile without title")));
                }
        return list;
    }

    /// <summary>Lässt das Gerät einen Ton spielen. Ist Live-Senden aus oder noch ein Wert unterwegs, wird er vorher gesendet.</summary>
    private async Task TestSoundAsync(string id)
    {
        if (!_core!.Link.IsConnected) { Status(T("Kein Gerät verbunden.", "No device connected."), error: true); return; }
        if (_dirty) await SendAsync(auto: true);          // aktuelle Lautstärke zuerst aufs Gerät
        _core.Link.Send(System.Text.Json.JsonSerializer.Serialize(new { t = "sound", id }));
    }

    private void OnAppLanguageChanged()
    {
        ApplyTexts();
        ActionsPage.ApplyTexts();
        RadioPage.ApplyTexts();
        StartPage.ApplyTexts();
        ConnectionsPage.ApplyTexts();
        FirmwarePage.ApplyTexts();
        AppPage.ApplyTexts();
        Refresh(panels: true);
    }

    private void OnConnectionChanged(bool connected, DeviceInfo? info) => Dispatcher.UIThread.Post(UpdateConnection);

    private void UpdateConnection()
    {
        if (_core == null) return;
        var connected = _core.Link.IsConnected;
        var info = _core.Link.Device;
        var text = connected ? Strings.Format("win.status.connected", info?.Firmware ?? "?") : Strings.Get("win.status.disconnected");
        DeviceStatusText.Text = text;
        NavConn.Text = connected ? T("Gerät verbunden", "Device connected") : T("Kein Gerät", "No device");
        NavDot.Fill = new SolidColorBrush(Color.Parse(connected ? "#3DDC84" : "#E5484D"));
        UpdateLiveStatus();
    }

    private void OnActionsChanged() => Dispatcher.UIThread.Post(() => UpdateTilePanel());

    // ---- Laden ---------------------------------------------------------------------------------

    private static ConfigModel DefaultConfig()
    {
        using var stream = AssetLoader.Open(new Uri("avares://OverBoardAgent/Assets/default-config.json"));
        using var reader = new StreamReader(stream);
        return ConfigModel.Parse(reader.ReadToEnd());
    }

    private async Task LoadInitialAsync()
    {
        if (_core!.Link.IsConnected)
        {
            try
            {
                SetConfig(ConfigModel.Parse(await _core.Link.GetConfigAsync()), resetHistory: true);
                Status(T("Konfiguration vom Gerät geladen.", "Configuration loaded from the device."), ok: true);
                return;
            }
            catch (Exception ex)
            {
                Log.Warn($"Editor: Laden vom Gerät fehlgeschlagen: {ex.Message}");
            }
        }
        SetConfig(DefaultConfig(), resetHistory: true);
        Status(T("Kein Gerät verbunden – Standardkonfiguration geladen. Änderungen werden gesendet, sobald ein Gerät verbunden ist und du „Jetzt senden“ drückst.",
                 "No device connected – default configuration loaded. Changes are sent when a device is connected and you press “Send now”."));
    }

    private async Task LoadFromDeviceAsync()
    {
        if (!_core!.Link.IsConnected) { Status(T("Kein Gerät verbunden.", "No device connected."), error: true); return; }
        try
        {
            Status(T("Lade vom Gerät …", "Loading from device …"));
            var loaded = ConfigModel.Parse(await _core.Link.GetConfigAsync());
            Touch(null);
            PushUndo();
            SetConfig(loaded, resetHistory: false);
            _dirty = false;
            Status(T("Konfiguration vom Gerät geladen.", "Configuration loaded from the device."), ok: true);
        }
        catch (Exception ex)
        {
            Status(Strings.Format("cfg.error", ex.Message), error: true);
        }
    }

    private void SetConfig(ConfigModel cfg, bool resetHistory)
    {
        _cfg = cfg;
        _page = 0;
        _sel = null;
        _on.Clear();
        if (resetHistory) { _undo.Clear(); _redo.Clear(); }
        Refresh(panels: true);
    }

    // ---- Anzeige --------------------------------------------------------------------------------

    private void Rebuild()
    {
        if (_cfg.Pages.Count == 0) return;
        var demo = DemoBox.IsChecked == true;
        var options = new PreviewOptions { Page = _page, Selected = demo ? null : _sel, ShowGrid = !demo, Ghost = _ghost, GhostValid = _ghostValid, States = CurrentStates(), Calendar = _core?.CalendarLines.Count > 0 || Settings.CalendarUrl.Length > 0 ? _core?.CalendarLines : null,
            Radio = _core?.Radio.CurrentStation is { } rs ? (rs.Name, _core.Radio.Title, _core.Radio.Playing) : null };
        foreach (var t in _on) options.On.Add(t);
        Host.Content = DisplayRenderer.Build(_cfg, options);
    }

    /// <summary>Alles neu zeichnen und die Seitenleisten aktualisieren.</summary>
    private void Refresh(bool panels)
    {
        Rebuild();
        if (panels)
        {
            UpdatePagePanel();
            UpdateSettingsPanel();
            UpdateTilePanel();
        }
        UpdateUndoButtons();
    }

    private void UpdateUndoButtons()
    {
        UndoButton.IsEnabled = _undo.Count > 0;
        RedoButton.IsEnabled = _redo.Count > 0;
    }

    private void UpdatePagePanel()
    {
        _suppress = true;
        try
        {
            PageList.SelectedIndex = -1;
            PageList.Items.Clear();
            for (var i = 0; i < _cfg.Pages.Count; i++)
                PageList.Items.Add($"{i + 1}  {_cfg.Pages[i].Title.Get(_cfg.Language)}");
            PageList.SelectedIndex = _page;
            var p = Page;
            PageTitleDe.Text = p.Title.Get(_cfg.Language);
            PageIcon.Text = p.Icon ?? "";
            PageRows.Value = p.Rows;
            DeletePageButton.IsEnabled = _cfg.Pages.Count > 1;
            AddPageButton.IsEnabled = _cfg.Pages.Count < ConfigValidator.MaxPages;
        }
        finally { _suppress = false; }
    }

    private void UpdateSettingsPanel()
    {
        _suppress = true;
        try
        {
            LanguageBox.SelectedIndex = _cfg.Language == "en" ? 1 : 0;
            ThemeBox.SelectedIndex = _cfg.Theme == "light" ? 1 : 0;
            OffMinBox.Value = Minutes(_cfg.Idle.OffS);
            PcAwayBox.IsChecked = _cfg.Power.PcAway;
            AwayDelayBox.Value = Minutes(_cfg.Power.PcAwayDelayS);
            AwayDelayBox.IsEnabled = _cfg.Power.PcAway;
            ClickBox.IsChecked = _cfg.Sound.Click;
            ClickVolumeBox.Value = _cfg.Sound.ClickVolume;
            SoundVolumeBox.Value = _cfg.Sound.Volume;
            BrightnessBox.Value = _cfg.Brightness;
            HoldBox.Value = _cfg.HoldMs;
            IdleBox.Value = Minutes(_cfg.Idle.TimeoutS);
            UpdatePowerSummary();
            SyncRadioTheme();
            IdleBrightBox.Value = _cfg.Idle.Brightness;
            IdleMoveBox.Value = Minutes(_cfg.Idle.MoveS);
            _cfg.Idle.Values ??= DefaultIdleValues();
            for (var i = 0; i < 2; i++)
            {
                var v = i < _cfg.Idle.Values.Count ? _cfg.Idle.Values[i] : null;
                _idleSrc[i].Text = v?.Source ?? "";
                _idleDe[i].Text = v?.Title.Get(_cfg.Language) ?? "";
                _idleFmt[i].SelectedIndex = v != null ? Array.IndexOf(FormatIds, v.Format) : 0;
            }
        }
        finally { _suppress = false; }
    }

    private void UpdateTilePanel()
    {
        _suppress = true;
        try
        {
            var t = _sel;
            TilePanel.IsVisible = t != null;
            NoTileText.IsVisible = t == null;
            if (t == null) { TileHeader.Text = ""; return; }

            TypeBox.SelectedIndex = t.IsButton ? 1 : t.IsCalendar ? 2 : t.IsRadio ? 3 : t.IsChart ? 4 : 0;
            TitleDe.Text = t.Title.Get(_cfg.Language);
            IconBox.Text = t.Icon ?? "";
            IconPreview.Text = t.Icon != null ? MdiIcons.Glyph(t.Icon) : "";
            var pcSource = IsPcSource(t.Source);
            var btnHa = t.IsButton && t.Action == null && ((!string.IsNullOrWhiteSpace(t.Source) && !pcSource) || _haModeTiles.Contains(t));
            var adv = t.IsButton && !btnHa && (_advTiles.Contains(t) || (!string.IsNullOrWhiteSpace(t.Source) && t.Source != AutoSource(t.Action)));
            ButtonModeBox.SelectedIndex = btnHa ? 1 : 0;
            AdvLightBox.IsChecked = adv;
            _srcKinds = KindsFor(t);
            FillCombo(SourceKindBox, _srcKinds.Select(KindText).ToList());
            var kind = pcSource ? "pc" : t.IsButton && string.IsNullOrWhiteSpace(t.Source) && t.Action != null ? "none" : "ha";
            SourceKindBox.SelectedIndex = Array.IndexOf(_srcKinds, kind);
            SourceKindLabel.Text = t.IsButton ? T("Leuchtet auf, wenn …", "Lights up when …") : T("Datenquelle", "Data source");
            PcSourceBox.SelectedIndex = pcSource ? Math.Max(0, Array.FindIndex(PcSources, p => "pc:" + p.Id == t.Source)) : -1;
            SourceBox.Text = pcSource ? "" : t.Source ?? "";
            FormatBox.SelectedIndex = Array.IndexOf(FormatIds, t.EffectiveFormat);
            MaxBox.Value = (decimal?)(t.Max ?? 0);
            MaxPanel.IsVisible = !t.IsButton && !t.IsCalendar && !t.IsRadio && t.EffectiveFormat is "power" or "kwh" or "percent";
            var hasSource = !t.IsCalendar && !t.IsRadio;
            var showKinds = t.IsButton ? adv : hasSource;
            SourceKindLabel.IsVisible = SourceKindBox.IsVisible = showKinds;
            FormatLabel.IsVisible = FormatBox.IsVisible = hasSource && !t.IsButton;
            PcSourceBox.IsVisible = showKinds && pcSource;
            SourceLabel.IsVisible = SourcePanelRow.IsVisible = t.IsButton ? btnHa || (adv && kind == "ha") : hasSource && !pcSource;
            SourceLabel.Text = t.IsButton && btnHa
                ? T("Home-Assistant-Entität, die umgeschaltet wird", "Home Assistant entity that is toggled")
                : T("Home-Assistant-Entität (z. B. sensor.xyz)", "Home Assistant entity (e.g. sensor.xyz)");
            SpanLabel.IsVisible = SpanBox.IsVisible = t.IsChart;
            SpanBox.SelectedIndex = Math.Max(0, Array.IndexOf(SpanSeconds, t.Span ?? 120));
            VolumePanel.IsVisible = t.IsButton && t.Action is "pc:volume.up" or "pc:volume.down";
            VolStepBox.Value = Settings.VolumeStep;
            VolRateBox.Value = Settings.VolumeRate;
            ButtonModeLabel.IsVisible = false;
            ButtonModeCards.IsVisible = t.IsButton;
            var typeName = t.IsChart ? Strings.Get("ed.type.chart") : t.IsButton ? Strings.Get("ed.type.button") : t.IsCalendar ? Strings.Get("ed.type.cal") : t.IsRadio ? Strings.Get("ed.type.radio") : Strings.Get("ed.type.value");
            TileHeader.Text = $"{typeName} · {t.W} × {t.H}";
            TileInfoText.IsVisible = InfoGoButton.IsVisible = t.IsCalendar || t.IsRadio;
            TileInfoText.Text = Strings.Get(t.IsCalendar ? "ed.info.cal" : "ed.info.radio");
            InfoGoButton.Content = Strings.Get(t.IsCalendar ? "ed.go.cal" : "ed.go.radio");
            SyncTypeButtons();
            AdvLightBox.IsVisible = t.IsButton && !btnHa;
            ActionLabel.IsVisible = ActionBox.IsVisible = NewActionButton.IsVisible = t.IsButton && !btnHa;
            ButtonPanel.IsVisible = t.IsButton;
            TriggerBox.SelectedIndex = Array.IndexOf(TriggerIds, t.EffectiveTrigger);
            TileHoldBox.Value = t.HoldMs ?? 0;
            XBox.Value = t.X + 1;
            YBox.Value = t.Y + 1;
            WBox.Value = t.W;
            HBox.Value = t.H;
            OnBox.IsChecked = _on.Contains(t);

            // Aktionsliste: eingebaute und eigene Aktionen; eine unbekannte Aktion der Konfiguration bleibt als Eintrag erhalten
            var choices = new List<ActionChoice> { new("", T("(keine Aktion)", "(no action)")) };
            choices.AddRange(BuiltinActions.All.Select(b => new ActionChoice("pc:" + b.Id, T(b.De, b.En))));
            choices.AddRange(_core!.Actions.Items.Select(a => new ActionChoice("pc:" + a.Id, a.Name)));
            if (t.Action != null && choices.All(c => c.Id != t.Action))
                choices.Add(new ActionChoice(t.Action, t.Action + " (?)"));
            ActionBox.SelectedIndex = -1;
            ActionBox.Items.Clear();
            foreach (var c in choices) ActionBox.Items.Add(c);
            ActionBox.SelectedIndex = Math.Max(0, choices.FindIndex(c => c.Id == (t.Action ?? "")));
        }
        finally { _suppress = false; }
    }

    // ---- Status ---------------------------------------------------------------------------------

    private void Status(string text, bool error = false, bool ok = false)
    {
        StatusText.Text = text;
        StatusText.Foreground = error ? new SolidColorBrush(Color.Parse("#E5484D"))
            : ok ? new SolidColorBrush(Color.Parse("#2E9E5B")) : null;
    }

    // ---- Verlauf (Rückgängig / Wiederholen) ------------------------------------------------------

    private void PushUndo()
    {
        _undo.Add(_cfg.ToJson());
        if (_undo.Count > 100) _undo.RemoveAt(0);
        _redo.Clear();
    }

    /// <summary>Merkt den Zustand vor einer Änderung. Wiederholte Änderungen am selben Feld kurz hintereinander zählen als eine.</summary>
    private void Touch(string? key)
    {
        var now = DateTime.UtcNow;
        if (key != null && key == _lastTouchKey && (now - _lastTouch).TotalSeconds < 2)
        {
            _lastTouch = now;
            return;
        }
        _lastTouchKey = key;
        _lastTouch = now;
        PushUndo();
    }

    private void DoUndo()
    {
        if (_undo.Count == 0) return;
        _redo.Add(_cfg.ToJson());
        RestoreFrom(_undo[^1]);
        _undo.RemoveAt(_undo.Count - 1);
    }

    private void DoRedo()
    {
        if (_redo.Count == 0) return;
        _undo.Add(_cfg.ToJson());
        RestoreFrom(_redo[^1]);
        _redo.RemoveAt(_redo.Count - 1);
    }

    private void RestoreFrom(string json)
    {
        var selectedIndex = _sel != null ? Page.Tiles.IndexOf(_sel) : -1;
        var page = _page;
        _cfg = ConfigModel.Parse(json);
        _page = Math.Clamp(page, 0, _cfg.Pages.Count - 1);
        _sel = selectedIndex >= 0 && selectedIndex < Page.Tiles.Count ? Page.Tiles[selectedIndex] : null;
        _on.Clear();
        _lastTouchKey = null;
        Refresh(panels: true);
        AfterChange();
    }

    // ---- Änderungen -----------------------------------------------------------------------------

    /// <summary>Nach jeder Änderung: neu zeichnen, prüfen, gegebenenfalls live senden.</summary>
    private void Changed(bool panels = false)
    {
        Refresh(panels);
        AfterChange();
    }

    private void AfterChange()
    {
        _dirty = true;
        var (outgoing, unfinished) = Outgoing();
        var errors = ConfigValidator.Validate(outgoing);
        if (errors.Count > 0)
        {
            Status(errors[0] + (errors.Count > 1 ? $"  (+{errors.Count - 1})" : "") + "  – " +
                   T("wird erst gesendet, wenn das behoben ist.", "will be sent once this is fixed."), error: true);
            _sendTimer.Stop();
            return;
        }
        var note = unfinished > 0 ? "  " + T("(Taste ohne Aktion wird ohne Bedienung gesendet.)", "(Button without action is sent without operation.)") : "";
        Status((LiveBox.IsChecked == true ? T("Änderung wird gesendet …", "Sending change …") : T("Nicht gesendet – „Jetzt senden“ drücken.", "Not sent – press “Send now”.")) + note);
        if (LiveBox.IsChecked == true) ScheduleSend();
    }

    /// <summary>Was an das Gerät geht: Tasten mit Bedienung, aber ohne Aktion oder Quelle, werden ohne Bedienung gesendet,
    /// damit eine angefangene Taste nicht alle anderen Änderungen blockiert.</summary>
    private (ConfigModel Config, int Unfinished) Outgoing()
    {
        var copy = _cfg.Clone();
        var n = 0;
        foreach (var t in copy.Pages.SelectMany(p => p.Tiles))
            if (t.IsButton && t.EffectiveTrigger != "none" && t.Action == null && string.IsNullOrEmpty(t.Source))
            {
                t.Trigger = "none";
                n++;
            }
        return (copy, n);
    }

    private void ScheduleSend()
    {
        _sendTimer.Stop();
        _sendTimer.Start();
    }

    private void EditCfg(string key, Action<ConfigModel> apply)
    {
        if (_suppress || _cfg.Pages.Count == 0) return;
        Touch(key);
        apply(_cfg);
        UpdatePowerSummary();
        SyncRadioTheme();
        Changed(panels: key is "lang");
    }

    private void EditPage(string key, Action<PageModel> apply, bool listToo = false, string? validIcon = null)
    {
        if (_suppress || _cfg.Pages.Count == 0) return;
        if (validIcon != null && validIcon.Length > 0 && !MdiIcons.Exists(validIcon)) return;   // erst bei gültigem Namen übernehmen
        Touch(key);
        apply(Page);
        Refresh(panels: false);
        if (listToo) UpdatePageListTitles();
        AfterChange();
    }

    private void UpdatePageListTitles()
    {
        _suppress = true;
        try
        {
            var sel = PageList.SelectedIndex;
            for (var i = 0; i < _cfg.Pages.Count && i < PageList.Items.Count; i++)
                PageList.Items[i] = $"{i + 1}  {_cfg.Pages[i].Title.Get(_cfg.Language)}";
            PageList.SelectedIndex = sel;
        }
        finally { _suppress = false; }
    }

    private void EditTile(string key, Action<TileModel> apply, bool panels = false, bool icon = false)
    {
        if (_suppress || _sel == null || _cfg.Pages.Count == 0) return;
        if (icon)
        {
            var name = IconBox.Text;
            if (!string.IsNullOrWhiteSpace(name) && !MdiIcons.Exists(name)) return;   // erst bei gültigem Namen übernehmen
            IconPreview.Text = string.IsNullOrWhiteSpace(name) ? "" : MdiIcons.Glyph(name);
        }
        Touch(key + "@" + _cfg.Pages.IndexOf(Page) + ":" + Page.Tiles.IndexOf(_sel));
        apply(_sel);
        _sel.Normalize();
        Refresh(panels: false);
        if (panels) UpdateTilePanel();
        AfterChange();
    }

    private void EditGeometry()
    {
        if (_suppress || _sel == null || _cfg.Pages.Count == 0) return;
        if (XBox.Value is not { } x || YBox.Value is not { } y || WBox.Value is not { } w || HBox.Value is not { } h) return;
        int nx = (int)x - 1, ny = (int)y - 1, nw = (int)w, nh = (int)h;
        if (!ConfigValidator.Fits(Page, _sel, nx, ny, nw, nh))
        {
            Status(T("Dort ist kein Platz (ausserhalb des Rasters oder eine andere Kachel).", "No room there (outside the grid or another tile)."), error: true);
            UpdateTilePanel();
            return;
        }
        Touch("geo");
        _sel.X = nx; _sel.Y = ny; _sel.W = nw; _sel.H = nh;
        Changed();
    }

    // ---- Seiten und Kacheln --------------------------------------------------------------------

    private void AddPage()
    {
        if (_cfg.Pages.Count >= ConfigValidator.MaxPages) return;
        Touch(null);
        var n = _cfg.Pages.Count + 1;
        _cfg.Pages.Add(new PageModel { Title = new LText(T("Seite", "Page") + " " + n, T("Seite", "Page") + " " + n), Icon = "view-grid", Rows = 4 });
        _page = _cfg.Pages.Count - 1;
        _sel = null;
        Changed(panels: true);
    }

    /// <summary>Zeigt die Vorlagen und fügt die gewählte Seite hinten an.</summary>
    public async Task AddFromTemplateAsync()
    {
        if (_cfg.Pages.Count >= ConfigValidator.MaxPages)
        {
            Status(T("Es sind schon alle Seiten belegt.", "All pages are already used."), error: true);
            return;
        }
        var page = await new TemplatesWindow(_cfg.Language, _cfg.Theme).ShowDialog<PageModel?>(this);
        if (page == null) return;
        Touch(null);
        _cfg.Pages.Add(page);
        _page = _cfg.Pages.Count - 1;
        _sel = null;
        Changed(panels: true);
    }

    private void DeletePage()
    {
        if (_cfg.Pages.Count <= 1) return;
        Touch(null);
        _cfg.Pages.RemoveAt(_page);
        _page = Math.Clamp(_page, 0, _cfg.Pages.Count - 1);
        _sel = null;
        Changed(panels: true);
    }

    /// <summary>Der Umschalter Gestalten | Vorführen zeigt den Zustand von DemoBox; im Vorführmodus sind Eigenschaften und Palette weg.</summary>
    private void SyncMode()
    {
        var demo = DemoBox.IsChecked == true;
        ModeDesignBtn.IsChecked = !demo;
        ModeDemoBtn.IsChecked = demo;
        InspectorPanel.IsVisible = !demo;
        PalettePanel.IsVisible = !demo;
        if (demo) Status(Strings.Get("ed.demo.hint"));
    }

    /// <summary>Zeile in der Werkzeugleiste: ob Änderungen sofort aufs Display gehen.</summary>
    private void UpdateLiveStatus()
    {
        var live = LiveBox.IsChecked == true;
        var connected = _core?.Link.IsConnected == true;
        LiveDot.Fill = new SolidColorBrush(Color.Parse(live ? (connected ? "#2E9E5B" : "#D9A400") : "#8A8F98"));
        LiveText.Text = Strings.Get(live ? (connected ? "ed.live.on" : "ed.live.nodev") : "ed.live.off");
        SendButton.IsVisible = !live;
    }

    private void SyncTypeButtons()
    {
        var t = _sel;
        TypeValueBtn.IsChecked = t != null && !t.IsButton && !t.IsCalendar && !t.IsRadio && !t.IsChart;
        TypeChartBtn.IsChecked = t?.IsChart == true;
        TypeButtonBtn.IsChecked = t?.IsButton == true;
        TypeCalBtn.IsChecked = t?.IsCalendar == true;
        TypeRadioBtn.IsChecked = t?.IsRadio == true;
        ModePcBtn.IsChecked = ButtonModeBox.SelectedIndex != 1;
        ModeHaBtn.IsChecked = ButtonModeBox.SelectedIndex == 1;
    }

    private void AddTile(string kind)
    {
        // Kalender und Radio brauchen Platz (4 x 2); wo es nicht reicht, so gross wie möglich
        var (w, h) = kind switch { "button" => (1, 1), "value" => (2, 1), "chart" => (2, 2), _ => (4, 2) };
        var spot = ConfigValidator.FindFreeSpot(Page, w, h);
        if (spot == null && kind is "calendar" or "radio" or "chart") { (w, h) = (4, 1); spot = ConfigValidator.FindFreeSpot(Page, w, h); }
        if (spot == null && kind != "button") { (w, h) = (2, 1); spot = ConfigValidator.FindFreeSpot(Page, w, h); }
        if (spot == null) { (w, h) = (1, 1); spot = ConfigValidator.FindFreeSpot(Page, w, h); }
        if (spot == null || Page.Tiles.Count >= ConfigValidator.MaxTiles)
        {
            Status(T("Auf dieser Seite ist kein Platz mehr. Zuerst eine Kachel löschen oder eine Reihe hinzufügen.",
                     "No room left on this page. Delete a tile or add a row first."), error: true);
            return;
        }
        Touch(null);
        var neu = new LText(T("Neu", "New"), T("Neu", "New"));
        var x = spot.Value.X;
        var y = spot.Value.Y;
        var tile = kind switch
        {
            "button" => new TileModel { Type = "button", X = x, Y = y, W = w, H = h, Icon = "star", Title = neu, Trigger = "tap" },
            "calendar" => new TileModel { Type = "calendar", X = x, Y = y, W = w, H = h, Icon = "calendar", Title = new LText(T("Kalender", "Calendar"), T("Kalender", "Calendar")) },
            "chart" => new TileModel { Type = "chart", X = x, Y = y, W = w, H = h, Icon = "chart-line", Title = new LText(T("Verlauf", "Chart"), T("Verlauf", "Chart")), Span = 120 },
            "radio" => new TileModel { Type = "radio", X = x, Y = y, W = w, H = h, Icon = "radio", Title = new LText("Radio", "Radio"), Trigger = "tap", Action = "pc:radio.toggle" },
            _ => new TileModel { Type = "value", X = x, Y = y, W = w, H = h, Format = "text", Title = neu },
        };
        Page.Tiles.Add(tile);
        _sel = tile;
        Changed(panels: true);
    }

    // ---- Kopieren, Ausschneiden, Einfügen (auch auf eine andere Seite) ------------------------------

    private TileModel? _clip;
    private bool _clipCut;                    // ausgeschnitten: nach dem Einfügen ist die Zwischenablage leer

    private void UpdatePasteButton()
    {
        PasteButton.IsEnabled = _clip != null;
        PasteButton.Content = _clip == null ? Strings.Get("ed.paste") : Strings.Format("ed.paste.of", _clip.Title.Get(_cfg.Language).Replace('\n', ' ').Trim() is { Length: > 0 } n ? n : Strings.Get("ed.type.button"));
    }

    private void CopySelected(bool cut)
    {
        if (_sel == null) return;
        _clip = _sel.Clone();
        _clipCut = cut;
        UpdatePasteButton();
        if (cut) DeleteSelected();           // ausgeschnitten: von der Seite nehmen, bleibt in der Zwischenablage
        Status(Strings.Get(cut ? "ed.clip.cut" : "ed.clip.copied"));
    }

    /// <summary>Fügt die Kachel aus der Zwischenablage auf der aktuellen Seite ein: an ihrer Stelle, wenn frei, sonst an der ersten freien. Ohne Platz bleibt sie in der Zwischenablage.</summary>
    private void PasteTile()
    {
        if (_clip == null) { Status(Strings.Get("ed.clip.none"), error: true); return; }
        if (Page.Tiles.Count >= ConfigValidator.MaxTiles)
        {
            Status(Strings.Format("ed.clip.nospace", _clip.W, _clip.H), error: true);
            return;
        }
        (int X, int Y)? spot = null;
        if (ConfigValidator.Fits(Page, null!, _clip.X, _clip.Y, _clip.W, _clip.H)) spot = (_clip.X, _clip.Y);
        else spot = ConfigValidator.FindFreeSpot(Page, _clip.W, _clip.H);
        if (spot == null)
        {
            Status(Strings.Format("ed.clip.nospace", _clip.W, _clip.H), error: true);
            return;
        }
        Touch(null);
        var tile = _clip.Clone();
        tile.X = spot.Value.X;
        tile.Y = spot.Value.Y;
        Page.Tiles.Add(tile);
        _sel = tile;
        if (_clipCut) { _clip = null; _clipCut = false; }
        UpdatePasteButton();
        Changed(panels: true);
        Status(Strings.Get("ed.clip.pasted"), ok: true);
    }

    private void DeleteSelected()
    {
        if (_sel == null) return;
        Touch(null);
        Page.Tiles.Remove(_sel);
        _on.Remove(_sel);
        _sel = null;
        Changed(panels: true);
    }

    // ---- Zeiger auf dem Display ------------------------------------------------------------------

    private TileModel? TileAt(Point p)
    {
        for (var i = Page.Tiles.Count - 1; i >= 0; i--)
            if (DisplayRenderer.TileRect(Page, Page.Tiles[i]).Contains(p)) return Page.Tiles[i];
        return null;
    }

    /// <summary>Vorführmodus: die Taste in der Vorschau kurz aufleuchten lassen und am Gerät (oder, ohne Gerät, direkt am PC) auslösen.</summary>
    private void DemoPress(TileModel tile)
    {
        if (!tile.IsButton && !tile.IsRadio) return;
        var index = Page.Tiles.IndexOf(tile);
        if (_core == null || index < 0) return;
        if (LiveBox.IsChecked == true && _core.Link.IsConnected)
        {
            _core.Link.Send(Protocol.Press(_page, index));          // das Gerät löst aus (auch Home-Assistant-Schalter) und leuchtet auf
        }
        else if (tile.Action is { } action && action.StartsWith("pc:", StringComparison.Ordinal))
        {
            var id = action[3..];
            Task.Run(() => _core.Run(id));                           // ohne Gerät wenigstens die PC-Aktion ausführen
        }
        // Aufleuchten in der Vorschau
        _on.Add(tile);
        Rebuild();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(260) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _on.Remove(tile);
            Rebuild();
        };
        timer.Start();
    }

    private void OnHostPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_cfg.Pages.Count == 0 || !e.GetCurrentPoint(Host).Properties.IsLeftButtonPressed) return;
        var p = e.GetPosition(Host);

        // Vorführmodus: ein Klick löst die Taste aus, statt sie auszuwählen
        if (DemoBox.IsChecked == true && !(_cfg.Pages.Count > 1 && p.Y >= DisplayRenderer.NavY && p.Y <= DisplayRenderer.NavY + DisplayRenderer.NavH))
        {
            if (TileAt(p) is { } pressed) DemoPress(pressed);
            return;
        }

        // Seitenleiste unten auf dem Display: Seite wechseln
        if (_cfg.Pages.Count > 1 && p.Y >= DisplayRenderer.NavY && p.Y <= DisplayRenderer.NavY + DisplayRenderer.NavH)
        {
            var n = _cfg.Pages.Count;
            var layout = DisplayRenderer.NavLayout(n, _page);
            var index = -1;
            for (var i = 0; i < n; i++)
                if (p.X >= layout[i].X && p.X <= layout[i].X + layout[i].W) { index = i; break; }
            if (index >= 0 && index != _page)
            {
                _page = index;
                _sel = null;
                Refresh(panels: true);
                if (LiveBox.IsChecked == true) { Log.Info($"Seite {_page + 1} ans Gerät"); _core?.Link.Send(Protocol.Page(_page, click: DemoBox.IsChecked == true)); }   // das Display zeigt dieselbe Seite (im Vorführmodus mit Klick)
            }
            return;
        }

        _drag = Drag.None;
        _moved = false;
        if (_sel != null && DisplayRenderer.HandleRect(DisplayRenderer.TileRect(Page, _sel)).Inflate(6).Contains(p))
        {
            _drag = Drag.Resize;
        }
        else
        {
            var tile = TileAt(p);
            if (tile != _sel)
            {
                _sel = tile;
                Rebuild();
                UpdateTilePanel();
            }
            if (tile != null)
            {
                var cell = DisplayRenderer.CellAt(Page, p);
                _drag = Drag.Move;
                _grabCol = Math.Clamp(cell.Col - tile.X, 0, tile.W - 1);
                _grabRow = Math.Clamp(cell.Row - tile.Y, 0, tile.H - 1);
            }
        }
        if (_drag != Drag.None) e.Pointer.Capture(Host);
    }

    private void OnHostMoved(object? sender, PointerEventArgs e)
    {
        if (_drag == Drag.None || _sel == null) return;
        var p = e.GetPosition(Host);
        var cell = DisplayRenderer.CellAt(Page, p);
        int x = _sel.X, y = _sel.Y, w = _sel.W, h = _sel.H;
        if (_drag == Drag.Move)
        {
            x = Math.Clamp(cell.Col - _grabCol, 0, ConfigValidator.Cols - w);
            y = Math.Clamp(cell.Row - _grabRow, 0, Math.Clamp(Page.Rows, 1, ConfigValidator.MaxRows) - h);
        }
        else
        {
            w = Math.Max(1, cell.Col - _sel.X + 1);
            h = Math.Max(1, cell.Row - _sel.Y + 1);
        }
        if ((x, y, w, h) == _ghostCells && _ghost != null) return;
        _ghostCells = (x, y, w, h);
        var probe = new TileModel { X = x, Y = y, W = w, H = h };
        _ghost = DisplayRenderer.TileRect(Page, probe);
        _ghostValid = ConfigValidator.Fits(Page, _sel, x, y, w, h);
        _moved = true;
        Rebuild();
    }

    private void OnHostReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_drag == Drag.None) return;
        e.Pointer.Capture(null);
        var apply = _moved && _ghostValid && _sel != null;
        var cells = _ghostCells;
        _drag = Drag.None;
        _ghost = null;
        _moved = false;
        _ghostCells = default;
        if (apply)
        {
            if (cells != (_sel!.X, _sel.Y, _sel.W, _sel.H))
            {
                Touch(null);
                (_sel.X, _sel.Y, _sel.W, _sel.H) = cells;
                Changed(panels: true);
                return;
            }
        }
        Rebuild();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        var inText = FocusManager?.GetFocusedElement() is TextBox or AutoCompleteBox or NumericUpDown;
        if (e.Key == Key.Z && e.KeyModifiers.HasFlag(KeyModifiers.Control) && !inText) { DoUndo(); e.Handled = true; }
        else if (e.Key == Key.Y && e.KeyModifiers.HasFlag(KeyModifiers.Control) && !inText) { DoRedo(); e.Handled = true; }
        else if (e.Key == Key.Delete && !inText && _sel != null) { DeleteSelected(); e.Handled = true; }
        else if (e.Key == Key.C && e.KeyModifiers.HasFlag(KeyModifiers.Control) && !inText && _sel != null) { CopySelected(cut: false); e.Handled = true; }
        else if (e.Key == Key.X && e.KeyModifiers.HasFlag(KeyModifiers.Control) && !inText && _sel != null) { CopySelected(cut: true); e.Handled = true; }
        else if (e.Key == Key.V && e.KeyModifiers.HasFlag(KeyModifiers.Control) && !inText) { PasteTile(); e.Handled = true; }
        else if (e.Key == Key.Escape && _drag != Drag.None)
        {
            _drag = Drag.None; _ghost = null; _moved = false; Rebuild();
        }
    }

    // ---- Senden ---------------------------------------------------------------------------------

    private async Task SendAsync(bool auto)
    {
        if (_sending) { _sendAgain = true; return; }
        var (outgoing, _) = Outgoing();
        var errors = ConfigValidator.Validate(outgoing);
        if (errors.Count > 0)
        {
            Status(errors[0], error: true);
            return;
        }
        if (!_core!.Link.IsConnected)
        {
            if (!auto) Status(T("Kein Gerät verbunden.", "No device connected."), error: true);
            else Status(T("Kein Gerät verbunden – wird nicht gesendet.", "No device connected – not sent."), error: true);
            return;
        }
        _sending = true;
        try
        {
            var (ok, error) = await _core.Link.SetConfigAsync(outgoing.ToJson());
            Log.Info($"Editor: Senden {(ok ? "ok" : "Fehler " + error)}");
            if (ok)
            {
                _dirty = false;
                Status(T("Auf dem Gerät angewendet und gespeichert.", "Applied and stored on the device."), ok: true);
            }
            else
            {
                Status(Strings.Format("cfg.error", error ?? "?"), error: true);
            }
        }
        catch (Exception ex)
        {
            Status(Strings.Format("cfg.error", ex.Message), error: true);
        }
        finally
        {
            _sending = false;
        }
        if (_sendAgain)
        {
            _sendAgain = false;
            await SendAsync(auto: true);
        }
    }

    private async Task ResetAsync()
    {
        if (!_core!.Link.IsConnected) { Status(T("Kein Gerät verbunden.", "No device connected."), error: true); return; }
        if (!await ConfirmAsync(Strings.Get("cfg.reset.confirm"))) return;
        try
        {
            var (ok, error) = await _core.Link.ResetConfigAsync();
            if (!ok) { Status(Strings.Format("cfg.error", error ?? "?"), error: true); return; }
            Touch(null);
            SetConfig(ConfigModel.Parse(await _core.Link.GetConfigAsync()), resetHistory: false);
            _dirty = false;
            Status(Strings.Get("cfg.reset.done"), ok: true);
        }
        catch (Exception ex)
        {
            Status(Strings.Format("cfg.error", ex.Message), error: true);
        }
    }

    // ---- Dateien und JSON ---------------------------------------------------------------------

    private async Task OpenFileAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("JSON") { Patterns = ["*.json"] }],
        });
        if (files.Count == 0) return;
        try
        {
            await using var stream = await files[0].OpenReadAsync();
            using var reader = new StreamReader(stream);
            var loaded = ConfigModel.Parse(await reader.ReadToEndAsync());
            Touch(null);
            SetConfig(loaded, resetHistory: false);
            AfterChange();
        }
        catch (Exception ex)
        {
            Status(Strings.Format("cfg.json.invalid", ex.Message), error: true);
        }
    }

    private async Task SaveFileAsync()
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = "overboard-config.json",
            DefaultExtension = "json",
            FileTypeChoices = [new FilePickerFileType("JSON") { Patterns = ["*.json"] }],
        });
        if (file == null) return;
        try
        {
            await using var stream = await file.OpenWriteAsync();
            stream.SetLength(0);
            await using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false));
            await writer.WriteAsync(_cfg.ToJson(pretty: true));
            Status(Strings.Get("cfg.saved"), ok: true);
        }
        catch (Exception ex)
        {
            Status(Strings.Format("cfg.error", ex.Message), error: true);
        }
    }

    /// <summary>Experten-Ansicht: das JSON als Text bearbeiten; "Übernehmen" liest es in den Editor zurück.</summary>
    private async Task ShowJsonAsync()
    {
        var box = new TextBox
        {
            AcceptsReturn = true,
            Text = _cfg.ToJson(pretty: true),
            FontFamily = new FontFamily("Consolas, Cascadia Mono, monospace"),
            FontSize = 13,
            TextWrapping = TextWrapping.NoWrap,
        };
        var apply = new Button { Content = T("Übernehmen", "Apply"), MinWidth = 110 };
        var cancel = new Button { Content = Strings.Get("edit.cancel"), MinWidth = 110 };
        var message = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var dialog = new Window
        {
            Title = "JSON",
            Width = 760,
            Height = 700,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var buttons = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
        buttons.Children.Add(apply);
        buttons.Children.Add(cancel);
        var grid = new Grid { RowDefinitions = new RowDefinitions("*,Auto,Auto"), Margin = new Thickness(14) };
        grid.Children.Add(box);
        Grid.SetRow(message, 1);
        message.Margin = new Thickness(0, 8);
        grid.Children.Add(message);
        Grid.SetRow(buttons, 2);
        grid.Children.Add(buttons);
        dialog.Content = grid;

        ConfigModel? result = null;
        apply.Click += (_, _) =>
        {
            try
            {
                var parsed = ConfigModel.Parse(box.Text ?? "");
                var errors = ConfigValidator.Validate(parsed);
                if (errors.Count > 0)
                {
                    message.Text = string.Join("\n", errors.Take(6));
                    message.Foreground = new SolidColorBrush(Color.Parse("#E5484D"));
                    return;
                }
                result = parsed;
                dialog.Close();
            }
            catch (Exception ex)
            {
                message.Text = Strings.Format("cfg.json.invalid", ex.Message);
                message.Foreground = new SolidColorBrush(Color.Parse("#E5484D"));
            }
        };
        cancel.Click += (_, _) => dialog.Close();
        await dialog.ShowDialog(this);
        if (result == null) return;
        Touch(null);
        SetConfig(result, resetHistory: false);
        AfterChange();
    }

    private async Task<bool> ConfirmAsync(string text)
    {
        var dialog = new Window
        {
            Title = Title,
            Width = 480,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var yes = new Button { Content = Strings.Get("win.yes"), MinWidth = 90 };
        var no = new Button { Content = Strings.Get("win.no"), MinWidth = 90 };
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 20,
            Children =
            {
                new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap },
                new StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                    Spacing = 10,
                    Children = { yes, no },
                },
            },
        };
        yes.Click += (_, _) => dialog.Close(true);
        no.Click += (_, _) => dialog.Close(false);
        return await dialog.ShowDialog<bool>(this);
    }
}
