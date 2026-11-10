// NetWatch - the main window: Sending / Receiving / Blocked tabs, the program list and the server list.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace NetWatch
{
    internal sealed class MainForm : Form
    {
        const int TabSend = 0, TabRecv = 1, TabBlocked = 2;
        const int ColApp = 0, ColChart = 1, ColNow = 2, ColTotal = 3, ColHosts = 4, ColLast = 5, ColAction = 6;
        const int RColHost = 0, RColAddress = 1, RColPort = 2, RColSent = 3, RColRecv = 4, RColLast = 5;

        readonly AppOptions _options;
        readonly Settings _settings;
        readonly TrafficModel _model = new TrafficModel();
        readonly NameResolver _resolver;
        EtwSession _etw;
        DemoFeeder _demo;
        int _etwError;
        long _lostEvents;

        readonly FlatButton _tabSend = new FlatButton();
        readonly FlatButton _tabRecv = new FlatButton();
        readonly FlatButton _tabBlocked = new FlatButton();
        readonly FlatButton _btnOptions = new FlatButton();
        readonly FlatButton _btnRetry = new FlatButton();
        readonly BufferedPanel _notice = new BufferedPanel();
        readonly BufferedPanel _summary = new BufferedPanel();
        readonly BufferedPanel _detailHeader = new BufferedPanel();
        readonly BufferedPanel _status = new BufferedPanel();
        readonly GridList _apps = new GridList();
        readonly GridList _details = new GridList();
        readonly GridList _blocked = new GridList();
        readonly ContextMenuStrip _menu = new ContextMenuStrip();
        readonly ContextMenuStrip _rowMenu = new ContextMenuStrip();
        ToolStripMenuItem _miLan, _miNames;
        readonly System.Windows.Forms.Timer _timer = new System.Windows.Forms.Timer();
        readonly System.Windows.Forms.Timer _testTimer = new System.Windows.Forms.Timer();
        System.Threading.Timer _maintenance;
        int _maintenanceCount;

        int _tab = TabSend;
        readonly Dictionary<int, AppSnapshot> _cache = new Dictionary<int, AppSnapshot>();
        readonly List<AppSnapshot> _all = new List<AppSnapshot>();
        readonly List<AppSnapshot> _rows = new List<AppSnapshot>();
        readonly List<RemoteSnapshot> _remotes = new List<RemoteSnapshot>();
        readonly TotalsSnapshot _totals = new TotalsSnapshot();
        int _selectedId = -1;
        string _selectedName = "";
        bool _selectedVisible;
        bool _hasSelectedRemote;
        IpKey _selectedRemote;
        int _appSort = ColTotal;
        bool _appSortDescending = true;
        int _remoteSort = RColSent;
        bool _remoteSortDescending = true;

        List<BlockedApp> _blockedList = new List<BlockedApp>();
        readonly Dictionary<string, BlockedApp> _blockedByPath = new Dictionary<string, BlockedApp>(StringComparer.OrdinalIgnoreCase);
        bool? _firewallOn;
        long _nowFileTime;
        float _detailFraction;
        bool _draggingSplit;
        string _noticeText = "";
        Color _noticeColor = Theme.Up;
        bool _noticeShown, _retryShown;
        int _hiddenWindows, _unusual;
        readonly FlatButton _btnWindows = new FlatButton();
        readonly FlatButton _btnDonate = new FlatButton();
        int _tick;
        int _testStep;
        Form _testDialog;
        bool _closing;

        int TopBarHeight { get { return Theme.S(62); } }

        public MainForm(AppOptions options, Settings settings)
        {
            Theme.Init();
            _options = options;
            _settings = settings;
            _resolver = new NameResolver(_model);
            _resolver.Enabled = settings.LookupNames && !options.Demo;
            _detailFraction = options.ShowcaseDir != null ? 0.26f : settings.DetailFraction;

            Text = options.Demo && options.ShowcaseDir == null ? "NetWatch  (demo data)" : "NetWatch";
            BackColor = Theme.Window;
            ForeColor = Theme.Text;
            AutoScaleMode = AutoScaleMode.None;
            DoubleBuffered = true;
            KeyPreview = true;
            MinimumSize = new Size(Theme.S(980), Theme.S(640));

            Rectangle area = Screen.PrimaryScreen.WorkingArea;
            int width = Math.Min(Theme.S(settings.Width), area.Width);
            int height = Math.Min(Theme.S(settings.Height), area.Height);
            Size = new Size(Math.Max(width, MinimumSize.Width), Math.Max(height, MinimumSize.Height));
            if (options.Scripted)
            {
                StartPosition = FormStartPosition.Manual;
                Location = new Point(area.Left + 30, area.Top + 30);
            }
            else
            {
                StartPosition = FormStartPosition.CenterScreen;
            }
            try
            {
                using (Stream stream = typeof(MainForm).Assembly.GetManifestResourceStream("NetWatch.ico"))
                    if (stream != null) Icon = new Icon(stream);
            }
            catch
            {
            }

            BuildTopBar();
            BuildPanels();
            BuildGrids();
            BuildMenus();
            SetTab(TabSend);

            _timer.Interval = 1000;
            _timer.Tick += delegate { OnTick(); };
        }

        protected override bool ShowWithoutActivation
        {
            get { return _options.Scripted; }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.DarkTitleBar(Handle);
        }

        // ------------------------------------------------------------------ building

        void BuildTopBar()
        {
            ConfigureTab(_tabSend, "Sending", Glyph.Up, Theme.Up, 84);
            ConfigureTab(_tabRecv, "Receiving", Glyph.Down, Theme.Down, 84);
            ConfigureTab(_tabBlocked, "Blocked", Glyph.Block, Theme.Danger, 20);
            _tabSend.Click += delegate { SetTab(TabSend); };
            _tabRecv.Click += delegate { SetTab(TabRecv); };
            _tabBlocked.Click += delegate { SetTab(TabBlocked); };

            _btnOptions.Kind = ButtonKind.Normal;
            _btnOptions.Glyph = Glyph.Chevron;
            _btnOptions.Text = "Options";
            _btnOptions.Click += delegate { _menu.Show(_btnOptions, new Point(0, _btnOptions.Height + Theme.S(4))); };
            Controls.Add(_btnOptions);

            _btnWindows.Kind = ButtonKind.Ghost;
            _btnWindows.Click += delegate { SetHideWindows(!_settings.HideWindows); };
            Controls.Add(_btnWindows);
            UpdateWindowsButton();

            _btnDonate.Kind = ButtonKind.Ghost;
            _btnDonate.Text = "Donate";
            _btnDonate.Click += delegate { ShowDonate(); };
            Controls.Add(_btnDonate);
        }

        void ShowDonate()
        {
            using (DonateDialog dialog = new DonateDialog())
                dialog.ShowDialog(this);
        }

        void SetHideWindows(bool hide)
        {
            _settings.HideWindows = hide;
            _settings.Save();
            UpdateWindowsButton();
            DoLayout();
            RefreshView();
        }

        void UpdateWindowsButton()
        {
            _btnWindows.Text = _settings.HideWindows ? "Show Windows processes" : "Hide Windows processes";
        }

        void ConfigureTab(FlatButton tab, string text, Glyph glyph, Color accent, int subWidth)
        {
            tab.Kind = ButtonKind.Tab;
            tab.Text = text;
            tab.Glyph = glyph;
            tab.Accent = accent;
            tab.SubWidth = subWidth;
            Controls.Add(tab);
        }

        void BuildPanels()
        {
            _notice.Visible = false;
            _notice.Paint += PaintNotice;
            _btnRetry.Kind = ButtonKind.Normal;
            _btnRetry.Text = "Try again";
            _btnRetry.Visible = false;
            _btnRetry.Click += delegate { StartCapture(); };
            _notice.Controls.Add(_btnRetry);
            Controls.Add(_notice);

            _summary.Paint += PaintSummary;
            Controls.Add(_summary);

            _detailHeader.BackColor = Theme.Panel;
            _detailHeader.Cursor = Cursors.SizeNS;
            _detailHeader.Paint += PaintDetailHeader;
            _detailHeader.MouseDown += delegate(object s, MouseEventArgs e) { if (e.Button == MouseButtons.Left) _draggingSplit = true; };
            _detailHeader.MouseUp += delegate { _draggingSplit = false; };
            _detailHeader.MouseMove += delegate(object s, MouseEventArgs e) { if (_draggingSplit) DragSplit(); };
            Controls.Add(_detailHeader);

            _status.Paint += PaintStatus;
            Controls.Add(_status);
        }

        void BuildGrids()
        {
            GridColumn c;

            // programs
            c = new GridColumn("PROGRAM", 0); c.Fill = 1; c.MinWidth = 230; _apps.Columns.Add(c);
            c = new GridColumn("LAST 60 SECONDS", 176); c.Sortable = false; _apps.Columns.Add(c);
            c = new GridColumn("", 124); c.RightAlign = true; _apps.Columns.Add(c);
            c = new GridColumn("", 124); c.RightAlign = true; _apps.Columns.Add(c);
            c = new GridColumn("SERVERS", 88); c.RightAlign = true; _apps.Columns.Add(c);
            c = new GridColumn("", 118); c.RightAlign = true; _apps.Columns.Add(c);
            c = new GridColumn("", 128); c.IsButton = true; c.Sortable = false; _apps.Columns.Add(c);
            _apps.RowHeight = Theme.S(54);
            _apps.PaintCell = PaintAppCell;
            _apps.RowHasButton = delegate(int row) { return row < _rows.Count && _rows[row].CanBlock; };
            _apps.RowKey = delegate(int row) { return row < _rows.Count ? (object)_rows[row].Id : null; };
            _apps.RowAlert = delegate(int row) { return row < _rows.Count && _rows[row].Warning != null; };
            _apps.CellToolTip = AppToolTip;
            _apps.SelectionChanged += delegate { OnAppSelected(); };
            _apps.HeaderClicked += OnAppHeader;
            _apps.ButtonClicked += OnAppButton;
            _apps.RowRightClicked += OnAppRightClick;
            Controls.Add(_apps);

            // servers of the selected program
            c = new GridColumn("SERVER", 0); c.Fill = 1; c.MinWidth = 200; _details.Columns.Add(c);
            c = new GridColumn("ADDRESS", 250); _details.Columns.Add(c);
            c = new GridColumn("PORT", 200); _details.Columns.Add(c);
            c = new GridColumn("SENT", 108); c.RightAlign = true; _details.Columns.Add(c);
            c = new GridColumn("RECEIVED", 108); c.RightAlign = true; _details.Columns.Add(c);
            c = new GridColumn("LAST ACTIVE", 118); c.RightAlign = true; _details.Columns.Add(c);
            _details.RowHeight = Theme.S(32);
            _details.PaintCell = PaintRemoteCell;
            _details.CellToolTip = RemoteToolTip;
            _details.SelectionChanged += delegate
            {
                int i = _details.SelectedIndex;
                _hasSelectedRemote = i >= 0 && i < _remotes.Count;
                if (_hasSelectedRemote) _selectedRemote = _remotes[i].Ip;
            };
            _details.HeaderClicked += OnRemoteHeader;
            _details.RowRightClicked += OnRemoteRightClick;
            Controls.Add(_details);

            // blocked programs
            c = new GridColumn("BLOCKED PROGRAM", 0); c.Fill = 1; c.MinWidth = 300; c.Sortable = false; _blocked.Columns.Add(c);
            c = new GridColumn("", 128); c.IsButton = true; c.Sortable = false; _blocked.Columns.Add(c);
            _blocked.RowHeight = Theme.S(54);
            _blocked.Selectable = false;
            _blocked.Accent = Theme.Danger;
            _blocked.PaintCell = PaintBlockedCell;
            _blocked.RowHasButton = delegate(int row) { return row < _blockedList.Count; };
            _blocked.RowKey = delegate(int row) { return row < _blockedList.Count ? _blockedList[row].Path : null; };
            _blocked.CellToolTip = delegate(int row, int col) { return row < _blockedList.Count ? _blockedList[row].Path : null; };
            _blocked.ButtonClicked += delegate(int row)
            {
                if (row < _blockedList.Count) ToggleBlock(_blockedList[row].Path, FriendlyName(_blockedList[row]), true);
            };
            _blocked.EmptyText = "Nothing is blocked.\n\nTo stop a program from using the network, open the Sending or Receiving tab and press Block next to it.";
            _blocked.Visible = false;
            Controls.Add(_blocked);
        }

        void BuildMenus()
        {
            StyleMenu(_menu, true);
            _miLan = new ToolStripMenuItem("Also count traffic inside my home network");
            _miLan.CheckOnClick = true;
            _miLan.Checked = _settings.IncludeLan;
            _miLan.Click += delegate
            {
                _settings.IncludeLan = _miLan.Checked;
                _settings.Save();
                RefreshView();
            };
            _miNames = new ToolStripMenuItem("Look up the names of servers");
            _miNames.CheckOnClick = true;
            _miNames.Checked = _settings.LookupNames;
            _miNames.Click += delegate
            {
                _settings.LookupNames = _miNames.Checked;
                _settings.Save();
                _resolver.Enabled = _settings.LookupNames && !_options.Demo;
            };
            ToolStripMenuItem reset = new ToolStripMenuItem("Start counting again from zero\u2026");
            reset.Click += delegate { ResetCounters(); };
            ToolStripMenuItem about = new ToolStripMenuItem("About NetWatch");
            about.Click += delegate { ShowAbout(); };
            ToolStripMenuItem donate = new ToolStripMenuItem("Donate\u2026");
            donate.Click += delegate { ShowDonate(); };
            ToolStripMenuItem link = new ToolStripMenuItem("Copy the project's web address");
            link.Click += delegate { CopyText(ProjectInfo.Website); };
            _menu.Items.Add(_miLan);
            _menu.Items.Add(_miNames);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(reset);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(donate);
            _menu.Items.Add(link);
            _menu.Items.Add(about);

            StyleMenu(_rowMenu, false);
        }

        static void StyleMenu(ContextMenuStrip menu, bool checks)
        {
            menu.Renderer = new DarkMenuRenderer();
            menu.BackColor = Theme.Panel;
            menu.ForeColor = Theme.Text;
            menu.Font = Theme.Body;
            menu.ShowImageMargin = false;
            menu.ShowCheckMargin = checks;
        }

        // -------------------------------------------------------------------- layout

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            DoLayout();
            Invalidate();
        }

        void DoLayout()
        {
            int w = ClientSize.Width, h = ClientSize.Height;
            if (w <= 0 || h <= 0) return;

            int x = Theme.S(14);
            FlatButton[] tabs = new FlatButton[] { _tabSend, _tabRecv, _tabBlocked };
            foreach (FlatButton tab in tabs)
            {
                int tabWidth = tab.PreferredWidth();
                tab.Bounds = new Rectangle(x, Theme.S(9), tabWidth, Theme.S(46));
                x += tabWidth + Theme.S(4);
            }
            int optionsWidth = _btnOptions.PreferredWidth();
            _btnOptions.Bounds = new Rectangle(w - Theme.S(18) - optionsWidth, Theme.S(15), optionsWidth, Theme.S(32));
            int windowsWidth = _btnWindows.PreferredWidth();
            _btnWindows.Bounds = new Rectangle(_btnOptions.Left - Theme.S(10) - windowsWidth, Theme.S(15), windowsWidth, Theme.S(32));
            _btnWindows.Visible = _tab != TabBlocked && _btnWindows.Left > x + Theme.S(8);
            int donateWidth = _btnDonate.PreferredWidth();
            int donateRight = (_tab != TabBlocked ? _btnWindows.Left : _btnOptions.Left) - Theme.S(10);
            _btnDonate.Bounds = new Rectangle(donateRight - donateWidth, Theme.S(15), donateWidth, Theme.S(32));
            _btnDonate.Visible = _btnDonate.Left > x + Theme.S(8);

            int y = TopBarHeight;
            if (_noticeShown)
            {
                int noticeHeight = Theme.S(44);
                _notice.Bounds = new Rectangle(0, y, w, noticeHeight);
                int retryWidth = _btnRetry.PreferredWidth();
                _btnRetry.Bounds = new Rectangle(w - Theme.S(18) - retryWidth, Theme.S(7), retryWidth, Theme.S(30));
                y += noticeHeight;
            }
            int summaryHeight = Theme.S(98);
            _summary.Bounds = new Rectangle(0, y, w, summaryHeight);
            y += summaryHeight;

            int statusHeight = Theme.S(30);
            int bodyHeight = Math.Max(0, h - y - statusHeight);
            if (_tab == TabBlocked)
            {
                _blocked.Bounds = new Rectangle(0, y, w, bodyHeight);
            }
            else
            {
                int headerHeight = Theme.S(46);
                int maxDetail = Math.Max(0, bodyHeight - headerHeight - Theme.S(140));
                int detailHeight = (int)(bodyHeight * _detailFraction);
                if (detailHeight < Theme.S(100)) detailHeight = Theme.S(100);
                if (detailHeight > maxDetail) detailHeight = maxDetail;
                int appsHeight = bodyHeight - headerHeight - detailHeight;
                _apps.Bounds = new Rectangle(0, y, w, appsHeight);
                _detailHeader.Bounds = new Rectangle(0, y + appsHeight, w, headerHeight);
                _details.Bounds = new Rectangle(0, y + appsHeight + headerHeight, w, detailHeight);
            }
            _status.Bounds = new Rectangle(0, h - statusHeight, w, statusHeight);
        }

        void DragSplit()
        {
            Point p = PointToClient(MousePosition);
            int bodyTop = _summary.Bottom;
            int bodyHeight = _status.Top - bodyTop;
            if (bodyHeight <= 0) return;
            int detailHeight = _status.Top - p.Y - _detailHeader.Height / 2;
            float fraction = detailHeight / (float)bodyHeight;
            if (fraction < 0.12f) fraction = 0.12f;
            if (fraction > 0.75f) fraction = 0.75f;
            if (Math.Abs(fraction - _detailFraction) < 0.002f) return;
            _detailFraction = fraction;
            DoLayout();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (Pen line = new Pen(Theme.Line))
                e.Graphics.DrawLine(line, 0, TopBarHeight - 1, ClientSize.Width, TopBarHeight - 1);
        }

        // ---------------------------------------------------------------- life cycle

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            DoLayout();

            if (_options.Demo)
            {
                _demo = new DemoFeeder(_model);
                if (_options.ShowcaseDir != null) _demo.StartShowcase(); else _demo.Start();
                List<BlockedApp> fake = new List<BlockedApp>();
                foreach (string path in _demo.SamplePaths(_options.ShowcaseDir != null ? 0 : 2))
                {
                    BlockedApp b = new BlockedApp();
                    b.Path = path;
                    b.Name = Path.GetFileName(path);
                    b.Outgoing = true;
                    b.Incoming = true;
                    fake.Add(b);
                }
                ApplyBlocked(fake, true);
                UpdateNotice();
            }
            else
            {
                if (_options.DiagFile != null)
                {
                    _model.EnableRawLog(120);
                    _adapterBase = ReadAdapters();
                }
                StartCapture();
                ThreadPool.QueueUserWorkItem(delegate { NameResolver.SeedFromWindowsDnsCache(_model); });
                LoadBlockedInBackground();
            }

            // (the invented programs of the showcase do not exist, so there is nothing to maintain)
            if (_options.ShowcaseDir == null) _maintenance = new System.Threading.Timer(OnMaintenance, null, 3000, 3000);
            _timer.Start();
            RefreshView();

            if (_options.SelfTestDir != null)
            {
                _testTimer.Interval = 3500;
                _testTimer.Tick += delegate { RunSelfTestStep(); };
                _testTimer.Start();
            }
            else if (_options.ShowcaseDir != null)
            {
                _testTimer.Interval = 3500;
                _testTimer.Tick += delegate { RunShowcaseStep(); };
                _testTimer.Start();
            }
            else if (_options.DiagFile != null)
            {
                _testTimer.Interval = 10000;
                _testTimer.Tick += delegate { WriteDiagnostics(); };
                _testTimer.Start();
            }
        }

        void StartCapture()
        {
            if (_etw != null)
            {
                _etw.Dispose();
                _etw = null;
            }
            _etw = new EtwSession(_model);
            try { _etwError = _etw.Start(); }
            catch (Exception ex)
            {
                _etwError = -1;
                Program.Log("capture start failed: " + ex);
            }
            UpdateNotice();
        }

        void UpdateNotice()
        {
            bool show = false, retry = false;
            if (_options.Demo && _options.ShowcaseDir == null)
            {
                _noticeText = "Demo mode: every number here is invented for testing. Nothing on this screen is real traffic.";
                _noticeColor = Theme.Up;
                show = true;
            }
            else if (_etwError != 0)
            {
                _noticeText = _etwError == 5
                    ? "Windows refused to share the traffic information. Close NetWatch, start it again and answer Yes when Windows asks for permission."
                    : "NetWatch could not start watching the network (Windows error " + _etwError.ToString(CultureInfo.InvariantCulture) + ").";
                _noticeColor = Theme.Danger;
                show = true;
                retry = true;
            }
            // (Control.Visible reads false until the window itself is shown, so keep the wanted state ourselves.)
            _noticeShown = show;
            _retryShown = retry;
            _btnRetry.Visible = retry;
            _notice.Visible = show;
            DoLayout();
            _notice.Invalidate();
            _status.Invalidate();
        }

        void OnMaintenance(object state)
        {
            if (_closing) return;
            try
            {
                _model.Maintain();
                if (++_maintenanceCount % 20 == 0 && !_options.Demo) LoadBlockedInBackground();
            }
            catch (Exception ex)
            {
                Program.Log("maintenance: " + ex.Message);
            }
        }

        void OnTick()
        {
            _tick++;
            if (_etw != null && _tick % 5 == 0) _lostEvents = _etw.QueryLostEvents();
            if (WindowState != FormWindowState.Minimized) RefreshView();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            _closing = true;
            if (WindowState == FormWindowState.Normal && !_options.Scripted)
            {
                _settings.Width = (int)(Width / Theme.Scale);
                _settings.Height = (int)(Height / Theme.Scale);
            }
            _settings.DetailFraction = _detailFraction;
            if (!_options.Scripted) _settings.Save();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _timer.Stop();
            _testTimer.Stop();
            if (_maintenance != null) _maintenance.Dispose();
            if (_demo != null) _demo.Dispose();
            if (_etw != null) _etw.Dispose();
            base.OnFormClosed(e);
        }

        /// <summary>Brings the window to the front (another copy of NetWatch was started).</summary>
        public void BringToFront2()
        {
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
            NativeMethods.SetForegroundWindow(Handle);
        }

        // ---------------------------------------------------------------------- tabs

        void SetTab(int tab)
        {
            _tab = tab;
            _tabSend.Selected = tab == TabSend;
            _tabRecv.Selected = tab == TabRecv;
            _tabBlocked.Selected = tab == TabBlocked;

            bool traffic = tab != TabBlocked;
            _apps.Visible = traffic;
            _detailHeader.Visible = traffic;
            _details.Visible = traffic;
            _blocked.Visible = !traffic;

            if (traffic)
            {
                bool send = tab == TabSend;
                Color accent = send ? Theme.Up : Theme.Down;
                _apps.Accent = accent;
                _details.Accent = accent;
                _apps.Columns[ColNow].Title = send ? "SENDING NOW" : "RECEIVING NOW";
                _apps.Columns[ColTotal].Title = send ? "TOTAL SENT" : "TOTAL RECEIVED";
                _apps.Columns[ColLast].Title = send ? "LAST SENT" : "LAST RECEIVED";
                _apps.EmptyText = send
                    ? "No program has sent anything yet.\n\nNetWatch counts from the moment it was opened, so programs appear here as soon as they send data."
                    : "No program has received anything yet.\n\nNetWatch counts from the moment it was opened, so programs appear here as soon as they receive data.";
                if (_remoteSort == RColSent || _remoteSort == RColRecv)
                {
                    _remoteSort = send ? RColSent : RColRecv;
                    _remoteSortDescending = true;
                }
            }
            DoLayout();
            RefreshView();
        }

        // ---------------------------------------------------------------------- data

        void RefreshView()
        {
            _nowFileTime = DateTime.UtcNow.ToFileTimeUtc();
            long nowSecond = _nowFileTime / 10000000L;
            bool lan = _settings.IncludeLan;
            _model.Snapshot(lan, nowSecond, _cache, _all, _totals);
            foreach (AppSnapshot a in _all)
                a.Blocked = a.Path != null && _blockedByPath.ContainsKey(a.Path);

            _tabSend.SubText = _totals.SentRate >= 1 ? Theme.Rate(_totals.SentRate) : "idle";
            _tabRecv.SubText = _totals.RecvRate >= 1 ? Theme.Rate(_totals.RecvRate) : "idle";
            _tabBlocked.SubText = _blockedList.Count > 0 ? _blockedList.Count.ToString(CultureInfo.InvariantCulture) : "";

            if (_tab == TabBlocked)
            {
                _blocked.SetRowCount(_blockedList.Count);
                _blocked.Invalidate();
            }
            else
            {
                bool send = _tab == TabSend;
                _rows.Clear();
                _hiddenWindows = 0;
                _unusual = 0;
                foreach (AppSnapshot a in _all)
                {
                    if ((send ? a.Sent : a.Received) == 0) continue;
                    if (a.Warning != null) _unusual++;
                    // Windows' own processes can be hidden - but never one that looks unusual
                    if (_settings.HideWindows && a.IsWindows && a.Warning == null)
                    {
                        _hiddenWindows++;
                        continue;
                    }
                    _rows.Add(a);
                }
                _rows.Sort(CompareApps);
                _apps.SortColumn = _appSort;
                _apps.SortDescending = _appSortDescending;
                _apps.SetRowCount(_rows.Count);

                int selected = -1;
                for (int i = 0; i < _rows.Count; i++)
                    if (_rows[i].Id == _selectedId) { selected = i; break; }
                _apps.SetSelected(selected, false);
                _selectedVisible = selected >= 0;
                if (_selectedVisible) _selectedName = _rows[selected].Name;
                RefreshDetails();
                _apps.Invalidate();
                _detailHeader.Invalidate();
            }
            _summary.Invalidate();
            _status.Invalidate();
        }

        void RefreshDetails()
        {
            if (!_selectedVisible)
            {
                _remotes.Clear();
                _details.SetRowCount(0);
                _details.EmptyText = _rows.Count == 0 ? "" : "Click a program in the list above to see which servers it talks to.";
                return;
            }
            bool send = _tab == TabSend;
            _model.SnapshotRemotes(_selectedId, _settings.IncludeLan, _remotes);
            _remotes.RemoveAll(delegate(RemoteSnapshot r) { return (send ? r.Sent : r.Received) == 0; });
            _remotes.Sort(CompareRemotes);
            _details.SortColumn = _remoteSort;
            _details.SortDescending = _remoteSortDescending;
            _details.EmptyText = "";
            _details.SetRowCount(_remotes.Count);

            int selected = -1;
            if (_hasSelectedRemote)
            {
                for (int i = 0; i < _remotes.Count; i++)
                    if (!_remotes[i].IsOverflow && _remotes[i].Ip.Equals(_selectedRemote)) { selected = i; break; }
            }
            _details.SetSelected(selected, false);

            if (_resolver.Enabled)
            {
                int limit = Math.Min(_remotes.Count, 200);
                for (int i = 0; i < limit; i++)
                {
                    RemoteSnapshot r = _remotes[i];
                    if (r.Host == null && !r.IsLan && !r.IsOverflow) _resolver.Request(r.Ip);
                }
            }
            _details.Invalidate();
        }

        int CompareApps(AppSnapshot a, AppSnapshot b)
        {
            bool send = _tab == TabSend;
            int c = 0;
            switch (_appSort)
            {
                case ColApp: c = string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase); break;
                case ColNow: c = (send ? a.SentRate : a.RecvRate).CompareTo(send ? b.SentRate : b.RecvRate); break;
                case ColTotal: c = (send ? a.Sent : a.Received).CompareTo(send ? b.Sent : b.Received); break;
                case ColHosts: c = (send ? a.SentHosts : a.RecvHosts).CompareTo(send ? b.SentHosts : b.RecvHosts); break;
                case ColLast: c = (send ? a.LastSent : a.LastRecv).CompareTo(send ? b.LastSent : b.LastRecv); break;
            }
            if (_appSortDescending) c = -c;
            if (c == 0 && _appSort != ColTotal) c = -(send ? a.Sent : a.Received).CompareTo(send ? b.Sent : b.Received);
            if (c == 0) c = string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
            if (c == 0) c = a.Id.CompareTo(b.Id);
            return c;
        }

        int CompareRemotes(RemoteSnapshot a, RemoteSnapshot b)
        {
            int c = 0;
            switch (_remoteSort)
            {
                case RColHost:
                    if (a.Host == null && b.Host != null) return 1;        // unnamed servers always last
                    if (a.Host != null && b.Host == null) return -1;
                    c = string.Compare(a.Host, b.Host, StringComparison.OrdinalIgnoreCase);
                    break;
                case RColAddress: c = string.Compare(a.Address, b.Address, StringComparison.Ordinal); break;
                case RColPort: c = string.Compare(a.PortText, b.PortText, StringComparison.Ordinal); break;
                case RColSent: c = a.Sent.CompareTo(b.Sent); break;
                case RColRecv: c = a.Received.CompareTo(b.Received); break;
                case RColLast: c = Math.Max(a.LastSent, a.LastRecv).CompareTo(Math.Max(b.LastSent, b.LastRecv)); break;
            }
            if (_remoteSortDescending) c = -c;
            if (c == 0) c = -(a.Sent + a.Received).CompareTo(b.Sent + b.Received);
            if (c == 0) c = string.Compare(a.Address, b.Address, StringComparison.Ordinal);
            return c;
        }

        void OnAppSelected()
        {
            int i = _apps.SelectedIndex;
            if (i < 0 || i >= _rows.Count) return;
            if (_rows[i].Id != _selectedId)
            {
                _selectedId = _rows[i].Id;
                _hasSelectedRemote = false;
                _details.ScrollToTop();
            }
            _selectedName = _rows[i].Name;
            _selectedVisible = true;
            RefreshDetails();
            _detailHeader.Invalidate();
        }

        void OnAppHeader(int column)
        {
            if (column == _appSort) _appSortDescending = !_appSortDescending;
            else
            {
                _appSort = column;
                _appSortDescending = column != ColApp;
            }
            RefreshView();
        }

        void OnRemoteHeader(int column)
        {
            if (column == _remoteSort) _remoteSortDescending = !_remoteSortDescending;
            else
            {
                _remoteSort = column;
                _remoteSortDescending = column >= RColSent;
            }
            RefreshDetails();
        }

        // ------------------------------------------------------------------ blocking

        void OnAppButton(int row)
        {
            if (row < 0 || row >= _rows.Count) return;
            AppSnapshot a = _rows[row];
            if (!a.CanBlock) return;
            ToggleBlock(a.Path, a.Name, a.Blocked);
        }

        void ToggleBlock(string path, string name, bool currentlyBlocked)
        {
            if (string.IsNullOrEmpty(path)) return;
            if (_options.Demo)
            {
                MessageDialog.Info(this, "NetWatch", "Not available in demo mode", "Blocking is switched off while NetWatch shows demo data.");
                return;
            }
            if (!currentlyBlocked)
            {
                string body = "NetWatch will add a rule to Windows Firewall that stops this program from sending or receiving anything over the network:\n\n" +
                              path + "\n\nNothing else on this PC is changed, and you can undo it at any time on the Blocked tab.";
                if (_firewallOn == false)
                    body += "\n\nNote: Windows Firewall is switched off on this PC, so the block only starts working once it is switched on again.";
                if (!MessageDialog.Confirm(this, "NetWatch", "Block " + name + "?", body, "Block", "Cancel", ButtonKind.Danger, Theme.Danger))
                    return;
            }

            string error = null;
            Cursor = Cursors.WaitCursor;
            try
            {
                if (currentlyBlocked) FirewallManager.Unblock(path);
                else FirewallManager.Block(path, name);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                Program.Log("firewall change failed: " + ex);
            }
            // Read the firewall again so the screen shows what is really in force.
            try { ApplyBlocked(FirewallManager.ListBlocked(), FirewallManager.IsFirewallOn()); }
            catch (Exception ex) { if (error == null) error = ex.Message; }
            Cursor = Cursors.Default;

            bool nowBlocked = _blockedByPath.ContainsKey(path);
            if (error != null || nowBlocked == currentlyBlocked)
            {
                MessageDialog.Info(this, "NetWatch", "That did not work",
                    "Windows Firewall did not accept the change, so nothing was " + (currentlyBlocked ? "unblocked" : "blocked") + "." +
                    (error != null ? "\n\n" + error : ""));
            }
            RefreshView();
        }

        void LoadBlockedInBackground()
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    List<BlockedApp> list = FirewallManager.ListBlocked();
                    bool? on = FirewallManager.IsFirewallOn();
                    if (_closing || !IsHandleCreated) return;
                    BeginInvoke((MethodInvoker)delegate { ApplyBlocked(list, on); RefreshView(); });
                }
                catch (Exception ex)
                {
                    Program.Log("reading firewall rules failed: " + ex.Message);
                }
            });
        }

        void ApplyBlocked(List<BlockedApp> list, bool? firewallOn)
        {
            _blockedList = list ?? new List<BlockedApp>();
            _blockedByPath.Clear();
            foreach (BlockedApp b in _blockedList) _blockedByPath[b.Path] = b;
            _firewallOn = firewallOn;
        }

        string FriendlyName(BlockedApp b)
        {
            foreach (AppSnapshot a in _cache.Values)
                if (a.Path != null && string.Equals(a.Path, b.Path, StringComparison.OrdinalIgnoreCase)) return a.Name;
            return b.Name;
        }

        // --------------------------------------------------------------------- menus

        void ResetCounters()
        {
            if (!MessageDialog.Confirm(this, "NetWatch", "Start counting again from zero?",
                    "All totals counted so far are cleared and NetWatch starts counting afresh. Blocked programs stay blocked.",
                    "Clear totals", "Cancel", ButtonKind.Primary, Theme.Down))
                return;
            _model.Reset();
            _cache.Clear();
            _selectedId = -1;
            _selectedVisible = false;
            _hasSelectedRemote = false;
            RefreshView();
        }

        void ShowAbout()
        {
            MessageDialog.Info(this, "About NetWatch", "NetWatch " + ProjectInfo.Version,
                "Shows which programs on this PC send and receive data, how much, and which servers they talk to.\n\n" +
                "NetWatch only counts the traffic that Windows itself reports. It never reads the contents of what is sent, " +
                "and none of what it shows leaves this PC.\n\n" +
                "Counting starts when NetWatch is opened and stops when it is closed.\n\n" +
                "Free and open source (MIT licence). Source code: " + ProjectInfo.Website);
        }

        void OnAppRightClick(int row, Point location)
        {
            if (row < 0 || row >= _rows.Count) return;
            AppSnapshot a = _rows[row];
            _rowMenu.Items.Clear();
            string path = a.Path, name = a.Name;
            bool blockedNow = a.Blocked;
            if (path != null)
            {
                ToolStripMenuItem folder = new ToolStripMenuItem("Show the program file in its folder");
                folder.Click += delegate { ShowInFolder(path); };
                ToolStripMenuItem copy = new ToolStripMenuItem("Copy the file path");
                copy.Click += delegate { CopyText(path); };
                _rowMenu.Items.Add(folder);
                _rowMenu.Items.Add(copy);
                if (a.CanBlock)
                {
                    _rowMenu.Items.Add(new ToolStripSeparator());
                    ToolStripMenuItem block = new ToolStripMenuItem(blockedNow ? "Unblock" : "Block\u2026");
                    block.Click += delegate { ToggleBlock(path, name, blockedNow); };
                    _rowMenu.Items.Add(block);
                }
            }
            else
            {
                ToolStripMenuItem none = new ToolStripMenuItem("No program file is known for this entry");
                none.Enabled = false;
                _rowMenu.Items.Add(none);
            }
            _rowMenu.Show(_apps, location);
        }

        void OnRemoteRightClick(int row, Point location)
        {
            if (row < 0 || row >= _remotes.Count) return;
            RemoteSnapshot r = _remotes[row];
            if (r.IsOverflow) return;
            _rowMenu.Items.Clear();
            string host = r.Host, address = r.Address;
            if (host != null)
            {
                ToolStripMenuItem copyName = new ToolStripMenuItem("Copy the server name");
                copyName.Click += delegate { CopyText(host); };
                _rowMenu.Items.Add(copyName);
            }
            ToolStripMenuItem copyAddress = new ToolStripMenuItem("Copy the address");
            copyAddress.Click += delegate { CopyText(address); };
            _rowMenu.Items.Add(copyAddress);
            _rowMenu.Show(_details, location);
        }

        static void CopyText(string text)
        {
            try { if (!string.IsNullOrEmpty(text)) Clipboard.SetText(text); }
            catch { }
        }

        static void ShowInFolder(string path)
        {
            try { Process.Start("explorer.exe", "/select,\"" + path + "\""); }
            catch { }
        }

        // ------------------------------------------------------------------ tooltips

        string AppToolTip(int row, int column)
        {
            if (row < 0 || row >= _rows.Count) return null;
            AppSnapshot a = _rows[row];
            switch (column)
            {
                case ColApp:
                {
                    string text = a.Path != null ? a.Name + "\n" + a.Path : a.Name + "\n" + a.Sub;
                    if (a.Warning != null)
                        text += "\n\nMarked as unusual because:\n" + a.Warning +
                                "\n\nThis is a hint, not proof that the program is harmful. If you do not recognise it, look its name up, or block it.";
                    return text;
                }
                case ColHosts:
                    return _tab == TabSend ? "How many different servers this program has sent data to." : "How many different servers this program has received data from.";
                case ColAction:
                    if (a.IsSelf) return "This is NetWatch.";
                    if (a.IsSystem) return "Windows needs this part to stay connected, so NetWatch does not offer to block it.";
                    if (a.Path == null) return "This program closed before NetWatch could see which file it was.";
                    return a.Blocked ? "Let this program use the network again." : "Stop this program from sending or receiving anything.";
            }
            return null;
        }

        string RemoteToolTip(int row, int column)
        {
            if (row < 0 || row >= _remotes.Count) return null;
            RemoteSnapshot r = _remotes[row];
            if (r.IsOverflow) return "This program talked to more servers than NetWatch lists one by one. The rest are added up here.";
            if (column == RColHost || column == RColAddress)
                return (r.Host ?? "Name unknown") + "\n" + r.Address + (r.IsLan ? "\nA device inside your own network" : "");
            return null;
        }

        // ------------------------------------------------------------------- drawing

        void PaintAppCell(Graphics g, int row, int column, Rectangle cell, GridCellState state)
        {
            if (row >= _rows.Count) return;
            AppSnapshot a = _rows[row];
            bool send = _tab == TabSend;
            Color accent = send ? Theme.Up : Theme.Down;
            Rectangle text = new Rectangle(cell.X, cell.Y, cell.Width - Theme.S(10), cell.Height);

            switch (column)
            {
                case ColApp:
                {
                    int iconSize = Theme.S(28);
                    Rectangle iconRect = new Rectangle(cell.X, cell.Y + (cell.Height - iconSize) / 2, iconSize, iconSize);
                    DrawIcon(g, a.Icon, iconRect, a.WindowsPart, a.Name);
                    int textX = iconRect.Right + Theme.S(12);
                    int textWidth = cell.Right - textX - Theme.S(12);
                    int nameWidth = textWidth;
                    bool unusual = a.Warning != null;
                    if (a.Blocked || unusual)
                    {
                        // little labels after the name: UNUSUAL (solid red) and/or BLOCKED (outlined red)
                        int unusualWidth = unusual ? Theme.Measure("UNUSUAL", Theme.Caps) + Theme.S(14) : 0;
                        int blockedWidth = a.Blocked ? Theme.Measure("BLOCKED", Theme.Caps) + Theme.S(14) : 0;
                        int pills = unusualWidth + blockedWidth + (unusual && a.Blocked ? Theme.S(6) : 0);
                        int measured = Theme.Measure(a.Name, Theme.BodyBold);
                        nameWidth = Math.Max(Theme.S(40), Math.Min(measured + 2, textWidth - pills - Theme.S(8)));
                        int pillX = textX + nameWidth + Theme.S(8);
                        if (unusual)
                        {
                            Rectangle pill = new Rectangle(pillX, cell.Y + Theme.S(10), unusualWidth, Theme.S(18));
                            Theme.FillRound(g, pill, Theme.S(9), Theme.Danger);
                            Theme.Center(g, "UNUSUAL", Theme.Caps, pill, Theme.OnAccent);
                            pillX += unusualWidth + Theme.S(6);
                        }
                        if (a.Blocked)
                        {
                            Rectangle pill = new Rectangle(pillX, cell.Y + Theme.S(10), blockedWidth, Theme.S(18));
                            Theme.FillRound(g, pill, Theme.S(9), Color.FromArgb(70, 30, 34));
                            Theme.Center(g, "BLOCKED", Theme.Caps, pill, Theme.Danger);
                        }
                    }
                    Theme.Left(g, a.Name, Theme.BodyBold, new Rectangle(textX, cell.Y + Theme.S(8), nameWidth, Theme.S(21)), a.Running ? Theme.Text : Theme.TextDim);
                    string sub = a.Running ? a.Sub : a.Sub + "  \u00B7  closed";
                    Theme.Left(g, sub, Theme.Small, new Rectangle(textX, cell.Y + Theme.S(28), textWidth, Theme.S(18)), Theme.TextFaint);
                    break;
                }
                case ColChart:
                {
                    Rectangle chart = new Rectangle(cell.X, cell.Y + Theme.S(13), cell.Width - Theme.S(26), cell.Height - Theme.S(26));
                    using (Pen baseline = new Pen(Theme.Line))
                        g.DrawLine(baseline, chart.X, chart.Bottom - 1, chart.Right - 1, chart.Bottom - 1);
                    float peak = send ? a.SentPeak : a.RecvPeak;
                    if (peak > 0)
                        Theme.AreaChart(g, chart, send ? a.SentHistory : a.RecvHistory, Math.Max(peak, 2048f), accent, Math.Max(1f, Theme.Scale * 1.1f));
                    break;
                }
                case ColNow:
                {
                    double rate = send ? a.SentRate : a.RecvRate;
                    Theme.Right(g, Theme.Rate(rate), Theme.Body, text, rate >= 1 ? accent : Theme.TextFaint);
                    break;
                }
                case ColTotal:
                    Theme.Right(g, Theme.Bytes(send ? a.Sent : a.Received), Theme.BodyBold, text, Theme.Text);
                    break;
                case ColHosts:
                    Theme.Right(g, (send ? a.SentHosts : a.RecvHosts).ToString(CultureInfo.InvariantCulture), Theme.Body, text, Theme.TextDim);
                    break;
                case ColLast:
                {
                    string ago = Theme.Ago(send ? a.LastSent : a.LastRecv, _nowFileTime);
                    Theme.Right(g, ago, Theme.Body, text, ago == "now" ? Theme.Text : Theme.TextDim);
                    break;
                }
                case ColAction:
                {
                    if (a.CanBlock)
                        DrawRowButton(g, _apps.ButtonRect(cell), a.Blocked ? "Unblock" : "Block", state, a.Blocked ? Theme.Good : Theme.Danger);
                    else if (a.IsSystem || a.IsSelf)
                        Theme.Right(g, a.IsSelf ? "NetWatch itself" : "Part of Windows", Theme.Small,
                            new Rectangle(cell.X, cell.Y, cell.Width - Theme.S(6), cell.Height), Theme.TextFaint);
                    break;
                }
            }
        }

        static void DrawRowButton(Graphics g, Rectangle r, string label, GridCellState state, Color hotColor)
        {
            int radius = Theme.S(6);
            if (state.ButtonHover)
            {
                Theme.FillRound(g, r, radius, state.ButtonDown ? FlatButton.Mix(hotColor, Color.Black, 0.18f) : hotColor);
                Theme.Center(g, label, Theme.BodyBold, r, Theme.OnAccent);
            }
            else
            {
                Theme.FillRound(g, r, radius, state.Selected ? Color.FromArgb(56, 63, 84) : Theme.Button);
                Theme.Center(g, label, Theme.Body, r, Theme.Text);
            }
        }

        static void DrawIcon(Graphics g, Image icon, Rectangle r, bool windowsPart, string name)
        {
            if (windowsPart)
            {
                // a part of Windows itself: four blue panes
                Theme.FillRound(g, r, Theme.S(6), Theme.Button);
                int gap = Math.Max(1, Theme.S(2));
                int pane = (r.Width - Theme.S(12) - gap) / 2;
                int left = r.X + (r.Width - (2 * pane + gap)) / 2, top = r.Y + (r.Height - (2 * pane + gap)) / 2;
                using (SolidBrush b = new SolidBrush(Theme.Down))
                {
                    g.FillRectangle(b, left, top, pane, pane);
                    g.FillRectangle(b, left + pane + gap, top, pane, pane);
                    g.FillRectangle(b, left, top + pane + gap, pane, pane);
                    g.FillRectangle(b, left + pane + gap, top + pane + gap, pane, pane);
                }
                return;
            }
            if (icon != null)
            {
                InterpolationMode old = g.InterpolationMode;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                try { g.DrawImage(icon, r); }
                catch { }
                g.InterpolationMode = old;
                return;
            }
            // no icon known: a coloured tile with the program's first letter
            Color[] palette = new Color[] {
                Color.FromArgb(74, 109, 167), Color.FromArgb(92, 132, 96), Color.FromArgb(150, 98, 72), Color.FromArgb(120, 92, 150),
                Color.FromArgb(76, 128, 136), Color.FromArgb(150, 110, 60), Color.FromArgb(140, 84, 110), Color.FromArgb(96, 104, 122) };
            int hash = 0;
            char letter = '?';
            if (!string.IsNullOrEmpty(name))
            {
                foreach (char c in name) hash = (hash * 31 + c) & 0x7FFFFFFF;
                foreach (char c in name)
                    if (char.IsLetterOrDigit(c)) { letter = char.ToUpperInvariant(c); break; }
            }
            Theme.FillRound(g, r, Theme.S(6), palette[hash % palette.Length]);
            Theme.Center(g, letter.ToString(), Theme.BodyBold, r, Color.FromArgb(240, 242, 247));
        }

        void PaintRemoteCell(Graphics g, int row, int column, Rectangle cell, GridCellState state)
        {
            if (row >= _remotes.Count) return;
            RemoteSnapshot r = _remotes[row];
            bool send = _tab == TabSend;
            Rectangle text = new Rectangle(cell.X, cell.Y, cell.Width - Theme.S(10), cell.Height);
            switch (column)
            {
                case RColHost:
                    if (r.Host != null) Theme.Left(g, r.Host, Theme.Body, text, Theme.Text);
                    else Theme.Left(g, r.IsLan ? "device in your own network" : "name unknown", Theme.Body, text, Theme.TextFaint);
                    break;
                case RColAddress:
                    Theme.Left(g, r.Address, Theme.Body, text, Theme.TextDim);
                    break;
                case RColPort:
                    Theme.Left(g, r.PortText, Theme.Small, text, Theme.TextDim);
                    break;
                case RColSent:
                    if (r.Sent > 0) Theme.Right(g, Theme.Bytes(r.Sent), Theme.Body, text, send ? Theme.Text : Theme.TextDim);
                    break;
                case RColRecv:
                    if (r.Received > 0) Theme.Right(g, Theme.Bytes(r.Received), Theme.Body, text, send ? Theme.TextDim : Theme.Text);
                    break;
                case RColLast:
                    Theme.Right(g, Theme.Ago(Math.Max(r.LastSent, r.LastRecv), _nowFileTime), Theme.Body, text, Theme.TextDim);
                    break;
            }
        }

        void PaintBlockedCell(Graphics g, int row, int column, Rectangle cell, GridCellState state)
        {
            if (row >= _blockedList.Count) return;
            BlockedApp b = _blockedList[row];
            if (column == 0)
            {
                int iconSize = Theme.S(28);
                Rectangle iconRect = new Rectangle(cell.X, cell.Y + (cell.Height - iconSize) / 2, iconSize, iconSize);
                DrawIcon(g, IconCache.TryGet(b.Path), iconRect, false, FriendlyName(b));
                int textX = iconRect.Right + Theme.S(12);
                int textWidth = cell.Right - textX - Theme.S(12);
                Theme.Left(g, FriendlyName(b), Theme.BodyBold, new Rectangle(textX, cell.Y + Theme.S(8), textWidth, Theme.S(21)), Theme.Text);
                Theme.Left(g, b.Path, Theme.Small, new Rectangle(textX, cell.Y + Theme.S(28), textWidth, Theme.S(18)), Theme.TextFaint);
            }
            else
            {
                DrawRowButton(g, _blocked.ButtonRect(cell), "Unblock", state, Theme.Good);
            }
        }

        int PaintStat(Graphics g, int x, int top, string label, string value, Color valueColor, int width)
        {
            Theme.Left(g, label, Theme.Caps, new Rectangle(x, top, width - Theme.S(12), Theme.S(16)), Theme.TextFaint);
            Theme.Left(g, value, Theme.Big, new Rectangle(x, top + Theme.S(19), width - Theme.S(12), Theme.S(40)), valueColor);
            return x + width;
        }

        void PaintSummary(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Rectangle r = _summary.ClientRectangle;
            g.Clear(Theme.Window);
            int x = Theme.S(24), top = Theme.S(18);

            if (_tab == TabBlocked)
            {
                int count = _blockedList.Count;
                x = PaintStat(g, x, top, "BLOCKED PROGRAMS", count.ToString(CultureInfo.InvariantCulture), count > 0 ? Theme.Danger : Theme.Text, Theme.S(210));
                string info = "A blocked program cannot send or receive anything over the network until you unblock it. " +
                              "NetWatch does this with ordinary Windows Firewall rules, which keep working when NetWatch is closed.";
                Theme.Wrapped(g, info, Theme.Body, new Rectangle(x, top + Theme.S(4), Math.Min(r.Width - x - Theme.S(24), Theme.S(760)), r.Height - top), Theme.TextDim);
                return;
            }

            bool send = _tab == TabSend;
            Color accent = send ? Theme.Up : Theme.Down;
            string since = _model.StartedUtc.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
            double rate = send ? _totals.SentRate : _totals.RecvRate;
            x = PaintStat(g, x, top, (send ? "SENT SINCE " : "RECEIVED SINCE ") + since, Theme.Bytes(send ? _totals.Sent : _totals.Received), Theme.Text, Theme.S(196));
            x = PaintStat(g, x, top, send ? "SENDING NOW" : "RECEIVING NOW", rate >= 1 ? Theme.Rate(rate) : "idle", rate >= 1 ? accent : Theme.TextFaint, Theme.S(186));
            x = PaintStat(g, x, top, send ? "PROGRAMS THAT SENT" : "PROGRAMS THAT RECEIVED",
                (send ? _totals.SendingApps : _totals.ReceivingApps).ToString(CultureInfo.InvariantCulture), Theme.Text, Theme.S(196));

            Rectangle chart = new Rectangle(x, top + Theme.S(18), r.Width - x - Theme.S(26), r.Height - top - Theme.S(30));
            if (chart.Width < Theme.S(180)) return;
            float peak = send ? _totals.SentPeak : _totals.RecvPeak;
            Theme.Left(g, "ALL PROGRAMS, LAST 60 SECONDS", Theme.Caps, new Rectangle(chart.X, top, chart.Width / 2 + Theme.S(60), Theme.S(16)), Theme.TextFaint);
            if (peak > 0)
                Theme.Right(g, "highest " + Theme.Rate(peak), Theme.Small, new Rectangle(chart.X + chart.Width / 2, top - 1, chart.Width / 2, Theme.S(16)), Theme.TextFaint);
            using (Pen baseline = new Pen(Theme.Line))
                g.DrawLine(baseline, chart.X, chart.Bottom - 1, chart.Right - 1, chart.Bottom - 1);
            if (peak > 0)
                Theme.AreaChart(g, chart, send ? _totals.SentHistory : _totals.RecvHistory, Math.Max(peak, 2048f), accent, Math.Max(1.2f, Theme.Scale * 1.4f));
        }

        void PaintDetailHeader(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Rectangle r = _detailHeader.ClientRectangle;
            g.Clear(Theme.Panel);
            using (Pen line = new Pen(Theme.Line))
                g.DrawLine(line, 0, 0, r.Width, 0);
            // little grip that hints the divider can be dragged
            int gripWidth = Theme.S(40);
            Theme.FillRound(g, new Rectangle((r.Width - gripWidth) / 2, Theme.S(5), gripWidth, Theme.S(3)), Theme.S(2), _draggingSplit ? Theme.TextFaint : Theme.Line);

            Rectangle text = new Rectangle(Theme.S(18), Theme.S(8), r.Width - Theme.S(36), r.Height - Theme.S(8));
            if (!_selectedVisible)
            {
                Theme.Left(g, "Servers", Theme.Title, text, Theme.TextFaint);
                return;
            }
            bool send = _tab == TabSend;
            string title = send ? "Where " + _selectedName + " sends data" : "Where " + _selectedName + " receives data from";
            int count = _remotes.Count;
            string suffix = count == 1 ? "1 server" : count.ToString(CultureInfo.InvariantCulture) + " servers";
            int suffixWidth = Theme.Measure(suffix, Theme.Body) + Theme.S(4);
            int titleWidth = Math.Min(Theme.Measure(title, Theme.Title) + 2, text.Width - suffixWidth - Theme.S(14));
            Theme.Left(g, title, Theme.Title, new Rectangle(text.X, text.Y, titleWidth, text.Height), Theme.Text);
            Theme.Left(g, suffix, Theme.Body, new Rectangle(text.X + titleWidth + Theme.S(12), text.Y + Theme.S(1), suffixWidth, text.Height), Theme.TextFaint);
        }

        void PaintNotice(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Rectangle r = _notice.ClientRectangle;
            g.Clear(FlatButton.Mix(Theme.Window, _noticeColor, 0.13f));
            int right = _retryShown ? _btnRetry.Left - Theme.S(14) : r.Width - Theme.S(18);
            Theme.Left(g, _noticeText, Theme.Body, new Rectangle(Theme.S(24), 0, right - Theme.S(24), r.Height), _noticeColor);
        }

        void PaintStatus(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Rectangle r = _status.ClientRectangle;
            g.Clear(Theme.Window);
            using (Pen line = new Pen(Theme.Line))
                g.DrawLine(line, 0, 0, r.Width, 0);

            bool demoLabel = _options.Demo && _options.ShowcaseDir == null;
            bool running = _options.Demo || (_etw != null && _etw.IsRunning && _etwError == 0);
            Color dot = demoLabel ? Theme.Up : (running ? Theme.Good : Theme.Danger);
            int dotSize = Theme.S(8);
            SmoothingMode old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (SolidBrush b = new SolidBrush(dot))
                g.FillEllipse(b, Theme.S(20), (r.Height - dotSize) / 2 + 1, dotSize, dotSize);
            g.SmoothingMode = old;

            string left;
            if (demoLabel) left = "Demo data";
            else if (!running) left = "Not watching";
            else
            {
                left = "Watching every program on this PC  \u00B7  for " + Theme.Duration(DateTime.UtcNow - _model.StartedUtc) +
                       "  \u00B7  " + (_settings.IncludeLan ? "internet and home network traffic" : "internet traffic only");
                if (_lostEvents > 0)
                    left += "  \u00B7  the PC was too busy to count everything (" + _lostEvents.ToString("N0", CultureInfo.InvariantCulture) + " events missed)";
            }
            if (_tab != TabBlocked && _settings.HideWindows)
                left += "  \u00B7  " + _hiddenWindows.ToString(CultureInfo.InvariantCulture) + (_hiddenWindows == 1 ? " Windows process hidden" : " Windows processes hidden");
            string right = "";
            Color rightColor = Theme.TextFaint;
            if (_tab != TabBlocked && _unusual > 0)
            {
                right = _unusual == 1 ? "1 unusual program, marked in red" : _unusual.ToString(CultureInfo.InvariantCulture) + " unusual programs, marked in red";
                rightColor = Theme.Danger;
            }
            else if (!demoLabel)
            {
                if (_firewallOn == true || _options.ShowcaseDir != null) right = "Windows Firewall is on";
                else if (_firewallOn == false)
                {
                    right = "Windows Firewall is off, so blocking has no effect";
                    rightColor = Theme.Danger;
                }
            }
            int rightWidth = right.Length > 0 ? Theme.Measure(right, Theme.Small) + Theme.S(6) : 0;
            Theme.Left(g, left, Theme.Small, new Rectangle(Theme.S(36), 1, r.Width - Theme.S(60) - rightWidth, r.Height), Theme.TextDim);
            if (rightWidth > 0)
                Theme.Right(g, right, Theme.Small, new Rectangle(r.Width - Theme.S(20) - rightWidth, 1, rightWidth, r.Height), rightColor);
        }

        // -------------------------------------------------------- tests & diagnostics

        Dictionary<string, long[]> _adapterBase;

        static Dictionary<string, long[]> ReadAdapters()
        {
            Dictionary<string, long[]> result = new Dictionary<string, long[]>();
            try
            {
                foreach (System.Net.NetworkInformation.NetworkInterface nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                    System.Net.NetworkInformation.IPInterfaceStatistics statistics = nic.GetIPStatistics();
                    result[nic.Name] = new long[] { statistics.BytesSent, statistics.BytesReceived };
                }
            }
            catch
            {
            }
            return result;
        }

        void WriteDiagnostics()
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("NetWatch diagnostics  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
                sb.AppendLine("running as administrator: " + ProcessUtil.IsElevated());
                sb.AppendLine("capture start result: " + _etwError);
                if (_etw != null)
                {
                    sb.AppendLine("providers (0 = ok): network " + _etw.NetworkProviderError + ", process " + _etw.ProcessProviderError + ", dns " + _etw.DnsProviderError);
                    sb.AppendLine("events received: " + _etw.EventCount + "   lost: " + _etw.QueryLostEvents());
                }
                sb.AppendLine("firewall on: " + (_firewallOn.HasValue ? _firewallOn.Value.ToString() : "unknown") + "   blocked programs: " + _blockedList.Count);
                if (_adapterBase != null)
                {
                    // Windows' own per-adapter byte counters over the same period, as an independent cross-check
                    // (they include packet headers, so they read a little higher than NetWatch's numbers).
                    sb.AppendLine("network adapters since start:");
                    foreach (KeyValuePair<string, long[]> now in ReadAdapters())
                    {
                        long[] start;
                        if (!_adapterBase.TryGetValue(now.Key, out start)) continue;
                        long sent = now.Value[0] - start[0], received = now.Value[1] - start[1];
                        if (sent != 0 || received != 0)
                            sb.AppendLine("   " + now.Key + ": sent " + Theme.Bytes(sent) + ", received " + Theme.Bytes(received));
                    }
                }
                sb.AppendLine();
                sb.Append(_model.DumpText());
                File.WriteAllText(_options.DiagFile, sb.ToString(), Encoding.UTF8);
                CaptureWindow(this, Path.ChangeExtension(_options.DiagFile, ".png"));
            }
            catch (Exception ex)
            {
                Program.Log("diagnostics failed: " + ex);
            }
        }

        void RunSelfTestStep()
        {
            _testTimer.Interval = 900;
            string dir = _options.SelfTestDir;
            try
            {
                switch (_testStep++)
                {
                    case 0:
                        CaptureWindow(this, Path.Combine(dir, "1-sending.png"));
                        break;
                    case 1:
                        if (_rows.Count > 1)
                        {
                            _selectedId = _rows[1].Id;
                            RefreshView();
                        }
                        break;
                    case 2:
                        CaptureWindow(this, Path.Combine(dir, "2-sending-selected.png"));
                        break;
                    case 3:
                        SetTab(TabRecv);
                        break;
                    case 4:
                        CaptureWindow(this, Path.Combine(dir, "3-receiving.png"));
                        break;
                    case 5:
                        SetTab(TabBlocked);
                        break;
                    case 6:
                        CaptureWindow(this, Path.Combine(dir, "4-blocked.png"));
                        break;
                    case 7:
                        SetTab(TabSend);
                        _testDialog = MessageDialog.CreateForTest("NetWatch", "Block Example Program?",
                            "NetWatch will add a rule to Windows Firewall that stops this program from sending or receiving anything over the network:\n\n" +
                            "C:\\Program Files\\Example\\example.exe\n\nNothing else on this PC is changed, and you can undo it at any time on the Blocked tab.",
                            "Block", "Cancel", ButtonKind.Danger);
                        _testDialog.StartPosition = FormStartPosition.Manual;
                        _testDialog.Location = new Point(Left + Theme.S(200), Top + Theme.S(200));
                        _testDialog.Show(this);
                        break;
                    case 8:
                        CaptureWindow(_testDialog, Path.Combine(dir, "5-confirm.png"));
                        _testDialog.Close();
                        _testDialog = null;
                        break;
                    case 9:
                        Size = MinimumSize;
                        break;
                    case 10:
                        CaptureWindow(this, Path.Combine(dir, "6-smallest.png"));
                        break;
                    case 11:
                        _menu.Show(_btnOptions, new Point(0, _btnOptions.Height + Theme.S(4)));
                        break;
                    case 12:
                        CaptureWindow(_menu, Path.Combine(dir, "7-menu.png"));
                        _menu.Close();
                        break;
                    case 13:
                        // back to the normal size; sort by name; include the home network; pick "Windows system"
                        Size = new Size(Theme.S(1240), Theme.S(820));
                        OnAppHeader(ColApp);
                        _miLan.PerformClick();
                        foreach (AppSnapshot a in _all)
                            if (a.Name == "Windows system") _selectedId = a.Id;
                        RefreshView();
                        if (_apps.SelectedIndex >= 0) _apps.SetSelected(_apps.SelectedIndex, true);
                        break;
                    case 14:
                        CaptureWindow(this, Path.Combine(dir, "8-by-name-with-home-network.png"));
                        break;
                    case 15:
                        OnAppHeader(ColTotal);
                        _apps.ScrollToTop();
                        RefreshView();
                        OnAppRightClick(0, new Point(Theme.S(300), Theme.S(60)));
                        break;
                    case 16:
                        CaptureWindow(_rowMenu, Path.Combine(dir, "9-row-menu.png"));
                        _rowMenu.Close();
                        break;
                    case 17:
                        // pretend the mouse pointer rests on the first row's Block button
                        _apps.TestSetHover(0, ColAction, true);
                        break;
                    case 18:
                        CaptureWindow(this, Path.Combine(dir, "10-hover-block.png"));
                        _apps.TestSetHover(-1, -1, false);
                        break;
                    case 19:
                    {
                        // press and release it: in demo mode this must only show the "not available" message
                        Point p = TestButtonPoint();
                        NativeMethods.PostMessage(_apps.Handle, 0x0201 /* WM_LBUTTONDOWN */, (IntPtr)1, TestLParam(p));
                        NativeMethods.PostMessage(_apps.Handle, 0x0202 /* WM_LBUTTONUP */, IntPtr.Zero, TestLParam(p));
                        break;
                    }
                    case 20:
                    {
                        Form dialog = null;
                        foreach (Form f in Application.OpenForms)
                            if (f is MessageDialog) dialog = f;
                        File.AppendAllText(Path.Combine(dir, "clicks.txt"), "dialog after Block click: " + (dialog != null) + Environment.NewLine);
                        if (dialog != null)
                        {
                            CaptureWindow(dialog, Path.Combine(dir, "11-click-result.png"));
                            dialog.Close();
                        }
                        break;
                    }
                    case 21:
                    {
                        // a header click through the real mouse path: sort by "servers"
                        Rectangle cell = _apps.GetCellBounds(0, ColHosts);
                        Point p = new Point(cell.X + cell.Width / 2, Theme.S(14));
                        NativeMethods.PostMessage(_apps.Handle, 0x0201, (IntPtr)1, TestLParam(p));
                        NativeMethods.PostMessage(_apps.Handle, 0x0202, IntPtr.Zero, TestLParam(p));
                        break;
                    }
                    case 22:
                        File.AppendAllText(Path.Combine(dir, "clicks.txt"), "sort column after header click: " + _appSort + " (expected " + ColHosts + ")" + Environment.NewLine);
                        // and a row click: select the third row
                        if (_rows.Count > 2)
                        {
                            Rectangle cell = _apps.GetCellBounds(2, ColApp);
                            Point p = new Point(cell.X + Theme.S(60), cell.Y + cell.Height / 2);
                            NativeMethods.PostMessage(_apps.Handle, 0x0201, (IntPtr)1, TestLParam(p));
                            NativeMethods.PostMessage(_apps.Handle, 0x0202, IntPtr.Zero, TestLParam(p));
                        }
                        break;
                    case 23:
                        File.AppendAllText(Path.Combine(dir, "clicks.txt"), "selected row after click: " + _apps.SelectedIndex + " (expected 2), details rows: " + _remotes.Count + Environment.NewLine);
                        CaptureWindow(this, Path.Combine(dir, "12-clicked-row.png"));
                        break;
                    case 24:
                        // mark two programs as unusual (one of them a Windows process) and look at the red highlight
                        OnAppHeader(ColTotal);
                        if (!_appSortDescending) OnAppHeader(ColTotal);
                        _selectedId = -1;
                        RefreshView();
                        _apps.ScrollToTop();
                        if (_rows.Count > 3)
                        {
                            _model.MarkUnusualForTests(_rows[1].Id, "It is not digitally signed, so nobody vouches for who made it.\nIt runs from a temporary or download folder.");
                            foreach (AppSnapshot a in _rows)
                                if (a.IsWindows) { _model.MarkUnusualForTests(a.Id, "It uses the name of a Windows program (svchost.exe) but is not in the Windows folder."); break; }
                        }
                        RefreshView();
                        _apps.TestSetHeaderHover(ColNow);
                        break;
                    case 25:
                        CaptureWindow(this, Path.Combine(dir, "13-unusual.png"));
                        File.AppendAllText(Path.Combine(dir, "clicks.txt"), "unusual programs: " + _unusual + " (expected 2), rows: " + _rows.Count + Environment.NewLine);
                        _apps.TestSetHeaderHover(-1);
                        break;
                    case 26:
                        _btnWindows.PerformClickForTests();
                        break;
                    case 27:
                    {
                        int windowsRows = 0, unusualWindows = 0;
                        foreach (AppSnapshot a in _rows)
                            if (a.IsWindows) { windowsRows++; if (a.Warning != null) unusualWindows++; }
                        File.AppendAllText(Path.Combine(dir, "clicks.txt"), "after hiding Windows processes: rows " + _rows.Count + ", hidden " + _hiddenWindows +
                            ", Windows rows still shown " + windowsRows + " (expected 1: the unusual one, got unusual " + unusualWindows + "), button says '" + _btnWindows.Text + "'" + Environment.NewLine);
                        CaptureWindow(this, Path.Combine(dir, "14-windows-hidden.png"));
                        break;
                    }
                    case 28:
                        _btnWindows.PerformClickForTests();
                        break;
                    case 29:
                        File.AppendAllText(Path.Combine(dir, "clicks.txt"), "after showing them again: rows " + _rows.Count + ", hidden " + _hiddenWindows + Environment.NewLine);
                        _model.Reset();
                        _cache.Clear();
                        _selectedId = -1;
                        RefreshView();
                        File.AppendAllText(Path.Combine(dir, "clicks.txt"), "rows right after reset: " + _rows.Count + Environment.NewLine);
                        break;
                    default:
                        _testTimer.Stop();
                        File.WriteAllText(Path.Combine(dir, "done.txt"), "rows=" + _rows.Count + " all=" + _all.Count);
                        Close();
                        break;
                }
            }
            catch (Exception ex)
            {
                File.AppendAllText(Path.Combine(dir, "errors.txt"), "step " + _testStep + ": " + ex + Environment.NewLine);
            }
        }

        // Makes the example pictures for the project page (invented programs only).
        void RunShowcaseStep()
        {
            _testTimer.Interval = 1100;
            string dir = _options.ShowcaseDir;
            try
            {
                switch (_testStep++)
                {
                    case 0:
                        if (_rows.Count > 1)
                        {
                            _selectedId = _rows[1].Id;
                            RefreshView();
                        }
                        break;
                    case 1:
                        CaptureWindow(this, Path.Combine(dir, "netwatch-sending.png"));
                        break;
                    case 2:
                        SetTab(TabRecv);
                        break;
                    case 3:
                        CaptureWindow(this, Path.Combine(dir, "netwatch-receiving.png"));
                        break;
                    case 4:
                        _testDialog = new DonateDialog();
                        _testDialog.StartPosition = FormStartPosition.Manual;
                        _testDialog.Location = new Point(Left + Theme.S(200), Top + Theme.S(200));
                        _testDialog.Show(this);
                        break;
                    case 5:
                        CaptureWindow(_testDialog, Path.Combine(dir, "donate.png"));
                        _testDialog.Close();
                        _testDialog = null;
                        break;
                    default:
                        _testTimer.Stop();
                        Close();
                        break;
                }
            }
            catch (Exception ex)
            {
                File.AppendAllText(Path.Combine(dir, "errors.txt"), "step " + _testStep + ": " + ex + Environment.NewLine);
            }
        }

        static void CaptureWindow(Control window, string file)
        {
            Theme.SaveWindowPicture(window, file);
        }

        Point TestButtonPoint()
        {
            Rectangle button = _apps.ButtonRect(_apps.GetCellBounds(0, ColAction));
            return new Point(button.X + button.Width / 2, button.Y + button.Height / 2);
        }

        static IntPtr TestLParam(Point p)
        {
            return (IntPtr)((p.Y << 16) | (p.X & 0xFFFF));
        }
    }
}
