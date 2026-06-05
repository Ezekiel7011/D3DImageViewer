using AoiBatchExportApi;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CSharpViewer;

public sealed class MainForm : Form
{
    private readonly Panel _viewport = new() { Dock = DockStyle.Fill, BackColor = Color.Black, AllowDrop = true };
    private readonly System.Windows.Forms.Timer _renderTimer = new() { Interval = 16 };
    private IntPtr _viewer = IntPtr.Zero;

    private Label _mouse = new();
    private Label _status = new();
    private Label _roi = new();
    private Label _meta = new();
    private TableLayoutPanel _metaTable = new();
    private CheckedListBox _overlays = new();
    private CheckedListBox _aoiObjects = new();
    private ComboBox _proc = new();
    private CheckBox _crosshair = new();
    private CheckBox _gray = new();
    private CheckBox _debug = new();
    private NumericUpDown _threshold = new();
    private NumericUpDown _gamma = new();
    private NumericUpDown _radius = new();
    private NumericUpDown _geoX = new();
    private NumericUpDown _geoY = new();
    private NumericUpDown _geoW = new();
    private NumericUpDown _geoH = new();
    private NumericUpDown _geoA = new();

    private ToolMode _tool = ToolMode.ZoomRect;
    private DisplayMode _display = DisplayMode.Base;
    private bool _refreshingList;
    private bool _loading;
    private NVImageInfo _lastInfo;
    private readonly Dictionary<string, string> _metadata = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<AoiDefectRec> _aoiDefects = new();
    private AoiRect? _diePos;
    private List<string> _folderImages = new();
    private int _folderImageIndex = -1;
    private int _selectedOverlayId = -1;
    private bool _syncingGeometry;
    private bool _allowListCheckChange;
    private readonly Dictionary<ToolMode, Button> _toolButtons = new();

    // Dirty render mode: static images should not redraw at full speed when idle.
    // Render is requested by UI/input changes, and continuous rendering is enabled briefly
    // during interactions so pan/zoom/tile updates stay smooth.
    private bool _renderDirty = true;
    private long _continuousRenderUntilMs;
    private long _nextDebugRenderMs;


    private static readonly Color Dark = Color.FromArgb(24, 28, 31);
    private static readonly Color Dark2 = Color.FromArgb(32, 36, 39);
    private static readonly Color Accent = Color.FromArgb(112, 190, 245);

    public MainForm()
    {
        Text = "AOI D3D11 Native Hybrid Image Viewer";
        Width = 1500;
        Height = 900;
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;
        BackColor = Color.Black;
        BuildUi();
        _renderTimer.Tick += (_, _) => TickRender();
        Shown += (_, _) => EnsureNative();
    }

    private void BuildUi()
    {
        Controls.Add(_viewport);
        Controls.Add(MakeTopBar());
        Controls.Add(MakeRightTabs());

        _viewport.MouseDown += (_, e) => { EnsureNative(); NativeMethods.NV_MouseDown(_viewer, MouseButton(e), e.X, e.Y, 0); SyncSelectedOverlayFromNative(false); RefreshOverlayList(); RequestRender(600); };
        _viewport.MouseMove += (_, e) => { if (_viewer == IntPtr.Zero) return; NativeMethods.NV_MouseMove(_viewer, e.X, e.Y, 0); UpdateMouseInfo(e.X, e.Y); if (e.Button != MouseButtons.None) { SyncSelectedOverlayFromNative(false); RequestRender(300); } else if (_debug.Checked) RequestRender(); };
        _viewport.MouseUp += (_, e) => { if (_viewer == IntPtr.Zero) return; NativeMethods.NV_MouseUp(_viewer, MouseButton(e), e.X, e.Y, 0); SyncSelectedOverlayFromNative(true); RefreshOverlayList(); UpdateRoiAnalysis(); RequestRender(400); };
        _viewport.MouseWheel += (_, e) => { if (_viewer == IntPtr.Zero) return; NativeMethods.NV_MouseWheel(_viewer, e.Delta, e.X, e.Y); RequestRender(900); };
        _viewport.Resize += (_, _) => { if (_viewer != IntPtr.Zero) { NativeMethods.NV_Resize(_viewer, _viewport.Width, _viewport.Height); RequestRender(400); } };
        _viewport.DragEnter += (_, e) => { if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true) e.Effect = DragDropEffects.Copy; };
        _viewport.DragDrop += async (_, e) => { var files = (string[])e.Data!.GetData(DataFormats.FileDrop)!; if (files.Length > 0) await LoadImageAsync(files[0]); };
        KeyDown += MainForm_KeyDown;
    }

    private Control MakeTopBar()
    {
        // Two-row topbar: the right docked tab panel reduces the visible width, so do not let
        // the live coordinate label push the option checkboxes underneath the right panel.
        var p = new Panel { Dock = DockStyle.Top, Height = 78, BackColor = Color.FromArgb(36, 36, 36) };
        int x = 12;
        Button B(string text, int w, EventHandler ev)
        {
            var b = FlatButton(text, w, 32);
            b.Left = x; b.Top = 12; b.Click += ev; x += w + 10; p.Controls.Add(b); return b;
        }
        B("Load", 74, async (_, _) => await OpenImageDialogAsync());
        B("Prev", 62, async (_, _) => await LoadSiblingImageAsync(-1));
        B("Next", 62, async (_, _) => await LoadSiblingImageAsync(1));
        B("Fit", 58, (_, _) => { EnsureNative(); NativeMethods.NV_FitToWindow(_viewer); });
        B("1:1", 58, (_, _) => { EnsureNative(); NativeMethods.NV_OneToOne(_viewer); });
        B("Image Info", 96, (_, _) => MessageBox.Show(ImageInfoText(), "Image Info"));
        B("Export View", 96, (_, _) => ExportCurrentViewport());
        B("Batch Export", 106, async (_, _) => await BatchExportFolderAsync());

        p.Controls.Add(_mouse);
        _mouse.SetBounds(x + 12, 18, 380, 24);
        _mouse.ForeColor = Color.White;
        _mouse.BackColor = Color.Transparent;
        _mouse.Font = new Font("Consolas", 10, FontStyle.Bold);
        _mouse.AutoEllipsis = false;

        // Put option switches on the second row so Crosshair / Gray Value / Debug Overlay
        // stay visible even after adding Prev / Next and the coordinate readout.
        int checkX = 492;
        _crosshair = TopCheck("Crosshair", false, ref checkX);
        _gray = TopCheck("Gray Value", true, ref checkX);
        _debug = TopCheck("Debug Overlay", false, ref checkX);

        p.Resize += (_, _) =>
        {
            // Visible right edge is before the right tab panel. Keep coordinate text inside it.
            int visibleRight = Math.Max(720, p.Width - 370);
            _mouse.Width = Math.Max(260, Math.Min(420, visibleRight - _mouse.Left - 12));
        };
        return p;

        CheckBox TopCheck(string text, bool chk, ref int cx)
        {
            var c = new CheckBox
            {
                Text = text,
                Checked = chk,
                Left = cx,
                Top = 50,
                Height = 22,
                Width = text.Length * 8 + 34,
                ForeColor = Color.White,
                BackColor = Color.Transparent
            };
            c.CheckedChanged += (_, _) => { PushOptions(); RequestRender(300); };
            cx += c.Width + 16;
            p.Controls.Add(c);
            return c;
        }
    }

    private TabControl MakeRightTabs()
    {
        var tabs = new TabControl { Dock = DockStyle.Right, Width = 355, Font = new Font("Segoe UI", 9), BackColor = Dark, ForeColor = Color.White };
        tabs.TabPages.Add(MakeToolsTab());
        tabs.TabPages.Add(MakeProcessingTab());
        tabs.TabPages.Add(MakeDefectsTab());
        return tabs;
    }

    private TabPage Page(string title)
    {
        return new TabPage(title) { BackColor = Dark, ForeColor = Color.White, Padding = new Padding(10) };
    }

    private TabPage MakeToolsTab()
    {
        var page = Page("Tools");
        var root = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, BackColor = Dark };
        page.Controls.Add(root);

        root.Controls.Add(Group("Navigate / Edit", Row(
            ToolButton("Zoom ROI", ToolMode.ZoomRect, true),
            ToolButton("Edit", ToolMode.Select, false))));

        root.Controls.Add(Group("Create Object", Row(
            ToolButton("Rect", ToolMode.Roi, false),
            ToolButton("Line", ToolMode.Measure, false),
            ToolButton("Circle", ToolMode.Circle, false))));

        _overlays.Width = 320; _overlays.Height = 150;
        var listGroup = Group("Overlay Items", _overlays); _overlays.CheckOnClick = false; _overlays.BackColor = Color.White; _overlays.ForeColor = Color.Black;
        _overlays.MouseDown += (_, e) => HandleOverlayListMouseDown(_overlays, e);
        _overlays.ItemCheck += (_, e) => HandleOverlayItemCheck(_overlays, e);
        _overlays.SelectedIndexChanged += (_, _) => SelectOverlayFromList(_overlays);
        root.Controls.Add(listGroup);
        root.Controls.Add(Row(ActionButton("Delete", 94, DeleteSelected), ActionButton("Clear All", 94, ClearAll)));

        var geom = new TableLayoutPanel { Width = 320, Height = 180, ColumnCount = 2, RowCount = 6, BackColor = Dark };
        geom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); geom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        AddNum(geom, "X", _geoX, 0, 0); AddNum(geom, "Y", _geoY, 1, 0); AddNum(geom, "W/Len", _geoW, 0, 2); AddNum(geom, "H", _geoH, 1, 2); AddNum(geom, "Angle", _geoA, 0, 4);
        var apply = ActionButton("Apply", 130, ApplyGeometry); geom.Controls.Add(apply, 1, 5);
        root.Controls.Add(Group("Geometry", geom));

        _roi = new Label { Width = 320, Height = 125, Font = new Font("Consolas", 9), ForeColor = Color.White, BackColor = Dark, Text = "ROI: --\r\nSample: --\r\nMean: --   Std: --\r\nMin/Max: -- / --\r\nPixels: --" };
        root.Controls.Add(Group("Selected ROI", _roi));
        return page;
    }

    private TabPage MakeProcessingTab()
    {
        var page = Page("Processing");
        var root = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, BackColor = Dark };
        page.Controls.Add(root);

        _proc = new ComboBox { Width = 292, Height = 28, DropDownStyle = ComboBoxStyle.DropDownList, BackColor = Color.White, ForeColor = Color.Black };
        _proc.DataSource = Enum.GetValues(typeof(ProcOp));
        _proc.SelectedIndexChanged += (_, _) => { PushProcessing(); RequestRender(300); };
        root.Controls.Add(Group("Current", Stack(Label("Operator"), _proc, Row(ProcDisplay("Base", DisplayMode.Base), ProcDisplay("Result", DisplayMode.Processed), ProcDisplay("Blend", DisplayMode.Blend)))));

        _threshold = MakeNum(0, 255, 128, 0); _gamma = MakeNum(1, 500, 70, 2); _radius = MakeNum(1, 128, 12, 0);
        _threshold.ValueChanged += (_, _) => { PushProcessing(); UpdateRoiAnalysis(); RequestRender(300); };
        _gamma.ValueChanged += (_, _) => { PushProcessing(); RequestRender(300); };
        _radius.ValueChanged += (_, _) => { PushProcessing(); RequestRender(300); };
        root.Controls.Add(Group("Parameters", Row(Stack(Label("Threshold"), _threshold), Stack(Label("Gamma"), _gamma), Stack(Label("Adaptive R"), _radius))));

        root.Controls.Add(Group("Enhance", Row(ProcButton("Original", ProcOp.None), ProcButton("Stretch", ProcOp.LinearStretch), ProcButton("Gamma", ProcOp.Gamma), ProcButton("Invert", ProcOp.Invert))));
        root.Controls.Add(Group("Threshold", Row(ProcButton("Manual", ProcOp.Threshold), ProcButton("Otsu", ProcOp.OtsuThreshold), ProcButton("Adaptive", ProcOp.AdaptiveThreshold))));
        root.Controls.Add(Group("Filter / Edge", Row(ProcButton("Blur", ProcOp.Blur), ProcButton("Median", ProcOp.Median), ProcButton("Sharpen", ProcOp.Sharpen), ProcButton("Sobel", ProcOp.Sobel))));
        root.Controls.Add(Group("Morphology", Row(ProcButton("Erode", ProcOp.MorphologyErode), ProcButton("Dilate", ProcOp.MorphologyDilate), ProcButton("Open", ProcOp.MorphologyOpen), ProcButton("Close", ProcOp.MorphologyClose))));
        _status = new Label { Width = 300, Height = 75, ForeColor = Color.White, BackColor = Dark, Font = new Font("Consolas", 9), Text = "Visible tiles are processed on iGPU shader.\r\nCPU analysis is lazy and ROI-scoped." };
        root.Controls.Add(Group("Status", _status));
        return page;
    }

    private TabPage MakeDefectsTab()
    {
        var page = Page("Defects");
        var root = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, BackColor = Dark };
        page.Controls.Add(root);
        _metaTable = MakeMetaTable();
        root.Controls.Add(Group("AOI Metadata", _metaTable));
        _aoiObjects = new CheckedListBox { Width = 315, Height = 300, CheckOnClick = false, BackColor = Color.White, ForeColor = Color.Black };
        _aoiObjects.MouseDown += (_, e) => HandleOverlayListMouseDown(_aoiObjects, e);
        _aoiObjects.ItemCheck += (_, e) => HandleOverlayItemCheck(_aoiObjects, e);
        _aoiObjects.SelectedIndexChanged += (_, _) => SelectOverlayFromList(_aoiObjects);
        root.Controls.Add(Row(ActionButton("Show All", 94, ShowAllAoi), ActionButton("Hide All", 94, HideAllAoi)));
        root.Controls.Add(Group("AOI Objects", _aoiObjects));
        return page;
    }

    private TableLayoutPanel MakeMetaTable()
    {
        var table = new TableLayoutPanel { Width = 315, Height = 300, ColumnCount = 2, RowCount = 8, BackColor = Dark, Padding = new Padding(6) };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 86));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < 8; i++) table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        foreach (var key in new[] { "Created", "Result", "Die XYT", "Chip H/W", "Defects", "Error", "Path", "Recipe" })
        {
            var lk = new Label { Text = key, Width = 82, Height = key is "Path" or "Recipe" ? 48 : 24, ForeColor = Color.FromArgb(128, 210, 255), BackColor = Dark, Font = new Font("Segoe UI", 9, FontStyle.Bold), TextAlign = ContentAlignment.TopLeft };
            var lv = new Label { Name = "v_" + key, Text = "--", Width = 205, Height = key is "Path" or "Recipe" ? 48 : 24, ForeColor = Color.White, BackColor = Dark, Font = new Font("Consolas", 8.6f, FontStyle.Bold), AutoEllipsis = false };
            table.Controls.Add(lk);
            table.Controls.Add(lv);
        }
        return table;
    }

    private void RefreshMetaTable()
    {
        foreach (Control c in _metaTable.Controls)
        {
            if (c is Label l && l.Name.StartsWith("v_", StringComparison.Ordinal))
            {
                string key = l.Name[2..];
                l.Text = WrapMetaValue(key, _metadata.GetValueOrDefault(key, "--"));
            }
        }
    }

    private static string WrapMetaValue(string key, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "--";
        if (key is "Path" or "Recipe")
        {
            if (value.Length > 40) value = value.Insert(Math.Min(value.Length, 40), "\r\n");
        }
        return value;
    }

    private GroupBox Group(string title, Control child)
    {
        var g = new GroupBox { Text = title, Width = 322, Height = child.Height + 34, ForeColor = Color.FromArgb(128, 210, 255), BackColor = Dark, Padding = new Padding(10) };
        child.Left = 10; child.Top = 20; g.Controls.Add(child); return g;
    }

    private FlowLayoutPanel Row(params Control[] controls)
    {
        var p = new FlowLayoutPanel { Width = 302, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Dark, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        foreach (var c in controls) { c.Margin = new Padding(4, 4, 4, 4); p.Controls.Add(c); }
        p.Height = controls.Length == 0 ? 36 : controls.Max(c => c.Height + c.Margin.Vertical) + 8;
        return p;
    }

    private FlowLayoutPanel Stack(params Control[] controls)
    {
        int width = controls.Any(c => c.Width > 150 || c is FlowLayoutPanel) ? 302 : 96;
        var p = new FlowLayoutPanel { Width = width, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Dark, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        foreach (var c in controls) { c.Margin = new Padding(4, 3, 4, 3); p.Controls.Add(c); }
        p.Height = controls.Sum(c => c.Height + c.Margin.Vertical) + 8;
        return p;
    }

    private Label Label(string text) => new() { Text = text, Width = 90, Height = 18, ForeColor = Color.White, BackColor = Dark };
    private Button FlatButton(string text, int w, int h) { var b = new Button { Text = text, Width = w, Height = h, ForeColor = Color.Black, BackColor = Color.FromArgb(250, 250, 250), FlatStyle = FlatStyle.Flat, UseVisualStyleBackColor = false, Font = new Font("Segoe UI", 9, FontStyle.Regular) }; b.FlatAppearance.BorderColor = Color.FromArgb(210, 210, 210); b.FlatAppearance.MouseOverBackColor = Color.FromArgb(225, 242, 255); b.FlatAppearance.MouseDownBackColor = Accent; return b; }
    private Button ActionButton(string text, int w, Action act) { var b = FlatButton(text, w, 30); b.Click += (_, _) => act(); return b; }
    private Button ToolButton(string text, ToolMode mode, bool active)
    {
        var b = FlatButton(text, 95, 30);
        b.Tag = mode;
        _toolButtons[mode] = b;
        b.Click += (_, _) => SetTool(mode);
        SetToolButtonVisual(b, active);
        return b;
    }

    private void SetToolButtonVisual(Button b, bool active)
    {
        b.BackColor = active ? Accent : Color.FromArgb(250, 250, 250);
        b.ForeColor = Color.Black;
        b.FlatAppearance.BorderColor = active ? Color.FromArgb(30, 150, 220) : Color.FromArgb(210, 210, 210);
        b.FlatAppearance.BorderSize = active ? 2 : 1;
        b.Font = new Font("Segoe UI", 9, active ? FontStyle.Bold : FontStyle.Regular);
    }

    private void UpdateToolButtonVisuals()
    {
        foreach (var kv in _toolButtons)
            SetToolButtonVisual(kv.Value, kv.Key == _tool);
    }
    private Button ProcButton(string text, ProcOp op) { var b = FlatButton(text, 68, 30); b.Click += (_, _) => { _proc.SelectedItem = op; if (op == ProcOp.OtsuThreshold && _viewer != IntPtr.Zero) { int t = NativeMethods.NV_ComputeOtsuThreshold(_viewer); _threshold.Value = Math.Max(_threshold.Minimum, Math.Min(_threshold.Maximum, t)); } PushProcessing(); RequestRender(300); }; return b; }
    private Button ProcDisplay(string text, DisplayMode m) { var b = FlatButton(text, 92, 30); b.Click += (_, _) => { _display = m; PushProcessing(); RequestRender(300); }; return b; }
    private NumericUpDown MakeNum(decimal min, decimal max, decimal val, int decimals) => new() { Minimum = min, Maximum = max, Value = val, DecimalPlaces = decimals, Width = 88, Increment = decimals > 0 ? 1 : 1, BackColor = Color.White, ForeColor = Color.Black };

    private void AddNum(TableLayoutPanel p, string label, NumericUpDown n, int col, int row)
    {
        n.Minimum = -100000000; n.Maximum = 100000000; n.DecimalPlaces = 3; n.Width = 130;
        p.Controls.Add(Label(label), col, row); p.Controls.Add(n, col, row + 1);
    }

    private static long NowMs() => Environment.TickCount64;

    private void RequestRender(int continuousMs = 0)
    {
        _renderDirty = true;
        if (continuousMs > 0)
        {
            long until = NowMs() + continuousMs;
            if (until > _continuousRenderUntilMs) _continuousRenderUntilMs = until;
        }
        if (!_renderTimer.Enabled) _renderTimer.Start();
    }

    private bool ShouldRenderFrame()
    {
        long now = NowMs();
        if (_renderDirty) return true;
        if (now < _continuousRenderUntilMs) return true;
        if (_debug.Checked && now >= _nextDebugRenderMs)
        {
            _nextDebugRenderMs = now + 200; // debug HUD refresh at about 5 FPS when idle
            return true;
        }
        return false;
    }

    private void EnsureNative()
    {
        if (_viewer != IntPtr.Zero) return;
        _viewport.CreateControl();
        _viewer = NativeMethods.NV_Create(_viewport.Handle);
        if (_viewer == IntPtr.Zero) throw new InvalidOperationException("NativeD3D11Viewer 建立失敗。請確認 DLL 在執行目錄，且顯示卡支援 D3D11。");
        NativeMethods.NV_Resize(_viewer, _viewport.Width, _viewport.Height);
        SetTool(_tool); PushOptions(); PushProcessing(); RequestRender(800); _renderTimer.Start();
    }

    private async Task OpenImageDialogAsync()
    {
        using var dlg = new OpenFileDialog { Filter = "Images|*.bmp;*.png;*.jpg;*.jpeg;*.jfif;*.tif;*.tiff;*.gif;*.wdp;*.jxr;*.webp|All files|*.*" };
        if (dlg.ShowDialog(this) == DialogResult.OK) await LoadImageAsync(dlg.FileName);
    }

    private async Task LoadImageAsync(string file)
    {
        EnsureNative();
        if (_loading) return;
        _loading = true; _renderTimer.Stop(); UseWaitCursor = true; _status.Text = "Loading in native C++ worker...\r\nUI remains responsive.";
        try
        {
            var result = await Task.Run(() => NativeMethods.NV_LoadImage(_viewer, file, out _lastInfo));
            if (result == 0)
            {
                var sb = new StringBuilder(2048); NativeMethods.NV_GetLastError(_viewer, sb, sb.Capacity);
                MessageBox.Show(sb.ToString(), "Load failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            UpdateFolderImageList(file);
            Text = $"AOI D3D11 Native Hybrid Image Viewer - {Path.GetFileName(file)}  ({CurrentFolderIndexText()})";
            ParseAoiMetadata(file);
            SeedAoiObjects();
            RefreshOverlayList(); UpdateRoiAnalysis(); NativeMethods.NV_FitToWindow(_viewer); RequestRender(1500);
        }
        finally
        {
            _renderTimer.Start(); UseWaitCursor = false; _loading = false;
        }
    }


    private static readonly HashSet<string> ImageExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".bmp", ".png", ".jpg", ".jpeg", ".jfif", ".tif", ".tiff", ".gif", ".wdp", ".jxr", ".webp"
    };

    private void UpdateFolderImageList(string file)
    {
        string? dir = Path.GetDirectoryName(file);
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
        {
            _folderImages = new List<string> { file };
            _folderImageIndex = 0;
            return;
        }

        _folderImages = Directory.EnumerateFiles(dir)
            .Where(p => ImageExts.Contains(Path.GetExtension(p)))
            .OrderBy(p => Path.GetFileName(p), StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        _folderImageIndex = _folderImages.FindIndex(p => string.Equals(Path.GetFullPath(p), Path.GetFullPath(file), StringComparison.OrdinalIgnoreCase));
        if (_folderImageIndex < 0)
        {
            _folderImages.Add(file);
            _folderImages = _folderImages.OrderBy(p => Path.GetFileName(p), StringComparer.CurrentCultureIgnoreCase).ToList();
            _folderImageIndex = _folderImages.FindIndex(p => string.Equals(Path.GetFullPath(p), Path.GetFullPath(file), StringComparison.OrdinalIgnoreCase));
        }
    }

    private string CurrentFolderIndexText()
    {
        return _folderImages.Count > 0 && _folderImageIndex >= 0
            ? $"{_folderImageIndex + 1}/{_folderImages.Count}"
            : "--/--";
    }

    private async Task LoadSiblingImageAsync(int direction)
    {
        if (_loading) return;
        if (_folderImages.Count == 0 || _folderImageIndex < 0)
        {
            if (_lastInfo.width > 0 && !string.IsNullOrWhiteSpace(_lastInfo.path))
                UpdateFolderImageList(_lastInfo.path);
            else
                return;
        }
        if (_folderImages.Count <= 1) return;

        int next = (_folderImageIndex + direction + _folderImages.Count) % _folderImages.Count;
        await LoadImageAsync(_folderImages[next]);
    }

    private void TickRender()
    {
        if (_viewer == IntPtr.Zero) return;
        if (!ShouldRenderFrame()) return;

        bool stillContinuous = NowMs() < _continuousRenderUntilMs;
        _renderDirty = false;

        NativeMethods.NV_Render(_viewer);
        SyncSelectedOverlayFromNative(false);
        NativeMethods.NV_GetDiagnostics(_viewer, out var d);

        if (_debug.Checked)
        {
            _status.Text = $"FPS {d.fps:F1}   Render {d.renderMs:F2} ms\r\nScale {d.scale:F3}  Dirty {(_renderDirty ? 1 : 0)}  Active {(stillContinuous ? 1 : 0)}\r\nGPU texture {d.imageWidth} x {d.imageHeight}\r\nOverlay {d.overlayCount}";
        }
    }

    private void UpdateMouseInfo(int x, int y)
    {
        if (NativeMethods.NV_GetMouseImageInfo(_viewer, x, y, out int ix, out int iy, out int gray) != 0)
            _mouse.Text = _gray.Checked ? $"X: {ix} , Y: {iy} , Gray: {gray}" : $"X: {ix} , Y: {iy}";
        else _mouse.Text = "X: --- , Y: --- , Gray: ---";
    }

    private void RefreshOverlayList()
    {
        if (_viewer == IntPtr.Zero) return;
        _refreshingList = true;
        _overlays.Items.Clear();
        _aoiObjects.Items.Clear();
        int manualSelect = -1, aoiSelect = -1;
        int n = NativeMethods.NV_GetOverlayCount(_viewer);
        for (int i = 0; i < n; i++)
        {
            if (NativeMethods.NV_GetOverlayInfo(_viewer, i, out var o) == 0) continue;
            var row = new OverlayRow(o.id, o.name, (OverlayType)o.type, o);
            if (IsAoi(row.Type))
            {
                int idx = _aoiObjects.Items.Add(row, o.visible != 0);
                if (row.Id == _selectedOverlayId) aoiSelect = idx;
            }
            else
            {
                int idx = _overlays.Items.Add(row, o.visible != 0);
                if (row.Id == _selectedOverlayId) manualSelect = idx;
            }
        }
        if (manualSelect >= 0) _overlays.SelectedIndex = manualSelect;
        if (aoiSelect >= 0) _aoiObjects.SelectedIndex = aoiSelect;
        _refreshingList = false;
    }

    private void HandleOverlayListMouseDown(CheckedListBox list, MouseEventArgs e)
    {
        int index = list.IndexFromPoint(e.Location);
        if (index < 0) return;

        Rectangle itemRect = list.GetItemRectangle(index);
        Rectangle checkBoxRect = new Rectangle(itemRect.Left + 1, itemRect.Top + 2, 18, Math.Max(14, itemRect.Height - 4));

        list.SelectedIndex = index;

        // Only the actual checkbox square toggles visibility.
        // Row click selects only; double-click/row click will not toggle.
        if (checkBoxRect.Contains(e.Location))
        {
            _allowListCheckChange = true;
            list.SetItemChecked(index, !list.GetItemChecked(index));
            _allowListCheckChange = false;
        }
    }

    private void HandleOverlayItemCheck(CheckedListBox list, ItemCheckEventArgs e)
    {
        if (_refreshingList) return;

        if (!_allowListCheckChange)
        {
            e.NewValue = e.CurrentValue;
            return;
        }

        if (_viewer == IntPtr.Zero) return;
        BeginInvoke(new Action(() =>
        {
            if (e.Index >= 0 && e.Index < list.Items.Count && list.Items[e.Index] is OverlayRow r)
                NativeMethods.NV_SetOverlayVisible(_viewer, r.Id, e.NewValue == CheckState.Checked ? 1 : 0);
        }));
    }

    private void SelectOverlayFromList(CheckedListBox list)
    {
        if (_refreshingList || _viewer == IntPtr.Zero) return;
        if (list.SelectedItem is not OverlayRow r) return;
        _selectedOverlayId = r.Id;
        NativeMethods.NV_SelectOverlay(_viewer, r.Id);
        LoadGeometryFrom(r.Info);
        UpdateRoiAnalysis();
    }

    private bool GeometryFocused() => _geoX.Focused || _geoY.Focused || _geoW.Focused || _geoH.Focused || _geoA.Focused;

    private void SyncSelectedOverlayFromNative(bool forceGeometry)
    {
        if (_viewer == IntPtr.Zero) return;
        if (NativeMethods.NV_GetSelectedOverlayInfo(_viewer, out var o) == 0) return;
        _selectedOverlayId = o.id;
        if (forceGeometry || !GeometryFocused())
        {
            _syncingGeometry = true;
            LoadGeometryFrom(o);
            _syncingGeometry = false;
        }
    }

    private static bool IsAoi(OverlayType t) => t is OverlayType.AoiDieCenter or OverlayType.AoiDieCorner or OverlayType.AoiDefect;

    private void UpdateRoiAnalysis()
    {
        if (_viewer == IntPtr.Zero) return;
        var hist = new int[256]; var px = new double[4096]; var py = new double[4096];
        if (NativeMethods.NV_AnalyzeSelectedRoi(_viewer, out var s, hist, px, px.Length, py, py.Length) == 0 || s.valid == 0)
        {
            _roi.Text = "ROI: --\r\nSample: --\r\nMean: --   Std: --\r\nMin/Max: -- / --\r\nPixels: --";
            return;
        }
        _roi.Text = $"ROI: ({s.x},{s.y}) {s.width}x{s.height}\r\nSample: {s.pixels}\r\nMean: {s.mean:F3}   Std: {s.stddev:F3}\r\nMin/Max: {s.minValue} / {s.maxValue}\r\nPixels: {s.pixels}\r\nBlob: {s.blobCount}  Largest: {s.largestBlobArea}";
    }

    private void ParseAoiMetadata(string file)
    {
        _metadata.Clear();
        _aoiDefects.Clear();
        _diePos = null;

        var fi = new FileInfo(file);
        _metadata["Created"] = "--";
        _metadata["Result"] = "--";
        _metadata["Die XYT"] = "--";
        _metadata["Chip H/W"] = "--";
        _metadata["Defects"] = "0";
        _metadata["Error"] = "AOI metadata not found.";
        _metadata["Path"] = file;
        _metadata["Recipe"] = "--";

        try
        {
            var json = ReadAoiJson(file, out string source);
            if (!string.IsNullOrWhiteSpace(json))
            {
                ParseAoiJson(json!, source);
            }
            else
            {
                ReadSidecarMetadata(file);
            }
        }
        catch (Exception ex)
        {
            _metadata["Error"] = ex.Message;
        }

        var sb = new StringBuilder();
        AppendMeta(sb, "Created");
        AppendMeta(sb, "Result");
        AppendMeta(sb, "Die XYT");
        AppendMeta(sb, "Chip H/W");
        AppendMeta(sb, "Defects");
        AppendMeta(sb, "Error");
        AppendMeta(sb, "Path");
        AppendMeta(sb, "Recipe");
        _meta.Text = sb.ToString();
        RefreshMetaTable();
    }

    private void AppendMeta(StringBuilder sb, string key)
    {
        string value = _metadata.GetValueOrDefault(key, "--");
        sb.AppendLine($"{key,-11} {value}");
    }

    private static string? ReadAoiJson(string file, out string source)
    {
        source = "";
        string ext = Path.GetExtension(file).ToLowerInvariant();
        var len = new FileInfo(file).Length;
        if (ext is not ".jpg" and not ".jpeg" && len > 200L * 1024 * 1024) return null;
        byte[] bytes = File.ReadAllBytes(file);
        string? xmp = ReadGmmJsonFromJpgXmp(bytes);
        if (!string.IsNullOrWhiteSpace(xmp)) { source = "XMP APP1 / gmm:AOIJson"; return xmp; }
        string? appended = ReadOldAppendMetadata(bytes);
        if (!string.IsNullOrWhiteSpace(appended)) { source = "APPENDED / AOI_BEGIN"; return appended; }
        return null;
    }

    private static string? ReadGmmJsonFromJpgXmp(byte[] bytes)
    {
        byte[] header = Encoding.ASCII.GetBytes("http://ns.adobe.com/xap/1.0/\0");
        int pos = FindPattern(bytes, header, 0);
        if (pos < 0) return null;
        int xmpStart = pos + header.Length;
        int segStart = pos - 4;
        if (segStart < 0 || bytes[segStart] != 0xFF || bytes[segStart + 1] != 0xE1) return null;
        int segLength = (bytes[segStart + 2] << 8) | bytes[segStart + 3];
        int segEnd = segStart + 2 + segLength;
        if (segEnd > bytes.Length || xmpStart >= segEnd) return null;
        string xmp = Encoding.UTF8.GetString(bytes, xmpStart, segEnd - xmpStart);
        const string beginTag = "<gmm:AOIJson><![CDATA[";
        const string endTag = "]]></gmm:AOIJson>";
        int b = xmp.IndexOf(beginTag, StringComparison.Ordinal);
        if (b < 0) return null;
        b += beginTag.Length;
        int e = xmp.IndexOf(endTag, b, StringComparison.Ordinal);
        if (e < 0) return null;
        return xmp.Substring(b, e - b).Replace("]]]]><![CDATA[>", "]]>").Trim();
    }

    private static string? ReadOldAppendMetadata(byte[] bytes)
    {
        byte[] begin = Encoding.UTF8.GetBytes("\nAOI_BEGIN\n");
        byte[] end = Encoding.UTF8.GetBytes("\nAOI_END\n");
        int s = FindPattern(bytes, begin, 0);
        if (s < 0) return null;
        s += begin.Length;
        int e = FindPattern(bytes, end, s);
        if (e < 0 || e <= s) return null;
        return Encoding.UTF8.GetString(bytes, s, e - s);
    }

    private static int FindPattern(byte[] data, byte[] pattern, int start)
    {
        for (int i = Math.Max(0, start); i <= data.Length - pattern.Length; i++)
        {
            bool ok = true;
            for (int j = 0; j < pattern.Length; j++) if (data[i + j] != pattern[j]) { ok = false; break; }
            if (ok) return i;
        }
        return -1;
    }

    private void ParseAoiJson(string json, string source)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        _metadata["Source"] = source;
        _metadata["Created"] = GetString(root, "created_time", "--");
        _metadata["Result"] = GetBool(root, "detectResult") ? "True" : "False";
        double dcx = GetDouble(root, "dieCenterX"), dcy = GetDouble(root, "dieCenterY"), dct = GetDouble(root, "dieCenterT");
        _metadata["Die XYT"] = $"{dcx:0.####}, {dcy:0.####}, {dct:0.####}";
        double chipH = GetDouble(root, "dChipH_mm"), chipW = GetDouble(root, "dChipW_mm");
        _metadata["Chip H/W"] = chipH > 0 || chipW > 0 ? $"{chipH:0.####} / {chipW:0.####} mm" : "--";
        _metadata["Error"] = GetString(root, "errMsg", "--");
        _metadata["Recipe"] = GetString(root, "recipe_path", "--");
        _diePos = ParseRect(GetString(root, "diePos", ""));
        if (dcx != 0 || dcy != 0) _metadata["DieCenterX"] = dcx.ToString(CultureInfo.InvariantCulture);
        if (dcx != 0 || dcy != 0) _metadata["DieCenterY"] = dcy.ToString(CultureInfo.InvariantCulture);
        if (dct != 0) _metadata["DieCenterT"] = dct.ToString(CultureInfo.InvariantCulture);

        if (root.TryGetProperty("defectList", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            int index = 1;
            foreach (var d in arr.EnumerateArray())
            {
                var r = ParseRect(GetString(d, "Rect", ""));
                if (r == null || r.Value.W <= 0 || r.Value.H <= 0) continue;
                double wu = GetDouble(d, "WidthUm"), hu = GetDouble(d, "HeightUm");
                string bin = GetString(d, "BinCode", "--");
                string algo = FormatAlgoIds(GetString(d, "AlgoID", ""));
                _aoiDefects.Add(new AoiDefectRec(index++, r.Value, wu, hu, bin, algo));
            }
        }
        _metadata["Defects"] = _aoiDefects.Count.ToString(CultureInfo.InvariantCulture);
    }

    private static string GetString(JsonElement e, string name, string fallback)
    {
        if (!e.TryGetProperty(name, out var v) || v.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return fallback;
        return v.ValueKind == JsonValueKind.String ? v.GetString() ?? fallback : v.ToString();
    }

    private static double GetDouble(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out double d)) return d;
        return double.TryParse(v.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out d) ? d : 0;
    }

    private static bool GetBool(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return false;
        if (v.ValueKind == JsonValueKind.True) return true;
        if (v.ValueKind == JsonValueKind.False) return false;
        return bool.TryParse(v.ToString(), out bool b) && b;
    }

    private static AoiRect? ParseRect(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var p = text.Split(',');
        if (p.Length < 4) return null;
        return new AoiRect(ToD(p[0]), ToD(p[1]), ToD(p[2]), ToD(p[3]));
    }

    private static double ToD(string s) => double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;

    private static string FormatAlgoIds(string algo)
    {
        if (!long.TryParse(algo, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) || v <= 0) return "--";
        var ids = new List<string>();
        for (int i = 0; i < 63; i++) if ((v & (1L << i)) != 0) ids.Add("AlgoID:" + i.ToString(CultureInfo.InvariantCulture));
        return ids.Count == 0 ? "--" : string.Join(", ", ids);
    }

    private void ReadSidecarMetadata(string file)
    {
        foreach (var ext in new[] { ".json", ".txt", ".csv", ".aoi" })
        {
            var side = Path.ChangeExtension(file, ext);
            if (!File.Exists(side)) continue;
            _metadata["Source"] = side;
            string text = File.ReadAllText(side);
            if (text.TrimStart().StartsWith("{")) { ParseAoiJson(text, side); return; }
            foreach (var line in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var m = Regex.Match(line, @"^\s*([A-Za-z0-9_ /.-]+)\s*[:=,]\s*(.+?)\s*$");
                if (m.Success && _metadata.Count < 32) _metadata[m.Groups[1].Value.Trim()] = m.Groups[2].Value.Trim();
            }
            break;
        }
    }

    private void SeedAoiObjects()
    {
        if (_lastInfo.width <= 0 || _viewer == IntPtr.Zero) return;
        if (_metadata.TryGetValue("DieCenterX", out var sx) && _metadata.TryGetValue("DieCenterY", out var sy) && float.TryParse(sx, NumberStyles.Float, CultureInfo.InvariantCulture, out var cx) && float.TryParse(sy, NumberStyles.Float, CultureInfo.InvariantCulture, out var cy))
            NativeMethods.NV_AddOverlay(_viewer, (int)OverlayType.AoiDieCenter, cx, cy, MathF.Max(8, MathF.Min(_lastInfo.width, _lastInfo.height) * 0.015f), 0, 0, "AOI Die Center");
        if (_diePos is { } die && die.W > 0 && die.H > 0)
            NativeMethods.NV_AddOverlay(_viewer, (int)OverlayType.AoiDieCorner, (float)die.X, (float)die.Y, (float)die.W, (float)die.H, 0, "AOI Die Corners");
        foreach (var d in _aoiDefects)
        {
            NativeMethods.NV_AddOverlay(_viewer, (int)OverlayType.AoiDefect, (float)d.Rect.X, (float)d.Rect.Y, (float)d.Rect.W, (float)d.Rect.H, 0, $"AOI Defect {d.Index}  Bin {d.Bin}  {d.AlgoIds}");
        }
    }

    private void LoadGeometryFrom(NVOverlayInfo o)
    {
        _geoX.Value = SafeDec(o.x1); _geoY.Value = SafeDec(o.y1);
        _geoW.Value = SafeDec(Math.Abs(o.x2 - o.x1)); _geoH.Value = SafeDec(Math.Abs(o.y2 - o.y1)); _geoA.Value = SafeDec(o.angle);
    }

    private static decimal SafeDec(float f) => Math.Max(-100000000, Math.Min(100000000, (decimal)f));

    private void ApplyGeometry()
    {
        if (_viewer == IntPtr.Zero || _overlays.SelectedItem is not OverlayRow r || IsAoi(r.Type)) return;
        NativeMethods.NV_UpdateOverlay(_viewer, r.Id, (float)_geoX.Value, (float)_geoY.Value, (float)_geoW.Value, (float)_geoH.Value, (float)_geoA.Value);
        _selectedOverlayId = r.Id;
        SyncSelectedOverlayFromNative(true);
        RefreshOverlayList(); UpdateRoiAnalysis(); RequestRender(300);
    }

    private void DeleteSelected()
    {
        if (_viewer == IntPtr.Zero || _overlays.SelectedItem is not OverlayRow r || IsAoi(r.Type)) return;
        NativeMethods.NV_DeleteOverlay(_viewer, r.Id); RefreshOverlayList(); UpdateRoiAnalysis(); RequestRender(300);
    }

    private void ClearAll()
    {
        if (_viewer == IntPtr.Zero) return;
        NativeMethods.NV_ClearOverlays(_viewer); RefreshOverlayList(); UpdateRoiAnalysis(); RequestRender(300);
    }

    private void ShowAllAoi()
    {
        if (_viewer == IntPtr.Zero) return;
        for (int i = 0; i < _aoiObjects.Items.Count; i++)
        {
            if (_aoiObjects.Items[i] is OverlayRow r) NativeMethods.NV_SetOverlayVisible(_viewer, r.Id, 1);
        }
        RefreshOverlayList(); RequestRender(300);
    }

    private void HideAllAoi()
    {
        if (_viewer == IntPtr.Zero) return;
        for (int i = 0; i < _aoiObjects.Items.Count; i++)
        {
            if (_aoiObjects.Items[i] is OverlayRow r) NativeMethods.NV_SetOverlayVisible(_viewer, r.Id, 0);
        }
        RefreshOverlayList(); RequestRender(300);
    }

    private void SetTool(ToolMode t)
    {
        _tool = t;
        UpdateToolButtonVisuals();
        EnsureNative();
        NativeMethods.NV_SetToolMode(_viewer, (int)t);
        RequestRender(200);
    }

    private void PushOptions()
    {
        if (_viewer == IntPtr.Zero) return;
        NativeMethods.NV_SetOptions(_viewer, _crosshair.Checked ? 1 : 0, _gray.Checked ? 1 : 0, _debug.Checked ? 1 : 0);
        RequestRender(_debug.Checked ? 600 : 200);
    }

    private void PushProcessing()
    {
        if (_viewer == IntPtr.Zero || _proc.SelectedItem == null) return;
        NativeMethods.NV_SetProcessing(_viewer, (int)(ProcOp)_proc.SelectedItem, (int)_display, (int)_threshold.Value, (double)_gamma.Value / 100.0, (int)_radius.Value);
        RequestRender(300);
    }

    private void ExportCurrentViewport()
    {
        if (_lastInfo.width <= 0 || string.IsNullOrWhiteSpace(_lastInfo.path) || !File.Exists(_lastInfo.path))
        {
            MessageBox.Show("No image loaded.", "Export", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        using var dlg = new SaveFileDialog
        {
            Filter = "JPEG image|*.jpg|PNG image|*.png",
            FileName = Path.GetFileNameWithoutExtension(_lastInfo.path) + "_layer_export.jpg"
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        var exportOverlays = new List<AoiExportOverlay>();
        int n = NativeMethods.NV_GetOverlayCount(_viewer);
        for (int i = 0; i < n; i++)
        {
            if (NativeMethods.NV_GetOverlayInfo(_viewer, i, out var o) == 0) continue;
            exportOverlays.Add(new AoiExportOverlay(
                o.id,
                (AoiExportOverlayType)o.type,
                o.visible != 0,
                o.x1,
                o.y1,
                o.x2,
                o.y2,
                o.angle,
                o.name ?? string.Empty));
        }

        try
        {
            AoiBatchExporter.ExportImageWithOverlays(_lastInfo.path, dlg.FileName, exportOverlays);
            MessageBox.Show($"Layer-combined image exported:\r\n{dlg.FileName}", "Export", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Export failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task BatchExportFolderAsync()
    {
        using var srcDlg = new FolderBrowserDialog { Description = "Select source image folder." };
        if (!string.IsNullOrWhiteSpace(_lastInfo.path))
        {
            string? dir = Path.GetDirectoryName(_lastInfo.path);
            if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir)) srcDlg.SelectedPath = dir;
        }
        if (srcDlg.ShowDialog(this) != DialogResult.OK) return;

        using var dstDlg = new FolderBrowserDialog { Description = "Select destination folder for exported images." };
        if (dstDlg.ShowDialog(this) != DialogResult.OK) return;

        UseWaitCursor = true;
        _status.Text = "Batch exporting AOI layouts...";
        try
        {
            var result = await Task.Run(() => AoiBatchExporter.ExportFolder(srcDlg.SelectedPath, dstDlg.SelectedPath));
            var sb = new StringBuilder();
            sb.AppendLine($"Source: {srcDlg.SelectedPath}");
            sb.AppendLine($"Dest:   {dstDlg.SelectedPath}");
            sb.AppendLine();
            sb.AppendLine($"Scanned:  {result.TotalScanned}");
            sb.AppendLine($"Exported: {result.ExportedCount}");
            sb.AppendLine($"No metadata copied: {result.SkippedWithoutMetadata}");
            sb.AppendLine($"Failed:   {result.FailedCount}");
            if (result.Errors.Count > 0)
            {
                sb.AppendLine();
                foreach (var e in result.Errors.Take(12)) sb.AppendLine(e);
                if (result.Errors.Count > 12) sb.AppendLine($"... {result.Errors.Count - 12} more");
            }
            MessageBox.Show(sb.ToString(), "Batch Export", MessageBoxButtons.OK, result.FailedCount == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }
        finally
        {
            UseWaitCursor = false;
        }
    }

    private async void MainForm_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.V) SetTool(ToolMode.Pan);
        else if (e.KeyCode == Keys.Z) SetTool(ToolMode.ZoomRect);
        else if (e.KeyCode == Keys.P) SetTool(ToolMode.Pixel);
        else if (e.KeyCode == Keys.R) SetTool(ToolMode.Roi);
        else if (e.KeyCode == Keys.M) SetTool(ToolMode.Measure);
        else if (e.KeyCode == Keys.C) SetTool(ToolMode.Circle);
        else if (e.KeyCode == Keys.D) { _debug.Checked = !_debug.Checked; PushOptions(); RequestRender(600); }
        else if (e.KeyCode == Keys.F) { NativeMethods.NV_FitToWindow(_viewer); RequestRender(600); }
        else if (e.KeyCode == Keys.D1) { NativeMethods.NV_OneToOne(_viewer); RequestRender(600); }
        else if (e.KeyCode == Keys.PageDown || e.KeyCode == Keys.Right) await LoadSiblingImageAsync(1);
        else if (e.KeyCode == Keys.PageUp || e.KeyCode == Keys.Left) await LoadSiblingImageAsync(-1);
        else if (e.Control && e.KeyCode == Keys.O) await OpenImageDialogAsync();
        else if (e.Control && e.KeyCode == Keys.E) ExportCurrentViewport();
        else if (e.Control && e.KeyCode == Keys.B) await BatchExportFolderAsync();
    }

    private string ImageInfoText()
    {
        if (_lastInfo.width <= 0) return "No image loaded.";
        return $"Path: {_lastInfo.path}\r\nSize: {_lastInfo.width} x {_lastInfo.height}\r\nBit depth/source: {_lastInfo.bitDepth}\r\nFormat: {_lastInfo.format}\r\nFile size: {_lastInfo.fileSize:n0} bytes\r\n\r\n底層：Native C++ / D3D11 / WIC / Large BMP loader\r\n前端：C# WinForms UI + P/Invoke\r\n\r\n操作：右鍵拖曳 = Pan；滾輪 = Zoom；Debug 與 Gray Value 皆由 C++ D3D overlay 繪製。";
    }

    private static int MouseButton(MouseEventArgs e) => e.Button == MouseButtons.Left ? 1 : e.Button == MouseButtons.Right ? 2 : 0;

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _renderTimer.Stop();
        if (_viewer != IntPtr.Zero) { NativeMethods.NV_Destroy(_viewer); _viewer = IntPtr.Zero; }
        base.OnFormClosed(e);
    }

    private sealed record OverlayRow(int Id, string Name, OverlayType Type, NVOverlayInfo Info)
    {
        public override string ToString() => Name;
    }

    private readonly record struct AoiRect(double X, double Y, double W, double H);
    private sealed record AoiDefectRec(int Index, AoiRect Rect, double WidthUm, double HeightUm, string Bin, string AlgoIds);
}
