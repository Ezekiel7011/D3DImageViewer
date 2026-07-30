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

/// <summary>
/// Main WinForms shell that hosts the native D3D11 viewport and coordinates image, overlay, metadata, and export workflows.
/// </summary>
public sealed class MainForm : Form
{
    #region Fields and Constants

    // Native viewer host and the WinForms controls that mirror native state.
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
    private CheckBox _fixedRectEnabled = new();
    private TextBox _fixedRectW = new();
    private TextBox _fixedRectH = new();
    private Label _fixedRectStatus = new();
    private int _fixedRectOverlayId = -1;

    private ToolMode _tool = ToolMode.ZoomRect;
    private DisplayMode _display = DisplayMode.Base;
    private bool _refreshingList;
    private bool _loading;
    private NVImageInfo _lastInfo;
    private readonly Dictionary<string, string> _metadata = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<AoiDefectRec> _aoiDefects = new();
    private readonly List<GenericRectRec> _genericRects = new();
    private readonly List<DrawPolygonRec> _drawPolygons = new();
    private readonly HashSet<int> _metadataOverlayIds = new();
    private readonly Dictionary<int, string> _metadataObjectListNames = new();
    private AoiRect? _diePos;
    private List<string> _folderImages = new();
    private int _folderImageIndex = -1;
    private long _folderListVersion;
    private int _selectedOverlayId = -1;
    private bool _syncingGeometry;
    private bool _allowListCheckChange;
    private int _pendingSiblingDirection;
    private DateTime _lastSiblingRequestUtc = DateTime.MinValue;
    private readonly Dictionary<ToolMode, Button> _toolButtons = new();

    // Dirty render mode: static images should not redraw at full speed when idle.
    // Render is requested by UI/input changes, and continuous rendering is enabled briefly
    // during interactions so pan/zoom/tile updates stay smooth.
    private bool _renderDirty = true;
    private long _continuousRenderUntilMs;
    private long _nextDebugRenderMs;
    private int _lastRenderReason;

    private const int RenderReasonIdle = 0;
    private const int RenderReasonDirty = 1;
    private const int RenderReasonActive = 2;
    private const int RenderReasonTile = 3;
    private const int RenderReasonDebug = 4;

    private const int TileUploadBudgetPerFrame = 12;
    private const int CpuTileCacheLimitMb = 512;
    private const int GpuTileCacheLimitMb = 384;
    private const int MaxTileQueueCount = 512;


    private static readonly Color Dark = Color.FromArgb(24, 28, 31);
    private static readonly Color Dark2 = Color.FromArgb(32, 36, 39);
    private static readonly Color Accent = Color.FromArgb(112, 190, 245);

    private readonly string? _initialFile;

    #endregion

    #region Construction

    /// <summary>
    /// Creates the viewer form and optionally loads an initial image after the native host is ready.
    /// </summary>
    public MainForm(string? initialFile = null)
    {
        _initialFile = initialFile;
        Text = "AOI D3D11 Native Hybrid Image Viewer";
        Width = 1500;
        Height = 900;
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;
        BackColor = Color.Black;
        Icon appIcon = TryLoadApplicationIcon();
        if (appIcon != null) Icon = appIcon;
        BuildUi();
        _renderTimer.Tick += (_, _) => TickRender();
        Shown += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(_initialFile) && File.Exists(_initialFile))
            {
                // File-association launch: initialize D3D before loading so image open speed
                // stays close to the original eager-initialization behavior.
                EnsureNative();
                BeginInvoke((MethodInvoker)(async () => await LoadImageAsync(_initialFile)));
            }
            else
            {
                // Empty launch: show the window first, then warm up native D3D during idle time.
                // This keeps app startup responsive without making the first manual image load pay
                // the full D3D/DirectWrite initialization cost.
                BeginInvoke((MethodInvoker)(() => EnsureNative()));
            }
        };
    }

    private static Icon TryLoadApplicationIcon()
    {
        try
        {
            return File.Exists(Application.ExecutablePath) ? Icon.ExtractAssociatedIcon(Application.ExecutablePath) : null;
        }
        catch
        {
            return null;
        }
    }

    #endregion

    #region UI Construction

    private void BuildUi()
    {
        Controls.Add(_viewport);
        Controls.Add(MakeTopBar());
        Controls.Add(MakeRightTabs());

        // Input events are forwarded to the native renderer first, then the managed UI
        // refreshes only the state that can change as a result of that interaction.
        _viewport.MouseDown += (_, e) =>
        {
            if (_loading) return;

            EnsureNative();
            NativeMethods.NV_MouseDown(_viewer, MouseButton(e), e.X, e.Y, 0);
            SyncSelectedOverlayFromNative(false);
            RefreshOverlayList();
            RequestRender(600);
        };

        _viewport.MouseMove += (_, e) =>
        {
            if (_loading || _viewer == IntPtr.Zero) return;

            NativeMethods.NV_MouseMove(_viewer, e.X, e.Y, 0);
            UpdateMouseInfo(e.X, e.Y);

            if (e.Button != MouseButtons.None)
            {
                SyncSelectedOverlayFromNative(false);
                RequestRender(300);
            }
            else if (_debug.Checked)
            {
                RequestRender();
            }
        };

        _viewport.MouseUp += (_, e) =>
        {
            if (_loading || _viewer == IntPtr.Zero) return;

            NativeMethods.NV_MouseUp(_viewer, MouseButton(e), e.X, e.Y, 0);
            SyncSelectedOverlayFromNative(true);
            RefreshOverlayList();
            UpdateRoiAnalysis();
            RequestRender(400);
        };

        _viewport.MouseWheel += (_, e) =>
        {
            if (_loading || _viewer == IntPtr.Zero) return;

            NativeMethods.NV_MouseWheel(_viewer, e.Delta, e.X, e.Y);
            RequestRender(900);
        };

        _viewport.Resize += (_, _) =>
        {
            if (_loading || _viewer == IntPtr.Zero) return;

            NativeMethods.NV_Resize(_viewer, _viewport.Width, _viewport.Height);
            RequestRender(400);
        };

        _viewport.DragEnter += (_, e) =>
        {
            if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true)
            {
                e.Effect = DragDropEffects.Copy;
            }
        };

        _viewport.DragDrop += async (_, e) =>
        {
            var files = (string[])e.Data!.GetData(DataFormats.FileDrop)!;
            if (files.Length > 0)
            {
                await LoadImageAsync(files[0]);
            }
        };

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
            b.Left = x;
            b.Top = 12;
            b.Click += ev;
            x += w + 10;
            p.Controls.Add(b);
            return b;
        }

        B("Load", 74, async (_, _) => await OpenImageDialogAsync());
        B("Prev", 62, async (_, _) => await LoadSiblingImageAsync(-1));
        B("Next", 62, async (_, _) => await LoadSiblingImageAsync(1));
        B("Fit", 58, (_, _) =>
        {
            EnsureNative();
            NativeMethods.NV_FitToWindow(_viewer);
            RequestRender(600);
        });
        B("1:1", 58, (_, _) =>
        {
            EnsureNative();
            NativeMethods.NV_OneToOne(_viewer);
            RequestRender(600);
        });
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
            c.CheckedChanged += (_, _) =>
            {
                PushOptions();
                RequestRender(300);
            };
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

        _overlays.Width = 320;
        _overlays.Height = 150;
        _overlays.CheckOnClick = false;
        _overlays.BackColor = Color.White;
        _overlays.ForeColor = Color.Black;

        var listGroup = Group("Overlay Items", _overlays);
        _overlays.MouseDown += (_, e) => HandleOverlayListMouseDown(_overlays, e);
        _overlays.ItemCheck += (_, e) => HandleOverlayItemCheck(_overlays, e);
        _overlays.SelectedIndexChanged += (_, _) => SelectOverlayFromList(_overlays);
        root.Controls.Add(listGroup);
        root.Controls.Add(Row(ActionButton("Delete", 94, DeleteSelected), ActionButton("Clear All", 94, ClearAll)));

        var geom = new TableLayoutPanel { Width = 320, Height = 180, ColumnCount = 2, RowCount = 6, BackColor = Dark };
        geom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        geom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        AddNum(geom, "X", _geoX, 0, 0);
        AddNum(geom, "Y", _geoY, 1, 0);
        AddNum(geom, "W/Len", _geoW, 0, 2);
        AddNum(geom, "H", _geoH, 1, 2);
        AddNum(geom, "Angle", _geoA, 0, 4);

        var apply = ActionButton("Apply", 130, ApplyGeometry);
        geom.Controls.Add(apply, 1, 5);
        root.Controls.Add(Group("Geometry", geom));

        root.Controls.Add(Group("Fixed Center Rect", MakeFixedRectPanel()));

        _roi = new Label { Width = 320, Height = 125, Font = new Font("Consolas", 9), ForeColor = Color.White, BackColor = Dark, Text = "ROI: --\r\nSample: --\r\nMean: --   Std: --\r\nMin/Max: -- / --\r\nPixels: --" };
        root.Controls.Add(Group("Selected ROI", _roi));
        return page;
    }


    private Control MakeFixedRectPanel()
    {
        var panel = new FlowLayoutPanel { Width = 302, Height = 112, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Dark };
        _fixedRectEnabled = new CheckBox
        {
            Text = "Enable fixed center rect",
            Width = 270,
            Height = 24,
            ForeColor = Color.White,
            BackColor = Dark
        };
        _fixedRectEnabled.CheckedChanged += (_, _) => ApplyFixedCenterRect();

        _fixedRectW = FixedRectTextBox("512");
        _fixedRectH = FixedRectTextBox("512");
        _fixedRectStatus = new Label { Width = 288, Height = 22, Text = "Center: image center; edit: key-in only", ForeColor = Color.FromArgb(200, 220, 235), BackColor = Dark };

        panel.Controls.Add(_fixedRectEnabled);
        panel.Controls.Add(Row(Stack(Label("Width"), _fixedRectW), Stack(Label("Height"), _fixedRectH), ActionButton("Apply", 78, () => ApplyFixedCenterRect())));
        panel.Controls.Add(_fixedRectStatus);
        return panel;
    }

    private TextBox FixedRectTextBox(string text)
    {
        var tb = new TextBox { Text = text, Width = 88, Height = 26, BackColor = Color.White, ForeColor = Color.Black };
        tb.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                ApplyFixedCenterRect();
            }
        };
        return tb;
    }

    private TabPage MakeProcessingTab()
    {
        var page = Page("Processing");
        var root = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, BackColor = Dark };
        page.Controls.Add(root);

        _proc = new ComboBox { Width = 292, Height = 28, DropDownStyle = ComboBoxStyle.DropDownList, BackColor = Color.White, ForeColor = Color.Black };
        _proc.DataSource = Enum.GetValues(typeof(ProcOp));
        _proc.SelectedIndexChanged += (_, _) =>
        {
            PushProcessing();
            RequestRender(300);
        };
        root.Controls.Add(Group("Current", Stack(Label("Operator"), _proc, Row(ProcDisplay("Base", DisplayMode.Base), ProcDisplay("Result", DisplayMode.Processed), ProcDisplay("Blend", DisplayMode.Blend)))));

        _threshold = MakeNum(0, 255, 128, 0);
        _gamma = MakeNum(1, 500, 70, 2);
        _radius = MakeNum(1, 128, 12, 0);

        _threshold.ValueChanged += (_, _) =>
        {
            PushProcessing();
            UpdateRoiAnalysis();
            RequestRender(300);
        };
        _gamma.ValueChanged += (_, _) =>
        {
            PushProcessing();
            RequestRender(300);
        };
        _radius.ValueChanged += (_, _) =>
        {
            PushProcessing();
            RequestRender(300);
        };
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
        root.Controls.Add(Group("Metadata", _metaTable));
        _aoiObjects = new CheckedListBox { Width = 315, Height = 300, CheckOnClick = false, BackColor = Color.White, ForeColor = Color.Black };
        _aoiObjects.MouseDown += (_, e) => HandleOverlayListMouseDown(_aoiObjects, e);
        _aoiObjects.ItemCheck += (_, e) => HandleOverlayItemCheck(_aoiObjects, e);
        _aoiObjects.SelectedIndexChanged += (_, _) => SelectOverlayFromList(_aoiObjects);
        root.Controls.Add(Row(ActionButton("Show All", 94, ShowAllAoi), ActionButton("Hide All", 94, HideAllAoi)));
        root.Controls.Add(Group("Metadata Objects", _aoiObjects));
        return page;
    }

    private TableLayoutPanel MakeMetaTable()
    {
        var keys = new[] { "Source", "Created", "Result", "Objects", "Defects", "Polygons", "Warnings", "Error", "Path", "Recipe" };
        var table = new TableLayoutPanel
        {
            Width = 315,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = keys.Length,
            GrowStyle = TableLayoutPanelGrowStyle.FixedSize,
            BackColor = Dark,
            Padding = new Padding(6)
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 86));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < keys.Length; i++)
        {
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        }

        foreach (var key in keys)
        {
            var lk = new Label
            {
                Text = key,
                AutoSize = true,
                MinimumSize = new Size(82, 24),
                MaximumSize = new Size(82, 0),
                ForeColor = Color.FromArgb(128, 210, 255),
                BackColor = Dark,
                Font = new Font("Segoe UI", 9, FontStyle.Bold),
                TextAlign = ContentAlignment.TopLeft
            };
            var lv = new Label
            {
                Name = "v_" + key,
                Text = "--",
                AutoSize = true,
                MinimumSize = new Size(205, 24),
                MaximumSize = new Size(205, 0),
                ForeColor = Color.White,
                BackColor = Dark,
                Font = new Font("Consolas", 8.6f, FontStyle.Bold),
                AutoEllipsis = false
            };
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
                string value = _metadata.GetValueOrDefault(key, "--");
                l.Text = WrapMetaValue(value);
                l.AccessibleDescription = value;
            }
        }
    }

    private static string WrapMetaValue(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "--";
        return value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Replace("\n", "\r\n", StringComparison.Ordinal);
    }

    private GroupBox Group(string title, Control child)
    {
        var g = new GroupBox { Text = title, Width = 322, Height = child.Height + 34, ForeColor = Color.FromArgb(128, 210, 255), BackColor = Dark, Padding = new Padding(10) };
        child.Left = 10;
        child.Top = 20;
        child.SizeChanged += (_, _) => g.Height = child.Height + 34;
        g.Controls.Add(child);
        return g;
    }

    private FlowLayoutPanel Row(params Control[] controls)
    {
        var p = new FlowLayoutPanel { Width = 302, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Dark, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        foreach (var c in controls)
        {
            c.Margin = new Padding(4, 4, 4, 4);
            p.Controls.Add(c);
        }

        p.Height = controls.Length == 0 ? 36 : controls.Max(c => c.Height + c.Margin.Vertical) + 8;
        return p;
    }

    private FlowLayoutPanel Stack(params Control[] controls)
    {
        int width = controls.Any(c => c.Width > 150 || c is FlowLayoutPanel) ? 302 : 96;
        var p = new FlowLayoutPanel { Width = width, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Dark, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        foreach (var c in controls)
        {
            c.Margin = new Padding(4, 3, 4, 3);
            p.Controls.Add(c);
        }

        p.Height = controls.Sum(c => c.Height + c.Margin.Vertical) + 8;
        return p;
    }

    private Label Label(string text) => new() { Text = text, Width = 90, Height = 18, ForeColor = Color.White, BackColor = Dark };

    private Button FlatButton(string text, int w, int h)
    {
        var b = new Button
        {
            Text = text,
            Width = w,
            Height = h,
            ForeColor = Color.Black,
            BackColor = Color.FromArgb(250, 250, 250),
            FlatStyle = FlatStyle.Flat,
            UseVisualStyleBackColor = false,
            Font = new Font("Segoe UI", 9, FontStyle.Regular)
        };

        b.FlatAppearance.BorderColor = Color.FromArgb(210, 210, 210);
        b.FlatAppearance.MouseOverBackColor = Color.FromArgb(225, 242, 255);
        b.FlatAppearance.MouseDownBackColor = Accent;
        return b;
    }

    private Button ActionButton(string text, int w, Action act)
    {
        var b = FlatButton(text, w, 30);
        b.Click += (_, _) => act();
        return b;
    }

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
        {
            SetToolButtonVisual(kv.Value, kv.Key == _tool);
        }
    }

    private Button ProcButton(string text, ProcOp op)
    {
        var b = FlatButton(text, 68, 30);
        b.Click += (_, _) =>
        {
            _proc.SelectedItem = op;

            if (op == ProcOp.OtsuThreshold && _viewer != IntPtr.Zero)
            {
                int t = NativeMethods.NV_ComputeOtsuThreshold(_viewer);
                _threshold.Value = Math.Max(_threshold.Minimum, Math.Min(_threshold.Maximum, t));
            }

            PushProcessing();
            RequestRender(300);
        };
        return b;
    }

    private Button ProcDisplay(string text, DisplayMode m)
    {
        var b = FlatButton(text, 92, 30);
        b.Click += (_, _) =>
        {
            _display = m;
            PushProcessing();
            RequestRender(300);
        };
        return b;
    }

    private NumericUpDown MakeNum(decimal min, decimal max, decimal val, int decimals) => new() { Minimum = min, Maximum = max, Value = val, DecimalPlaces = decimals, Width = 88, Increment = decimals > 0 ? 1 : 1, BackColor = Color.White, ForeColor = Color.Black };

    private void AddNum(TableLayoutPanel p, string label, NumericUpDown n, int col, int row)
    {
        n.Minimum = -100000000;
        n.Maximum = 100000000;
        n.DecimalPlaces = 3;
        n.Width = 130;
        p.Controls.Add(Label(label), col, row);
        p.Controls.Add(n, col, row + 1);
    }

    #endregion

    #region Render and Native Viewer Lifecycle

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
        _lastRenderReason = RenderReasonIdle;
        if (_renderDirty) { _lastRenderReason = RenderReasonDirty; return true; }
        if (now < _continuousRenderUntilMs) { _lastRenderReason = RenderReasonActive; return true; }
        // Large BMP full-resolution tiles are filled progressively. Keep rendering
        // while the native side still has queued tiles; otherwise high-zoom areas
        // may remain preview-resolution and look non-grid / blurred after zooming.
        if (_viewer != IntPtr.Zero && NativeMethods.NV_GetPendingTileCount(_viewer) > 0) { _lastRenderReason = RenderReasonTile; return true; }
        if (_debug.Checked && now >= _nextDebugRenderMs)
        {
            _nextDebugRenderMs = now + 200; // debug HUD refresh at about 5 FPS when idle
            _lastRenderReason = RenderReasonDebug;
            return true;
        }
        return false;
    }

    private static string RenderReasonName(int reason) => reason switch
    {
        RenderReasonDirty => "DIRTY",
        RenderReasonActive => "ACTIVE",
        RenderReasonTile => "TILE",
        RenderReasonDebug => "DEBUG",
        _ => "IDLE"
    };

    private void EnsureNative()
    {
        if (_viewer != IntPtr.Zero) return;
        _viewport.CreateControl();
        _viewer = NativeMethods.NV_Create(_viewport.Handle);
        if (_viewer == IntPtr.Zero) throw new InvalidOperationException("NativeD3D11Viewer 建立失敗。請確認 DLL 在執行目錄，且顯示卡支援 D3D11。");
        NativeMethods.NV_Resize(_viewer, _viewport.Width, _viewport.Height);
        NativeMethods.NV_SetTileSettings(_viewer, TileUploadBudgetPerFrame, CpuTileCacheLimitMb, GpuTileCacheLimitMb, MaxTileQueueCount);
        SetTool(_tool);
        PushOptions();
        PushProcessing();
        RequestRender(800);
        _renderTimer.Start();
    }

    #endregion

    #region Image Loading and Navigation

    private async Task OpenImageDialogAsync()
    {
        using var dlg = new OpenFileDialog { Filter = "Images|*.bmp;*.png;*.jpg;*.jpeg;*.jfif;*.tif;*.tiff;*.gif;*.wdp;*.jxr;*.webp|All files|*.*" };
        if (dlg.ShowDialog(this) == DialogResult.OK) await LoadImageAsync(dlg.FileName);
    }

    private async Task LoadImageAsync(string file)
    {
        EnsureNative();
        if (_loading) return;
        _loading = true;
        _renderTimer.Stop();
        UseWaitCursor = true;
        _status.Text = "Loading in native C++ worker...\r\nUI remains responsive.";

        try
        {
            var result = await Task.Run(() => NativeMethods.NV_LoadImage(_viewer, file, out _lastInfo));
            if (result == 0)
            {
                var sb = new StringBuilder(2048);
                NativeMethods.NV_GetLastError(_viewer, sb, sb.Capacity);
                MessageBox.Show(sb.ToString(), "Load failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            // Do not block image display on folder scanning. Large AOI folders / network paths
            // can contain thousands of files; scan them after the image is already visible.
            Text = $"AOI D3D11 Native Hybrid Image Viewer - {Path.GetFileName(file)}";
            NativeMethods.NV_FitToWindow(_viewer);
            RequestRender(1500);
            BeginUpdateFolderImageList(file);

            _fixedRectOverlayId = -1;
            _metadataOverlayIds.Clear();
            ParseAoiMetadata(file);

            // Metadata is parsed in managed code and then projected into native overlays.
            // The native viewer remains the single source of truth for geometry editing.
            SeedAoiObjects();
            ApplyFixedCenterRect(refreshList: false);
            RefreshOverlayList();
            UpdateRoiAnalysis();
            RequestRender(1500);
        }
        finally
        {
            _renderTimer.Start();
            UseWaitCursor = false;
            _loading = false;
            int pending = _pendingSiblingDirection;
            _pendingSiblingDirection = 0;
            if (pending != 0 && !IsDisposed)
            {
                BeginInvoke((MethodInvoker)(async () => await LoadSiblingImageAsync(pending)));
            }
        }
    }


    private static readonly HashSet<string> ImageExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".bmp", ".png", ".jpg", ".jpeg", ".jfif", ".tif", ".tiff", ".gif", ".wdp", ".jxr", ".webp"
    };

    private void BeginUpdateFolderImageList(string file)
    {
        long version = ++_folderListVersion;
        string fullFile = Path.GetFullPath(file);
        string? dir = Path.GetDirectoryName(fullFile);

        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
        {
            _folderImages = new List<string> { fullFile };
            _folderImageIndex = 0;
            Text = $"AOI D3D11 Native Hybrid Image Viewer - {Path.GetFileName(fullFile)}  ({CurrentFolderIndexText()})";
            return;
        }

        // Folder scanning can be very slow on AOI folders / network drives. Do it after the image is visible.
        _ = Task.Run(() => BuildFolderImageList(fullFile))
            .ContinueWith(t =>
            {
                if (IsDisposed || version != _folderListVersion || t.IsFaulted || t.IsCanceled) return;
                try
                {
                    BeginInvoke((MethodInvoker)(() =>
                    {
                        if (version != _folderListVersion) return;
                        _folderImages = t.Result.Images;
                        _folderImageIndex = t.Result.Index;
                        Text = $"AOI D3D11 Native Hybrid Image Viewer - {Path.GetFileName(fullFile)}  ({CurrentFolderIndexText()})";
                    }));
                }
                catch (InvalidOperationException) { }
            });
    }

    private void UpdateFolderImageList(string file)
    {
        var result = BuildFolderImageList(Path.GetFullPath(file));
        _folderImages = result.Images;
        _folderImageIndex = result.Index;
    }

    private static (List<string> Images, int Index) BuildFolderImageList(string fullFile)
    {
        string? dir = Path.GetDirectoryName(fullFile);
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
            return (new List<string> { fullFile }, 0);

        var images = Directory.EnumerateFiles(dir)
            .Where(p => ImageExts.Contains(Path.GetExtension(p)))
            .OrderBy(p => Path.GetFileName(p), StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        int index = images.FindIndex(p => string.Equals(Path.GetFullPath(p), fullFile, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            images.Add(fullFile);
            images = images.OrderBy(p => Path.GetFileName(p), StringComparer.CurrentCultureIgnoreCase).ToList();
            index = images.FindIndex(p => string.Equals(Path.GetFullPath(p), fullFile, StringComparison.OrdinalIgnoreCase));
        }

        return (images, index);
    }

    private string CurrentFolderIndexText()
    {
        return _folderImages.Count > 0 && _folderImageIndex >= 0
            ? $"{_folderImageIndex + 1}/{_folderImages.Count}"
            : "--/--";
    }

    private async Task LoadSiblingImageAsync(int direction)
    {
        if (direction == 0) return;

        if (_loading)
        {
            _pendingSiblingDirection = Math.Sign(direction);
            return;
        }

        // Collapse repeated key-auto-repeat bursts into one request. This avoids stacking
        // native image loads when the user holds Left/Right or clicks Prev/Next very fast.
        var now = DateTime.UtcNow;
        if ((now - _lastSiblingRequestUtc).TotalMilliseconds < 45) return;
        _lastSiblingRequestUtc = now;

        if (_folderImages.Count == 0 || _folderImageIndex < 0)
        {
            if (_lastInfo.width > 0 && !string.IsNullOrWhiteSpace(_lastInfo.path))
                UpdateFolderImageList(_lastInfo.path);
            else
                return;
        }
        if (_folderImages.Count <= 1) return;

        int next = (_folderImageIndex + direction + _folderImages.Count) % _folderImages.Count;
        _folderImageIndex = next;
        await LoadImageAsync(_folderImages[next]);
    }

    #endregion

    #region Render Loop

    private void TickRender()
    {
        if (_loading || _viewer == IntPtr.Zero) return;
        if (!ShouldRenderFrame()) return;

        bool stillContinuous = NowMs() < _continuousRenderUntilMs;
        _renderDirty = false;

        NativeMethods.NV_SetRenderReason(_viewer, _lastRenderReason);
        NativeMethods.NV_Render(_viewer);
        SyncSelectedOverlayFromNative(false);
        NativeMethods.NV_GetDiagnostics(_viewer, out var d);

        if (_debug.Checked)
        {
            _status.Text =
                $"FPS {d.fps:F1}   Render {d.renderMs:F2} ms   Reason {RenderReasonName(_lastRenderReason)}\r\n" +
                $"Scale {d.scale:F3}  Dirty {(_renderDirty ? 1 : 0)}  Active {(stillContinuous ? 1 : 0)}\r\n" +
                $"Tile worker {d.pendingWorkerRequests}  Ready {d.readyToUploadTiles}  Up/frame {d.uploadedTilesLastFrame}/{d.maxTileUploadPerFrame}\r\n" +
                $"CPU tiles {d.cpuCachedTiles}/{d.cpuCacheLimitMb}MB  GPU tiles {d.gpuResidentTiles}/{d.gpuCacheLimitMb}MB\r\n" +
                $"DupReq {d.duplicateTileRequests}  Stale {d.staleTileDiscards}  Overlay {d.overlayCount}";
        }
    }

    #endregion

    #region Overlay Lists and Selection

    private void UpdateMouseInfo(int x, int y)
    {
        if (NativeMethods.NV_GetMouseImageInfo(_viewer, x, y, out int ix, out int iy, out int gray) != 0)
        {
            _mouse.Text = _gray.Checked ? $"X: {ix} , Y: {iy} , Gray: {gray}" : $"X: {ix} , Y: {iy}";
        }
        else
        {
            _mouse.Text = "X: --- , Y: --- , Gray: ---";
        }
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
            string displayName = _metadataObjectListNames.GetValueOrDefault(o.id, o.name);
            var row = new OverlayRow(o.id, displayName, (OverlayType)o.type, o);
            if (IsMetadataObject(row))
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

        // ItemCheck is fired before CheckedListBox commits the new check state.
        // Defer the native visibility change until the UI state is committed,
        // then explicitly dirty one frame. Dirty render must still repaint when
        // scene content changes, even if the mouse is idle.
        BeginInvoke(new Action(() =>
        {
            if (_viewer == IntPtr.Zero) return;
            if (e.Index < 0 || e.Index >= list.Items.Count) return;
            if (list.Items[e.Index] is not OverlayRow r) return;

            NativeMethods.NV_SetOverlayVisible(_viewer, r.Id, e.NewValue == CheckState.Checked ? 1 : 0);
            UpdateRoiAnalysis();
            RequestRender(300);
        }));
    }

    private void SelectOverlayFromList(CheckedListBox list)
    {
        if (_refreshingList || _viewer == IntPtr.Zero) return;
        if (list.SelectedItem is not OverlayRow r) return;
        _selectedOverlayId = r.Id;
        NativeMethods.NV_SelectOverlay(_viewer, r.Id);
        if (!IsFixedRect(r.Type)) LoadGeometryFrom(r.Info);
        UpdateRoiAnalysis();
        RequestRender(200);
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

    private static bool IsAoi(OverlayType t) =>
        t is OverlayType.AoiDieCenter
          or OverlayType.AoiDieCorner
          or OverlayType.AoiDefect;

    private bool IsMetadataObject(OverlayRow row) =>
        IsAoi(row.Type) || _metadataOverlayIds.Contains(row.Id);

    private static bool IsFixedRect(OverlayType t) => t == OverlayType.FixedCenterRect;

    #endregion

    #region Metadata and AOI Overlays

    private void UpdateRoiAnalysis()
    {
        if (_viewer == IntPtr.Zero) return;
        var hist = new int[256];
        var px = new double[4096];
        var py = new double[4096];

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
        _genericRects.Clear();
        _drawPolygons.Clear();
        _diePos = null;
        _metadataObjectListNames.Clear();

        ViewerMetadataLoader.TryLoad(file, out var meta);
        foreach (var kv in meta.Properties)
            _metadata[kv.Key] = kv.Value;
        _metadata["Path"] = file;
        _metadata["Source"] = meta.SourceKind;

        int defectIndex = 1;
        int polygonIndex = 1;
        foreach (var overlay in meta.Overlays)
        {
            if (overlay.Kind == ViewerOverlayKind.Rectangle && overlay.Rect is { } rect)
            {
                var r = new AoiRect(rect.X, rect.Y, rect.W, rect.H);
                if (overlay.Category.Equals("die", StringComparison.OrdinalIgnoreCase) ||
                    overlay.Name.Contains("die", StringComparison.OrdinalIgnoreCase))
                {
                    _diePos ??= r;
                    continue;
                }

                if (overlay.Category.Equals("defect", StringComparison.OrdinalIgnoreCase) ||
                    overlay.Name.Contains("defect", StringComparison.OrdinalIgnoreCase))
                {
                    double wu = ParseMetaDouble(overlay.Attributes.GetValueOrDefault("WidthUm", "0"));
                    double hu = ParseMetaDouble(overlay.Attributes.GetValueOrDefault("HeightUm", "0"));
                    string bin = overlay.Attributes.GetValueOrDefault("BinCode", overlay.Attributes.GetValueOrDefault("bin", "--"));
                    string algo = overlay.Attributes.GetValueOrDefault("AlgoIds", overlay.Attributes.GetValueOrDefault("AlgoID", "--"));
                    _aoiDefects.Add(new AoiDefectRec(defectIndex++, r, wu, hu, bin, algo));
                }
                else
                {
                    string name = string.IsNullOrWhiteSpace(overlay.Name) ? $"Rectangle {_genericRects.Count + 1}" : overlay.Name;
                    string category = string.IsNullOrWhiteSpace(overlay.Category) ? "metadata" : overlay.Category;
                    _genericRects.Add(new GenericRectRec(_genericRects.Count + 1, r, name, category));
                }
            }
            else if (overlay.Kind == ViewerOverlayKind.Polygon && overlay.Points.Count >= 3)
            {
                int index = polygonIndex++;
                string name = string.IsNullOrWhiteSpace(overlay.Name) ? $"Polygon {index}" : overlay.Name;
                string result = overlay.Attributes.GetValueOrDefault("Result", _metadata.GetValueOrDefault("Result", ""));
                string bin = overlay.Attributes.GetValueOrDefault("BinCode", overlay.Attributes.GetValueOrDefault("bin", ""));
                string algo = overlay.Attributes.GetValueOrDefault(
                    "AlgoIds",
                    overlay.Attributes.GetValueOrDefault("AlgoID", overlay.Attributes.GetValueOrDefault("AlgoId", "")));
                _drawPolygons.Add(new DrawPolygonRec(
                    index,
                    name,
                    result,
                    bin,
                    algo,
                    overlay.Points.Select(p => new AoiPoint(p.X, p.Y)).ToList()));
            }
            else if ((overlay.Kind is ViewerOverlayKind.Point or ViewerOverlayKind.Crosshair) && overlay.Points.Count > 0)
            {
                var pt = overlay.Points[0];
                if (overlay.Category.Equals("die-center", StringComparison.OrdinalIgnoreCase) ||
                    overlay.Name.Contains("center", StringComparison.OrdinalIgnoreCase))
                {
                    _metadata["DieCenterX"] = pt.X.ToString(CultureInfo.InvariantCulture);
                    _metadata["DieCenterY"] = pt.Y.ToString(CultureInfo.InvariantCulture);
                }
            }
        }

        _metadata["Defects"] = _aoiDefects.Count.ToString(CultureInfo.InvariantCulture);
        _metadata["Polygons"] = _drawPolygons.Count.ToString(CultureInfo.InvariantCulture);
        if (!_metadata.ContainsKey("Created")) _metadata["Created"] = "--";
        if (!_metadata.ContainsKey("Result")) _metadata["Result"] = "--";
        if (!_metadata.ContainsKey("Recipe")) _metadata["Recipe"] = "--";
        if (!_metadata.ContainsKey("Error")) _metadata["Error"] = "--";

        var sb = new StringBuilder();
        AppendMeta(sb, "Source");
        AppendMeta(sb, "Created");
        AppendMeta(sb, "Result");
        AppendMeta(sb, "Objects");
        AppendMeta(sb, "Defects");
        AppendMeta(sb, "Polygons");
        AppendMeta(sb, "Warnings");
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

    private static double ParseMetaDouble(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;

    private void SeedAoiObjects()
    {
        if (_lastInfo.width <= 0 || _viewer == IntPtr.Zero) return;
        int id;

        // Convert parsed metadata records into read-only native overlays so all
        // drawing, hit-testing, and visibility toggles still use one renderer path.
        if (_metadata.TryGetValue("DieCenterX", out var sx) && _metadata.TryGetValue("DieCenterY", out var sy) && float.TryParse(sx, NumberStyles.Float, CultureInfo.InvariantCulture, out var cx) && float.TryParse(sy, NumberStyles.Float, CultureInfo.InvariantCulture, out var cy))
        {
            id = NativeMethods.NV_AddOverlay(_viewer, (int)OverlayType.AoiDieCenter, cx, cy, MathF.Max(8, MathF.Min(_lastInfo.width, _lastInfo.height) * 0.015f), 0, 0, "Die Center");
            if (id > 0) _metadataOverlayIds.Add(id);
        }
        if (_diePos is { } die && die.W > 0 && die.H > 0)
        {
            id = NativeMethods.NV_AddOverlay(_viewer, (int)OverlayType.AoiDieCorner, (float)die.X, (float)die.Y, (float)die.W, (float)die.H, 0, "Die Position");
            if (id > 0) _metadataOverlayIds.Add(id);
        }
        foreach (var r in _genericRects)
        {
            id = NativeMethods.NV_AddOverlay(_viewer, (int)OverlayType.Roi, (float)r.Rect.X, (float)r.Rect.Y, (float)r.Rect.W, (float)r.Rect.H, 0, $"{r.Name}  [{r.Category}]");
            if (id > 0) _metadataOverlayIds.Add(id);
        }
        foreach (var d in _aoiDefects)
        {
            string listName = BuildMetadataObjectListName(
                $"Defect {d.Index}",
                _metadata.GetValueOrDefault("Result", ""),
                d.Bin,
                d.AlgoIds);
            id = NativeMethods.NV_AddOverlay(_viewer, (int)OverlayType.AoiDefect, (float)d.Rect.X, (float)d.Rect.Y, (float)d.Rect.W, (float)d.Rect.H, 0, listName);
            if (id > 0) _metadataOverlayIds.Add(id);
        }
        foreach (var poly in _drawPolygons)
        {
            var xy = new float[poly.Points.Count * 2];
            for (int i = 0; i < poly.Points.Count; i++)
            {
                xy[i * 2] = (float)poly.Points[i].X;
                xy[i * 2 + 1] = (float)poly.Points[i].Y;
            }
            id = NativeMethods.NV_AddPolygonOverlay(_viewer, (int)OverlayType.DrawPolygon, xy, poly.Points.Count, poly.Name);
            if (id > 0)
            {
                _metadataOverlayIds.Add(id);
                _metadataObjectListNames[id] = BuildMetadataObjectListName(poly.Name, poly.Result, poly.Bin, poly.AlgoIds);
            }
        }
    }

    private static string BuildMetadataObjectListName(string name, string result, string bin, string algoIds)
    {
        var parts = new List<string> { name };
        if (HasMetadataDisplayValue(result)) parts.Add($"Result {result}");
        if (HasMetadataDisplayValue(bin)) parts.Add($"Bin {bin}");
        if (HasMetadataDisplayValue(algoIds)) parts.Add($"AlgoID {algoIds}");
        return string.Join("  ", parts);
    }

    private static bool HasMetadataDisplayValue(string value) =>
        !string.IsNullOrWhiteSpace(value) && value != "--";

    #endregion

    #region Overlay Editing

    private void ApplyFixedCenterRect() => ApplyFixedCenterRect(refreshList: true);

    private void ApplyFixedCenterRect(bool refreshList)
    {
        if (_viewer == IntPtr.Zero)
        {
            if (_fixedRectEnabled.Checked) EnsureNative();
            else return;
        }

        if (_lastInfo.width <= 0 || _lastInfo.height <= 0)
        {
            _fixedRectStatus.Text = "Load an image first.";
            return;
        }

        if (!_fixedRectEnabled.Checked)
        {
            if (_fixedRectOverlayId > 0)
            {
                NativeMethods.NV_SetOverlayVisible(_viewer, _fixedRectOverlayId, 0);
            }

            _fixedRectStatus.Text = "Fixed rect hidden.";
            if (refreshList)
            {
                RefreshOverlayList();
            }

            RequestRender(300);
            return;
        }

        if (!TryReadPositiveFloat(_fixedRectW.Text, out float w) || !TryReadPositiveFloat(_fixedRectH.Text, out float h))
        {
            _fixedRectStatus.Text = "Width / Height must be positive numbers.";
            return;
        }

        w = MathF.Min(w, MathF.Max(1, _lastInfo.width));
        h = MathF.Min(h, MathF.Max(1, _lastInfo.height));
        float x = (_lastInfo.width - w) * 0.5f;
        float y = (_lastInfo.height - h) * 0.5f;

        if (_fixedRectOverlayId <= 0 || !OverlayExists(_fixedRectOverlayId))
        {
            _fixedRectOverlayId = NativeMethods.NV_AddOverlay(_viewer, (int)OverlayType.FixedCenterRect, x, y, w, h, 0, "Fixed Center Rect");
        }
        else
        {
            NativeMethods.NV_UpdateOverlay(_viewer, _fixedRectOverlayId, x, y, w, h, 0);
            NativeMethods.NV_SetOverlayVisible(_viewer, _fixedRectOverlayId, 1);
        }

        _fixedRectStatus.Text = $"Center: ({_lastInfo.width / 2.0:F1}, {_lastInfo.height / 2.0:F1})  Size: {w:F1} x {h:F1}";
        if (refreshList) RefreshOverlayList();
        RequestRender(300);
    }

    private bool OverlayExists(int id)
    {
        if (_viewer == IntPtr.Zero || id <= 0) return false;
        int n = NativeMethods.NV_GetOverlayCount(_viewer);
        for (int i = 0; i < n; i++)
        {
            if (NativeMethods.NV_GetOverlayInfo(_viewer, i, out var o) != 0 && o.id == id) return true;
        }
        return false;
    }

    private static bool TryReadPositiveFloat(string text, out float value)
    {
        text = (text ?? string.Empty).Trim();
        if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
            !float.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value))
        {
            value = 0;
            return false;
        }
        return value > 0 && !float.IsNaN(value) && !float.IsInfinity(value);
    }

    private void LoadGeometryFrom(NVOverlayInfo o)
    {
        _geoX.Value = SafeDec(o.x1);
        _geoY.Value = SafeDec(o.y1);
        _geoW.Value = SafeDec(Math.Abs(o.x2 - o.x1));
        _geoH.Value = SafeDec(Math.Abs(o.y2 - o.y1));
        _geoA.Value = SafeDec(o.angle);
    }

    private static decimal SafeDec(float f) => Math.Max(-100000000, Math.Min(100000000, (decimal)f));

    private void ApplyGeometry()
    {
        if (_viewer == IntPtr.Zero || _overlays.SelectedItem is not OverlayRow r || IsMetadataObject(r) || IsFixedRect(r.Type)) return;
        NativeMethods.NV_UpdateOverlay(_viewer, r.Id, (float)_geoX.Value, (float)_geoY.Value, (float)_geoW.Value, (float)_geoH.Value, (float)_geoA.Value);
        _selectedOverlayId = r.Id;
        SyncSelectedOverlayFromNative(true);
        RefreshOverlayList();
        UpdateRoiAnalysis();
        RequestRender(300);
    }

    private void DeleteSelected()
    {
        if (_viewer == IntPtr.Zero || _overlays.SelectedItem is not OverlayRow r || IsMetadataObject(r) || IsFixedRect(r.Type)) return;
        NativeMethods.NV_DeleteOverlay(_viewer, r.Id);
        RefreshOverlayList();
        UpdateRoiAnalysis();
        RequestRender(300);
    }

    private void ClearAll()
    {
        if (_viewer == IntPtr.Zero) return;
        NativeMethods.NV_ClearOverlays(_viewer);
        RefreshOverlayList();
        UpdateRoiAnalysis();
        RequestRender(300);
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

    #endregion

    #region Export and Keyboard

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

    private static bool IsTextInputControl(Control? c)
    {
        while (c != null)
        {
            if (c is TextBoxBase) return true;
            if (c is NumericUpDown) return true;
            if (c is ComboBox cb && cb.DropDownStyle != ComboBoxStyle.DropDownList) return true;
            c = c.Parent;
        }
        return false;
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        Keys key = keyData & Keys.KeyCode;

        if (IsTextInputControl(ActiveControl))
            return base.ProcessCmdKey(ref msg, keyData);

        if (key == Keys.Left)
        {
            _ = LoadSiblingImageAsync(-1);
            return true;
        }

        if (key == Keys.Right)
        {
            _ = LoadSiblingImageAsync(1);
            return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    private async void MainForm_KeyDown(object? sender, KeyEventArgs e)
    {
        if (_loading) return;

        if (e.KeyCode == Keys.V) SetTool(ToolMode.Pan);
        else if (e.KeyCode == Keys.Z) SetTool(ToolMode.ZoomRect);
        else if (e.KeyCode == Keys.P) SetTool(ToolMode.Pixel);
        else if (e.KeyCode == Keys.R) SetTool(ToolMode.Roi);
        else if (e.KeyCode == Keys.M) SetTool(ToolMode.Measure);
        else if (e.KeyCode == Keys.C) SetTool(ToolMode.Circle);
        else if (e.KeyCode == Keys.D)
        {
            _debug.Checked = !_debug.Checked;
            PushOptions();
            RequestRender(600);
        }
        else if (e.KeyCode == Keys.F)
        {
            NativeMethods.NV_FitToWindow(_viewer);
            RequestRender(600);
        }
        else if (e.KeyCode == Keys.D1)
        {
            NativeMethods.NV_OneToOne(_viewer);
            RequestRender(600);
        }
        else if (e.KeyCode == Keys.PageDown) await LoadSiblingImageAsync(1);
        else if (e.KeyCode == Keys.PageUp) await LoadSiblingImageAsync(-1);
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
        if (_viewer != IntPtr.Zero)
        {
            NativeMethods.NV_Destroy(_viewer);
            _viewer = IntPtr.Zero;
        }
        base.OnFormClosed(e);
    }

    #endregion

    #region Nested Types

    private sealed record OverlayRow(int Id, string Name, OverlayType Type, NVOverlayInfo Info)
    {
        public override string ToString() => Name;
    }

    private readonly record struct AoiRect(double X, double Y, double W, double H);
    private readonly record struct AoiPoint(double X, double Y);
    private sealed record AoiDefectRec(int Index, AoiRect Rect, double WidthUm, double HeightUm, string Bin, string AlgoIds);
    private sealed record GenericRectRec(int Index, AoiRect Rect, string Name, string Category);
    private sealed record DrawPolygonRec(int Index, string Name, string Result, string Bin, string AlgoIds, List<AoiPoint> Points);

    #endregion
}
