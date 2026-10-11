namespace DuoSeat;

/// <summary>
/// Main window, laid out like a classic multiseat console:
///   tabs (General Settings / Workplaces / About);
///   Workplaces = "System" column of shared devices on the left + one column per workplace;
///   devices are dragged from System onto a workplace (or use right-click > Move to...).
/// </summary>
public sealed class MainForm : Form
{
    private readonly Config _cfg = Config.Load();
    private readonly InputRouter _router;
    private readonly ToolTip _tip = new() { InitialDelay = 300, ShowAlways = true };
    private readonly System.Windows.Forms.Timer _tick = new() { Interval = 150 };

    // tabs
    private readonly TabButton[] _tabs = new TabButton[3];
    private readonly Panel[] _pages = new Panel[3];

    // workplaces page
    private readonly FlowLayoutPanel _sysDevices = new();
    private readonly Button _sysHeader = new();
    private readonly WorkplaceCell[] _cells = new WorkplaceCell[2];
    private readonly Dictionary<string, DeviceTile> _tiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _unknownSeen = new(StringComparer.OrdinalIgnoreCase);
    private readonly ContextMenuStrip _tileMenu = new();

    // general settings page
    private readonly Label _sysLabel = new() { AutoSize = true, MaximumSize = new Size(640, 0), RightToLeft = RightToLeft.Yes };
    private readonly TextBox _user = new() { Width = 170 };
    private readonly TextBox _pass = new() { Width = 170, UseSystemPasswordChar = true };
    private readonly TextBox _addr = new() { Width = 170 };
    private readonly NumericUpDown _speed = new() { DecimalPlaces = 1, Increment = 0.1M, Minimum = 0.3M, Maximum = 6M, Width = 70 };

    // bottom bar
    private readonly Button _btnToggle = new() { Text = "Start", Width = 170, Height = 30 };
    private readonly Label _status = new() { AutoSize = false, TextAlign = ContentAlignment.MiddleLeft };

    private bool _loading;

    public MainForm()
    {
        _router = new InputRouter(_cfg);

        Text = "DuoSeat";
        Font = new Font("Segoe UI", 9f);
        BackColor = Color.White;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(900, 540);
        MinimumSize = new Size(700, 460);

        BuildShell();
        BuildWorkplacesPage();
        BuildGeneralPage();
        BuildAboutPage();

        _loading = true;
        _user.Text = _cfg.Station2User;
        _addr.Text = _cfg.Station2Address;
        _speed.Value = (decimal)Math.Clamp(_cfg.MouseSpeed, 0.3, 6.0);
        _loading = false;

        var sys = SystemTools.CheckSystem();
        _sysLabel.Text = sys.Text;
        _sysLabel.ForeColor = sys.Ok ? Color.DarkGreen : Color.Firebrick;

        RefreshDevices();
        UpdateMonitorCaptions();

        _router.DeviceSeen += OnDeviceSeen;
        _router.ActiveChanged += OnActiveChanged;
        _btnToggle.Click += OnToggle;
        _tick.Tick += (_, _) => OnTick();
        _tick.Start();

        ShowTab(1);
        SetStatus("Ready. Emergency stop: Ctrl+Alt+F12");
        OnTick();
    }

    // ------------------------------------------------------------------ shell (tab strip + bottom bar)

    private void BuildShell()
    {
        var content = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(0, 0, 0, 0) };
        for (int i = 0; i < _pages.Length; i++)
        {
            _pages[i] = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Visible = false };
            content.Controls.Add(_pages[i]);
        }

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 46, Padding = new Padding(8, 7, 8, 7), BackColor = Color.White };
        _status.Dock = DockStyle.Fill;
        _status.Padding = new Padding(10, 0, 0, 0);
        _btnToggle.Dock = DockStyle.Left;
        bottom.Controls.Add(_status);
        bottom.Controls.Add(_btnToggle);
        bottom.Paint += (_, e) => e.Graphics.DrawLine(new Pen(Theme.LineSoft), 0, 0, bottom.Width, 0);

        var strip = new Panel { Dock = DockStyle.Top, Height = 36, BackColor = Color.White, Padding = new Padding(0, 2, 0, 0) };
        string[] titles = { "General Settings", "Workplaces", "About" };
        TabIcon[] icons = { TabIcon.Gear, TabIcon.Monitor, TabIcon.Info };
        int[] widths = { 150, 130, 100 };
        int x = 0;
        for (int i = 0; i < _tabs.Length; i++)
        {
            int idx = i;
            _tabs[i] = new TabButton { Text = titles[i], Icon = icons[i], Left = x, Top = 2, Width = widths[i], Height = 34 };
            _tabs[i].Click += (_, _) => ShowTab(idx);
            x += widths[i];
            strip.Controls.Add(_tabs[i]);
        }
        strip.Paint += (_, e) =>
        {
            using var pen = new Pen(Theme.Line);
            e.Graphics.DrawLine(pen, 0, strip.Height - 1, strip.Width, strip.Height - 1);
        };

        // Fill first, then edges.
        Controls.Add(content);
        Controls.Add(bottom);
        Controls.Add(strip);
    }

    private void ShowTab(int index)
    {
        if (!_loading) ReadSettings();
        for (int i = 0; i < _pages.Length; i++)
        {
            _pages[i].Visible = i == index;
            _tabs[i].Selected = i == index;
        }
    }

    // ------------------------------------------------------------------ Workplaces tab

    private void BuildWorkplacesPage()
    {
        var page = _pages[1];
        var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Padding = new Padding(0) };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 214));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        // ---- System column
        var sys = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(1), Margin = new Padding(0) };
        sys.Paint += (_, e) =>
        {
            using var pen = new Pen(Theme.Line);
            e.Graphics.DrawRectangle(pen, 0, 0, sys.Width - 1, sys.Height - 1);
        };

        var title = new Label
        {
            Dock = DockStyle.Fill, Text = "System", TextAlign = ContentAlignment.MiddleCenter,
            BorderStyle = BorderStyle.FixedSingle, BackColor = Color.White,
        };
        var sysMenuBtn = new Button
        {
            Dock = DockStyle.Right, Width = 28, Text = "≡", Font = new Font("Segoe UI", 12f, FontStyle.Bold),
            FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(0xF7, 0xF7, 0xF7), TabStop = false, Cursor = Cursors.Hand,
        };
        sysMenuBtn.FlatAppearance.BorderColor = Color.FromArgb(0x9A, 0x9A, 0x9A);
        var sysMenu = new ContextMenuStrip();
        sysMenu.Items.Add("Refresh device list", null, (_, _) => RefreshDevices());
        sysMenuBtn.Click += (_, _) => sysMenu.Show(sysMenuBtn, new Point(0, sysMenuBtn.Height));

        var sysTop = new Panel { Dock = DockStyle.Top, Height = 28, Padding = new Padding(4, 3, 4, 1), BackColor = Color.White };
        sysTop.Controls.Add(title);
        sysTop.Controls.Add(sysMenuBtn);

        _sysHeader.Dock = DockStyle.Top;
        _sysHeader.Height = 26;
        _sysHeader.Text = "▽  Shared devices";
        _sysHeader.FlatStyle = FlatStyle.Flat;
        _sysHeader.BackColor = Color.FromArgb(0xFB, 0xFB, 0xFB);
        _sysHeader.FlatAppearance.BorderColor = Theme.Line;
        _sysHeader.TabStop = false;
        _sysHeader.Cursor = Cursors.Hand;
        _sysHeader.Click += (_, _) =>
        {
            _sysDevices.Visible = !_sysDevices.Visible;
            _sysHeader.Text = (_sysDevices.Visible ? "▽" : "▷") + "  Shared devices";
        };

        _sysDevices.Dock = DockStyle.Fill;
        _sysDevices.FlowDirection = FlowDirection.LeftToRight;
        _sysDevices.WrapContents = true;
        _sysDevices.AutoScroll = true;
        _sysDevices.Padding = new Padding(6, 6, 6, 6);
        _sysDevices.BackColor = Color.White;

        sys.Controls.Add(_sysDevices);
        sys.Controls.Add(_sysHeader);
        sys.Controls.Add(sysTop);
        MakeDropTarget(sys, () => 0);
        MakeDropTarget(_sysDevices, () => 0);
        MakeDropTarget(sysTop, () => 0);
        MakeDropTarget(_sysHeader, () => 0);
        _tip.SetToolTip(_sysHeader, "Devices that are not assigned to a workplace. They work on Workplace 1.");

        // ---- Workplace columns
        var cells = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0) };
        cells.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        cells.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        cells.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        for (int i = 0; i < 2; i++)
        {
            var cell = new WorkplaceCell(i + 1) { Dock = DockStyle.Fill, Margin = new Padding(0) };
            _cells[i] = cell;
            cells.Controls.Add(cell, i, 0);

            var menu = new ContextMenuStrip();
            menu.Opening += (_, _) => BuildCellMenu(cell, menu);
            cell.MenuButton.Click += (_, _) => menu.Show(cell.MenuButton, new Point(0, cell.MenuButton.Height));
            cell.Monitor.Click += (_, _) => menu.Show(cell.Monitor, new Point(0, cell.Monitor.Height));
            _tip.SetToolTip(cell.Monitor, "Click to choose the display of this workplace");

            int station = i + 1;
            MakeDropTarget(cell, () => station);
            MakeDropTarget(cell.Header, () => station);
            MakeDropTarget(cell.MonitorHolder, () => station);
            MakeDropTarget(cell.Devices, () => station);
            MakeDropTarget(cell.Number, () => station);
            MakeDropTarget(cell.Monitor, () => station);
        }

        table.Controls.Add(sys, 0, 0);
        table.Controls.Add(cells, 1, 0);
        page.Controls.Add(table);

        // right-click on a device
        _tileMenu.Items.Add("Move to Workplace 1", null, (_, _) => MoveFromMenu(1));
        _tileMenu.Items.Add("Move to Workplace 2", null, (_, _) => MoveFromMenu(2));
        _tileMenu.Items.Add("Move to System (shared)", null, (_, _) => MoveFromMenu(0));
    }

    private void MoveFromMenu(int station)
    {
        if (_tileMenu.SourceControl is DeviceTile t) AssignDevice(t.DevicePath, station);
    }

    private void BuildCellMenu(WorkplaceCell cell, ContextMenuStrip menu)
    {
        menu.Items.Clear();
        var screens = Screen.AllScreens;
        int current = cell.Station == 1 ? _cfg.Monitor1 : _cfg.Monitor2;
        current = Math.Clamp(current, 0, screens.Length - 1);

        var mon = new ToolStripMenuItem("Display");
        for (int i = 0; i < screens.Length; i++)
        {
            int idx = i;
            var item = new ToolStripMenuItem(
                $"Display {i + 1}  ({screens[i].Bounds.Width}x{screens[i].Bounds.Height})" + (screens[i].Primary ? "  - primary" : ""))
            { Checked = i == current };
            item.Click += (_, _) => SetMonitor(cell.Station, idx);
            mon.DropDownItems.Add(item);
        }
        menu.Items.Add(mon);

        var clear = new ToolStripMenuItem("Remove all devices from this workplace");
        clear.Click += (_, _) =>
        {
            foreach (var x in _tiles.Values.Where(w => w.Station == cell.Station).ToList())
                AssignDevice(x.DevicePath, 0);
        };
        menu.Items.Add(clear);

        if (cell.Station == 2)
        {
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Prepare system (RDP + user)...", null, (_, _) => OnPrepare());
            menu.Items.Add("Launch workplace 2 session", null, (_, _) => OnConnect());
        }
    }

    private void SetMonitor(int station, int index)
    {
        if (station == 1) _cfg.Monitor1 = index; else _cfg.Monitor2 = index;
        _cfg.Save();
        UpdateMonitorCaptions();
        if (_router.Active)
        {
            try { _router.Stop(); _router.Start(); }
            catch (Exception ex) { Msg(ex.Message); }
        }
    }

    private void UpdateMonitorCaptions()
    {
        int n = Screen.AllScreens.Length;
        _cells[0].Monitor.Caption = $"Display {Math.Clamp(_cfg.Monitor1, 0, n - 1) + 1}";
        _cells[1].Monitor.Caption = $"Display {Math.Clamp(_cfg.Monitor2, 0, n - 1) + 1}";
    }

    // ---- devices

    private void RefreshDevices()
    {
        foreach (var t in _tiles.Values)
        {
            t.Parent?.Controls.Remove(t);
            t.Dispose();
        }
        _tiles.Clear();

        var s1 = new HashSet<string>(_cfg.Station1Devices, StringComparer.OrdinalIgnoreCase);
        var s2 = new HashSet<string>(_cfg.Station2Devices, StringComparer.OrdinalIgnoreCase);

        foreach (var d in DeviceList.Enumerate())
        {
            var tile = new DeviceTile(d.Path, d.IsKeyboard);
            _tip.SetToolTip(tile, $"{d.TypeLabel}: {d.Friendly}\nDrag to a workplace, or right-click.");
            tile.ContextMenuStrip = _tileMenu;
            MakeDropTarget(tile, () => tile.Station);
            _tiles[d.Path] = tile;
            Place(tile, s2.Contains(d.Path) ? 2 : s1.Contains(d.Path) ? 1 : 0);
        }
    }

    private FlowLayoutPanel HostFor(int station) => station == 0 ? _sysDevices : _cells[station - 1].Devices;

    private void Place(DeviceTile tile, int station)
    {
        tile.Station = station;
        var host = HostFor(station);
        host.Controls.Add(tile);
        var ordered = host.Controls.OfType<DeviceTile>()
            .OrderBy(t => t.IsKeyboard ? 0 : 1).ThenBy(t => t.DevicePath, StringComparer.OrdinalIgnoreCase).ToList();
        for (int i = 0; i < ordered.Count; i++)
            host.Controls.SetChildIndex(ordered[i], i);
    }

    private void AssignDevice(string path, int station)
    {
        if (!_tiles.TryGetValue(path, out var tile)) return;

        _cfg.Station1Devices.RemoveAll(p => p.Equals(path, StringComparison.OrdinalIgnoreCase));
        _cfg.Station2Devices.RemoveAll(p => p.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (station == 1) _cfg.Station1Devices.Add(path);
        if (station == 2) _cfg.Station2Devices.Add(path);
        _cfg.Save();

        tile.Parent?.Controls.Remove(tile);
        Place(tile, station);
        _router.ReloadAssignments();
    }

    private void MakeDropTarget(Control c, Func<int> station)
    {
        c.AllowDrop = true;
        void Check(object? s, DragEventArgs e) =>
            e.Effect = e.Data?.GetDataPresent(DeviceTile.DragFormat) == true ? DragDropEffects.Move : DragDropEffects.None;
        c.DragEnter += Check;
        c.DragOver += Check;
        c.DragDrop += (_, e) =>
        {
            if (e.Data?.GetData(DeviceTile.DragFormat) is string p) AssignDevice(p, station());
        };
    }

    private void OnDeviceSeen(string path, bool isKeyboard)
    {
        if (string.IsNullOrEmpty(path)) return;
        if (_tiles.TryGetValue(path, out var tile))
        {
            tile.Flash();
            return;
        }
        // a device that was plugged in after startup
        if (_unknownSeen.Add(path))
        {
            RefreshDevices();
            if (_tiles.TryGetValue(path, out tile)) tile.Flash();
        }
    }

    // ---- periodic UI refresh (lamps + highlight expiry)

    private void OnTick()
    {
        foreach (var t in _tiles.Values) t.Tick();

        bool act = _router.Active;
        SetLamp(_cells[0].Led, act ? Theme.Green : Theme.Amber,
            act ? "Workplace 1: running" : "Workplace 1: ready (distribution stopped)");

        if (!act)
        {
            SetLamp(_cells[1].Led, Theme.Amber, "Workplace 2: ready (distribution stopped)");
        }
        else if (!_router.Station2Connected)
        {
            SetLamp(_cells[1].Led, Theme.Red, "Workplace 2: no session window found - launch the workplace 2 session");
        }
        else if (_cfg.Station2Devices.Count == 0)
        {
            SetLamp(_cells[1].Led, Theme.Red, "Workplace 2: no devices assigned");
        }
        else
        {
            SetLamp(_cells[1].Led, Theme.Green, "Workplace 2: running");
        }
    }

    private void SetLamp(LedBox led, Color color, string tip)
    {
        led.State = color;
        if (!string.Equals(led.Tag as string, tip, StringComparison.Ordinal))
        {
            led.Tag = tip;
            _tip.SetToolTip(led, tip);
        }
    }

    // ------------------------------------------------------------------ General Settings tab

    private void BuildGeneralPage()
    {
        var page = _pages[0];
        var box = new GroupBox { Text = "Workplace 2 (separate Windows user over local Remote Desktop)", Left = 16, Top = 16, Width = 660, Height = 250 };

        var grid = new TableLayoutPanel { Left = 12, Top = 24, Width = 630, Height = 110, ColumnCount = 4, RowCount = 2 };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 200));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 200));
        void Cell(string label, Control c, int col, int row)
        {
            grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(0, 6, 0, 0) }, col, row);
            grid.Controls.Add(c, col + 1, row);
        }
        Cell("User name:", _user, 0, 0);
        Cell("Password:", _pass, 2, 0);
        Cell("RDP address:", _addr, 0, 1);
        Cell("Mouse speed:", _speed, 2, 1);

        var prepare = new Button { Text = "Prepare system (RDP + user)", Left = 12, Top = 146, Width = 220, Height = 30 };
        var connect = new Button { Text = "Launch workplace 2 session", Left = 242, Top = 146, Width = 220, Height = 30 };
        prepare.Click += (_, _) => OnPrepare();
        connect.Click += (_, _) => OnConnect();

        _sysLabel.Left = 12;
        _sysLabel.Top = 188;
        _sysLabel.MaximumSize = new Size(630, 0);

        box.Controls.Add(grid);
        box.Controls.Add(prepare);
        box.Controls.Add(connect);
        box.Controls.Add(_sysLabel);

        var note = new Label
        {
            Left = 16, Top = 280, Width = 660, Height = 60,
            Text = "Steps: 1) Prepare system.  2) Launch workplace 2 session.  3) On the Workplaces tab drag the second keyboard and mouse " +
                   "onto Workplace 2, choose each display, then press Start.\nThe password is never saved to disk.",
        };

        page.Controls.Add(box);
        page.Controls.Add(note);

        _user.Leave += (_, _) => ReadSettings();
        _addr.Leave += (_, _) => ReadSettings();
        _speed.Leave += (_, _) => ReadSettings();
    }

    private void ReadSettings()
    {
        if (_loading) return;
        _cfg.Station2User = _user.Text.Trim();
        _cfg.Station2Address = _addr.Text.Trim();
        _cfg.MouseSpeed = (double)_speed.Value;
        _cfg.Save();
    }

    // ------------------------------------------------------------------ About tab

    private void BuildAboutPage()
    {
        var v = typeof(MainForm).Assembly.GetName().Version;
        string ver = v == null ? "" : $"{v.Major}.{v.Minor}.{v.Build}";

        var title = new Label { Left = 20, Top = 20, AutoSize = true, Font = new Font("Segoe UI", 16f, FontStyle.Bold), Text = "DuoSeat " + ver };
        var body = new Label
        {
            Left = 22, Top = 64, Width = 700, Height = 330,
            Text =
                "Two workplaces on one PC: each gets its own display, keyboard and mouse.\n\n" +
                "Emergency stop: Ctrl + Alt + F12 stops device distribution at once.\n\n" +
                "Important:\n" +
                " - Workplace 2 is a local Remote Desktop session of a different Windows user. Windows client editions (Home/Pro/Enterprise) allow only " +
                "one interactive session, so this needs Windows Server with Remote Desktop Services.\n" +
                " - Devices are split in user mode (Raw Input + hooks). There is one real mouse pointer, which jumps between the displays. " +
                "Fully independent pointers need a kernel driver, which this build does not include.\n" +
                " - Ctrl+Alt+Del and Pause/Break are not forwarded. Audio, printers and USB storage are not split.\n\n" +
                "MIT License.",
        };
        _pages[2].Controls.Add(title);
        _pages[2].Controls.Add(body);
    }

    // ------------------------------------------------------------------ buttons

    private void OnActiveChanged(bool active)
    {
        _btnToggle.Text = active ? "Stop  (Ctrl+Alt+F12)" : "Start";
        SetStatus(active ? "Distribution is running. Emergency stop: Ctrl+Alt+F12" : "Distribution stopped. Devices are back to normal.");
        OnTick();
    }

    private void OnPrepare()
    {
        ReadSettings();
        if (string.IsNullOrWhiteSpace(_cfg.Station2User) || string.IsNullOrWhiteSpace(_pass.Text))
        {
            Msg("اكتب اسم المستخدم وكلمة مرور للمحطة 2 أولاً (تبويب General Settings).");
            ShowTab(0);
            return;
        }
        string log = SystemTools.PrepareStation2(_cfg.Station2User, _pass.Text);
        Msg(log, "Prepare system");
    }

    private void OnConnect()
    {
        ReadSettings();
        if (string.IsNullOrWhiteSpace(_pass.Text))
        {
            Msg("اكتب كلمة مرور مستخدم المحطة 2 في تبويب General Settings (لا تُحفظ على القرص).");
            ShowTab(0);
            return;
        }
        try
        {
            var screens = Screen.AllScreens;
            var bounds = screens[Math.Clamp(_cfg.Monitor2, 0, screens.Length - 1)].Bounds;
            SystemTools.ConnectStation2(_cfg, _pass.Text, bounds);
            SetStatus("Workplace 2 session launched. When its desktop appears, press Start.");
        }
        catch (Exception ex)
        {
            Msg(ex.Message, "Cannot launch workplace 2");
        }
    }

    private void OnToggle(object? sender, EventArgs e)
    {
        try
        {
            if (_router.Active)
            {
                _router.Stop();
                return;
            }

            ReadSettings();
            if (_cfg.Station2Devices.Count == 0)
            {
                Msg("لم تحدد أي جهاز للمحطة 2. اسحب لوحة المفاتيح والماوس الخاصين بها إلى عمود Workplace 2 أولاً.");
                ShowTab(1);
                return;
            }
            if (Screen.AllScreens.Length < 2 || _cfg.Monitor1 == _cfg.Monitor2)
            {
                var r = MessageBox.Show(this,
                    "المحطتان على نفس الشاشة (أو لا توجد شاشة ثانية). التوزيع سيعمل لكن المؤشرين سيتشاركان نفس الشاشة. متابعة؟",
                    "DuoSeat", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button1,
                    MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign);
                if (r != DialogResult.Yes) return;
            }
            _router.Start();
        }
        catch (Exception ex)
        {
            Msg(ex.Message, "Error");
        }
    }

    private void Msg(string text, string title = "DuoSeat") =>
        MessageBox.Show(this, text, title, MessageBoxButtons.OK, MessageBoxIcon.None, MessageBoxDefaultButton.Button1,
            MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign);

    private void SetStatus(string text) => _status.Text = text;

    // ------------------------------------------------------------------ window plumbing

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try { _router.RegisterRawInput(Handle); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Raw Input"); }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_INPUT)
        {
            try { _router.OnRawInput(m.LParam); }
            catch { /* never let an input glitch kill the message loop */ }
        }
        base.WndProc(ref m); // DefWindowProc must run so the system can clean up raw input data
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _tick.Stop();
        ReadSettings();
        _router.Stop();
        SystemTools.ForgetCredentials(_cfg);
        base.OnFormClosing(e);
    }
}
