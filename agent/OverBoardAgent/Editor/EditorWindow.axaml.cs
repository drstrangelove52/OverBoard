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

    private static readonly string[] FormatIds = ["power", "kwh", "status", "onoff", "text", "percent", "time"];
    private static readonly (string De, string En)[] FormatNames =
    [
        ("Leistung (kW)", "Power (kW)"), ("Energie (kWh)", "Energy (kWh)"), ("Status", "Status"),
        ("An / Aus", "On / Off"), ("Text", "Text"), ("Prozent", "Percent"), ("Uhrzeit", "Time"),
    ];

    private static readonly string[] TriggerIds = ["none", "tap", "hold", "confirm"];
    private static readonly (string De, string En)[] TriggerNames =
    [
        ("Keine Aktion", "No action"), ("Antippen", "Tap"), ("Gedrückt halten", "Press and hold"), ("Antippen und bestätigen", "Tap and confirm"),
    ];

    // Werte im Ruhemodus: zwei Plätze
    private readonly TextBox[] _idleSrc = new TextBox[2];
    private readonly TextBox[] _idleDe = new TextBox[2];
    private readonly ComboBox[] _idleFmt = new ComboBox[2];
    private readonly TextBlock[] _idleHead = new TextBlock[2];
    private TextBlock? _idleIntro;

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
        SetupPage.Init(core);
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

        Opened += async (_, _) => await LoadInitialAsync();
        Closed += (_, _) =>
        {
            _sendTimer.Stop();
            _core.Actions.Changed -= OnActionsChanged;
            _core.Link.ConnectionChanged -= OnConnectionChanged;
            ActionsPage.Detach();
        };
        _core.Actions.Changed += OnActionsChanged;
    }

    private static string T(string de, string en) => Strings.Language == "en" ? en : de;

    private PageModel Page => _cfg.Pages[Math.Clamp(_page, 0, Math.Max(0, _cfg.Pages.Count - 1))];

    // ---- Texte ---------------------------------------------------------------------------------

    private void ApplyTexts()
    {
        Title = "OverBoard";
        AppTitle.Text = "OverBoard";
        for (var i = 0; i < _navLabels.Length; i++) _navLabels[i].Text = Strings.Get(NavKeys[i]);
        DeviceHeader.Text = Strings.Get("nav.device");
        ConnectionHeader.Text = T("Verbindung", "Connection");
        SetupHeader.Text = T("Einrichtung", "Setup");
        FirmwareHeader.Text = T("Firmware", "Firmware");
        DisplayCardHeader.Text = T("Anzeige des Geräts", "Device display");
        DisplayCardHint.Text = T("Änderungen hier gehen sofort aufs Gerät (wie im Display-Editor). Die Sprache gilt nur für das Display, nicht für dieses Programm.",
                                 "Changes here are sent to the device immediately (as in the display editor). The language applies to the display only, not to this program.");
        IdleHeader.Text = T("Ruhemodus", "Idle mode");
        UpdateConnection();
        UndoButton.Content = T("↶ Rückgängig", "↶ Undo");
        RedoButton.Content = T("↷ Wiederholen", "↷ Redo");
        SendButton.Content = T("Jetzt senden", "Send now");
        LiveBox.Content = T("Live auf das Gerät", "Live to device");
        LoadButton.Content = T("Vom Gerät laden", "Load from device");
        OpenFileButton.Content = T("Aus Datei …", "From file …");
        SaveFileButton.Content = T("In Datei …", "To file …");
        ResetButton.Content = T("Auf Standard", "Reset to default");
        JsonButton.Content = T("JSON …", "JSON …");

        PagesLabel.Text = T("Seiten", "Pages");
        AddPageButton.Content = T("+ Seite", "+ Page");
        DeletePageButton.Content = T("Seite löschen", "Delete page");
        PageTitleDeLabel.Text = T("Titel der Seite", "Page title");
        PageIconLabel.Text = T("Symbol der Seite", "Page icon");
        PageRowsLabel.Text = T("Reihen auf dieser Seite", "Rows on this page");
        AddLabel.Text = T("Neue Kachel", "New tile");
        AddValueButton.Content = T("+ Anzeige-Kachel", "+ Display tile");
        AddButtonButton.Content = T("+ Taste", "+ Button");

        LanguageLabel.Text = T("Sprache des Displays", "Display language");
        ThemeLabel.Text = T("Farbdesign", "Colour theme");
        BrightnessLabel.Text = T("Helligkeit (%)", "Brightness (%)");
        HoldLabel.Text = T("Haltedauer für „Gedrückt halten“ (ms)", "Hold time for “press and hold” (ms)");
        IdleLabel.Text = T("Ruhemodus nach (s)", "Idle mode after (s)");
        IdleBrightLabel.Text = T("Helligkeit im Ruhemodus (%)", "Brightness in idle mode (%)");
        IdleMoveLabel.Text = T("Uhr verschiebt sich alle (s)", "Clock moves every (s)");

        TileHeader.Text = T("Kachel", "Tile");
        NoTileText.Text = T("Kachel auf dem Display anklicken. Ziehen verschiebt sie, das blaue Quadrat unten rechts vergrössert sie. Entf löscht.",
                            "Click a tile on the display. Drag to move it, the blue square at the bottom right resizes it. Del deletes.");
        TypeLabel.Text = T("Art", "Type");
        TitleDeLabel.Text = T("Titel", "Title");
        IconLabel.Text = T("Symbol (Name tippen, z. B. lightbulb)", "Icon (type a name, e.g. lightbulb)");
        SourceLabel.Text = T("Datenquelle: HA-Entität (z. B. sensor.xyz) oder pc:volume / pc:mute / pc:mic_mute",
                             "Data source: HA entity (e.g. sensor.xyz) or pc:volume / pc:mute / pc:mic_mute");
        FormatLabel.Text = T("Darstellung", "Format");
        MaxLabel.Text = T("Höchstwert (z. B. für Balken, 0 = keiner)", "Maximum (0 = none)");
        TriggerLabel.Text = T("Bedienung", "Operation");
        ActionLabel.Text = T("Aktion auf dem PC", "PC action");
        NewActionButton.Content = T("Neue eigene Aktion …", "New custom action …");
        TileHoldLabel.Text = T("Haltedauer dieser Taste (ms, 0 = allgemeine)", "Hold time of this button (ms, 0 = global)");
        GeometryLabel.Text = T("Position und Grösse: Spalte, Reihe, Breite, Höhe", "Position and size: column, row, width, height");
        GeometryHint.Text = T("Spalten 1–8; die Kachel muss im Raster liegen und darf keine andere überlappen.",
                              "Columns 1–8; the tile must fit the grid and must not overlap another.");
        OnBox.Content = T("In der Vorschau als „eingeschaltet“ zeigen", "Show as “on” in the preview");
        DeleteTileButton.Content = T("Kachel löschen", "Delete tile");

        FillCombo(TypeBox, [T("Anzeige (Wert)", "Display (value)"), T("Taste", "Button")]);
        FillCombo(FormatBox, FormatNames.Select(f => T(f.De, f.En)).ToList());
        FillCombo(TriggerBox, TriggerNames.Select(f => T(f.De, f.En)).ToList());
        FillCombo(LanguageBox, [T("Deutsch", "German"), T("Englisch", "English")]);
        FillCombo(ThemeBox, [T("Dunkel", "Dark"), T("Hell", "Light")]);
        for (var i = 0; i < 2; i++)
        {
            FillCombo(_idleFmt[i], FormatNames.Select(f => T(f.De, f.En)).ToList());
            _idleHead[i].Text = T($"Ruhemodus, Wert {i + 1}", $"Idle mode, value {i + 1}");
            _idleSrc[i].Watermark = T("Quelle, z. B. sensor.xyz (leer = nicht anzeigen)", "Source, e.g. sensor.xyz (empty = hidden)");
            _idleDe[i].Watermark = T("Titel", "Title");
        }
        _idleIntro!.Text = T("Im Ruhemodus werden unter der Uhr bis zu zwei Werte angezeigt.", "In idle mode up to two values are shown below the clock.");
    }

    private static string? GuessFormat(HaEntity e) => e.DeviceClass == "timestamp" ? "time" : e.Unit switch
    {
        "W" or "kW" => "power",
        "kWh" or "Wh" => "kwh",
        "%" => "percent",
        _ => null,
    };

    /// <summary>Öffnet die Entitäten-Auswahl, oder weist auf die fehlenden Zugangsdaten hin.</summary>
    private async Task<HaEntity?> PickEntityAsync()
    {
        if (string.IsNullOrWhiteSpace(Settings.HaToken) || string.IsNullOrWhiteSpace(Settings.HaUrl))
        {
            Status(Strings.Get("ha.err.token"), error: true);
            ShowPage(2);                     // Seite "Gerät": dort sind Adresse und Token
            return null;
        }
        return await new EntityPickerWindow().ShowDialog<HaEntity?>(this);
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
        LiveBox.IsCheckedChanged += (_, _) => { if (LiveBox.IsChecked == true && _dirty) ScheduleSend(); };
        LoadButton.Click += async (_, _) => await LoadFromDeviceAsync();
        OpenFileButton.Click += async (_, _) => await OpenFileAsync();
        SaveFileButton.Click += async (_, _) => await SaveFileAsync();
        ResetButton.Click += async (_, _) => await ResetAsync();
        JsonButton.Click += async (_, _) => await ShowJsonAsync();
        SourcePickButton.Click += async (_, _) =>
        {
            var e = await PickEntityAsync();
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
        };
        AddPageButton.Click += (_, _) => AddPage();
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
        AddValueButton.Click += (_, _) => AddTile(isButton: false);
        AddButtonButton.Click += (_, _) => AddTile(isButton: true);

        // Geräteeinstellungen
        LanguageBox.SelectionChanged += (_, _) => EditCfg("lang", c => c.Language = LanguageBox.SelectedIndex == 1 ? "en" : "de");
        ThemeBox.SelectionChanged += (_, _) => EditCfg("theme", c => c.Theme = ThemeBox.SelectedIndex == 1 ? "light" : "dark");
        BrightnessBox.ValueChanged += (_, _) => EditCfg("bright", c => c.Brightness = (int)(BrightnessBox.Value ?? c.Brightness));
        HoldBox.ValueChanged += (_, _) => EditCfg("hold", c => c.HoldMs = (int)(HoldBox.Value ?? c.HoldMs));
        IdleBox.ValueChanged += (_, _) => EditCfg("idle", c => c.Idle.TimeoutS = (int)(IdleBox.Value ?? c.Idle.TimeoutS));
        IdleBrightBox.ValueChanged += (_, _) => EditCfg("idleb", c => c.Idle.Brightness = (int)(IdleBrightBox.Value ?? c.Idle.Brightness));
        IdleMoveBox.ValueChanged += (_, _) => EditCfg("idlem", c => c.Idle.MoveS = (int)(IdleMoveBox.Value ?? c.Idle.MoveS));

        // Kachel-Eigenschaften
        TypeBox.SelectionChanged += (_, _) => EditTile("type", t =>
        {
            t.Type = TypeBox.SelectedIndex == 1 ? "button" : "value";
            if (t.IsButton && t.Format == null) t.Format = "onoff";
        }, panels: true);
        TitleDe.TextChanged += (_, _) => EditTile("td", t => t.Title = new LText(TitleDe.Text ?? "", TitleDe.Text ?? ""));
        IconBox.TextChanged += (_, _) => EditTile("icon", t => t.Icon = string.IsNullOrWhiteSpace(IconBox.Text) ? null : IconBox.Text, icon: true);
        SourceBox.TextChanged += (_, _) => EditTile("src", t => t.Source = SourceBox.Text);
        FormatBox.SelectionChanged += (_, _) => EditTile("fmt", t => { if (FormatBox.SelectedIndex >= 0) t.Format = FormatIds[FormatBox.SelectedIndex]; });
        MaxBox.ValueChanged += (_, _) => EditTile("max", t => t.Max = (double?)MaxBox.Value);
        TriggerBox.SelectionChanged += (_, _) => EditTile("trig", t => { if (TriggerBox.SelectedIndex >= 0) t.Trigger = TriggerIds[TriggerBox.SelectedIndex]; });
        ActionBox.SelectionChanged += (_, _) => EditTile("act", t =>
        {
            if (ActionBox.SelectedItem is not ActionChoice c) return;
            t.Action = c.Id;
            // Gefährliche Aktionen (Herunterfahren …) nur mit Bestätigung auf dem Display
            if (c.Id.StartsWith("pc:", StringComparison.Ordinal) && BuiltinActions.Find(c.Id[3..]) is { Confirm: true } && t.EffectiveTrigger is "none" or "tap")
            {
                t.Trigger = "confirm";
                TriggerBox.SelectedIndex = Array.IndexOf(TriggerIds, "confirm");
            }
        });
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

        foreach (var box in new[] { XBox, YBox, WBox, HBox })
            box.ValueChanged += (_, _) => EditGeometry();
    }

    private sealed record ActionChoice(string Id, string Label)
    {
        public override string ToString() => Label;
    }

    // ---- Navigation ----------------------------------------------------------------------------

    private static readonly string[] NavKeys = ["nav.display", "nav.actions", "nav.device", "nav.app"];
    private static readonly string[] NavIcons = ["monitor-dashboard", "flash", "cellphone-link", "cog"];
    private readonly TextBlock[] _navLabels = new TextBlock[4];
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
        PageDisplay.IsVisible = index == 0;
        ActionsPage.IsVisible = index == 1;
        PageDevice.IsVisible = index == 2;
        AppPage.IsVisible = index == 3;
    }

    private void OnAppLanguageChanged()
    {
        ApplyTexts();
        ActionsPage.ApplyTexts();
        SetupPage.ApplyTexts();
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
        var options = new PreviewOptions { Page = _page, Selected = _sel, Ghost = _ghost, GhostValid = _ghostValid };
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
            BrightnessBox.Value = _cfg.Brightness;
            HoldBox.Value = _cfg.HoldMs;
            IdleBox.Value = _cfg.Idle.TimeoutS;
            IdleBrightBox.Value = _cfg.Idle.Brightness;
            IdleMoveBox.Value = _cfg.Idle.MoveS;
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
            if (t == null) return;

            TypeBox.SelectedIndex = t.IsButton ? 1 : 0;
            TitleDe.Text = t.Title.Get(_cfg.Language);
            IconBox.Text = t.Icon ?? "";
            IconPreview.Text = t.Icon != null ? MdiIcons.Glyph(t.Icon) : "";
            SourceBox.Text = t.Source ?? "";
            FormatBox.SelectedIndex = Array.IndexOf(FormatIds, t.EffectiveFormat);
            MaxBox.Value = (decimal?)(t.Max ?? 0);
            MaxPanel.IsVisible = !t.IsButton && t.EffectiveFormat is "power" or "kwh" or "percent";
            ButtonPanel.IsVisible = t.IsButton;
            FormatLabel.IsVisible = FormatBox.IsVisible = !t.IsButton || true;
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

    private void DeletePage()
    {
        if (_cfg.Pages.Count <= 1) return;
        Touch(null);
        _cfg.Pages.RemoveAt(_page);
        _page = Math.Clamp(_page, 0, _cfg.Pages.Count - 1);
        _sel = null;
        Changed(panels: true);
    }

    private void AddTile(bool isButton)
    {
        int w = isButton ? 1 : 2, h = 1;
        var spot = ConfigValidator.FindFreeSpot(Page, w, h) ?? ConfigValidator.FindFreeSpot(Page, 1, 1);
        if (spot == null || Page.Tiles.Count >= ConfigValidator.MaxTiles)
        {
            Status(T("Auf dieser Seite ist kein Platz mehr. Zuerst eine Kachel löschen oder eine Reihe hinzufügen.",
                     "No room left on this page. Delete a tile or add a row first."), error: true);
            return;
        }
        if (spot.Value is var (sx, sy) && !ConfigValidator.Fits(Page, null!, sx, sy, w, h)) w = 1;
        Touch(null);
        var tile = isButton
            ? new TileModel { Type = "button", X = spot.Value.X, Y = spot.Value.Y, W = w, H = h, Icon = "star", Title = new LText(T("Neu", "New"), T("Neu", "New")), Trigger = "tap" }
            : new TileModel { Type = "value", X = spot.Value.X, Y = spot.Value.Y, W = w, H = h, Format = "text", Title = new LText(T("Neu", "New"), T("Neu", "New")) };
        Page.Tiles.Add(tile);
        _sel = tile;
        Changed(panels: true);
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

    private void OnHostPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_cfg.Pages.Count == 0 || !e.GetCurrentPoint(Host).Properties.IsLeftButtonPressed) return;
        var p = e.GetPosition(Host);

        // Seitenleiste unten auf dem Display: Seite wechseln
        if (_cfg.Pages.Count > 1 && p.Y >= DisplayRenderer.NavY && p.Y <= DisplayRenderer.NavY + DisplayRenderer.NavH)
        {
            var n = _cfg.Pages.Count;
            var x0 = DisplayRenderer.GridX + (DisplayRenderer.GridW - (n * 220 + (n - 1) * 16)) / 2.0;
            var index = (int)Math.Floor((p.X - x0) / 236);
            if (index >= 0 && index < n && p.X - x0 - index * 236 <= 220 && index != _page)
            {
                _page = index;
                _sel = null;
                Refresh(panels: true);
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
