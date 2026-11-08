using System.Drawing;
using System.Globalization;
using System.IO;
using System.Windows.Forms;
using RobotStudioOffline.Core;

namespace RobotStudioOffline.Addin;

public sealed record BridgeUiState(bool Listening, string Endpoint, string? StationName, string? LastError, bool Stopping = false);

/// <summary>SDK-free bridge status view; the host supplies lifecycle and station observations.</summary>
public sealed class BridgePanel : UserControl
{
    private readonly Func<BridgeUiState> getState;
    private readonly Action startBridge;
    private readonly Action stopBridge;
    private readonly BridgeActivity activity;
    private readonly System.Windows.Forms.Timer poll = new() { Interval = 1000 };
    private readonly Label status = new() { AutoSize = true, Text = "Starting", Margin = new(0, 3, 0, 6) };
    private readonly TextBox endpoint = ReadOnlyBox();
    private readonly TextBox station = ReadOnlyBox();
    private readonly Label counts = WrapLabel();
    private readonly Label lastRequest = WrapLabel();
    private readonly Label message = WrapLabel();
    private readonly Button start = ActionButton("Start bridge");
    private readonly Button stop = ActionButton("Stop bridge");
    private readonly DataGridView history = ReadOnlyGrid();
    private readonly TextBox wrapperPath = ReadOnlyBox();
    private readonly TextBox setupCommand = new() { ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Height = 100, WordWrap = true };
    private readonly Label setupMessage = WrapLabel();
    private readonly List<Font> ownedFonts = [];
    private string historyKey = string.Empty;
    private string? actionError;

    public BridgePanel(Func<BridgeUiState> getState, Action startBridge, Action stopBridge, BridgeActivity activity)
    {
        this.getState = getState;
        this.startBridge = startBridge;
        this.stopBridge = stopBridge;
        this.activity = activity;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96, 96);
        Font = SystemFonts.MessageBoxFont;
        MinimumSize = new Size(420, 500);
        Size = new Size(480, 720);
        Dock = DockStyle.Fill;
        Padding = new Padding(12);
        AutoScroll = true;
        BackColor = SystemColors.Control;
        ForeColor = SystemColors.ControlText;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1, RowCount = 9, Margin = Padding.Empty
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var row = 0; row < 8; row++) layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var title = new Label { Text = "Offline Station Bridge", AutoSize = true, Font = OwnedFont(Font.FontFamily, 13, FontStyle.Bold), Margin = new(0, 0, 0, 4) };
        status.Font = OwnedFont(Font.FontFamily, Font.Size, FontStyle.Bold);
        layout.Controls.Add(title, 0, 0);
        layout.Controls.Add(status, 0, 1);
        layout.Controls.Add(LabeledFields(), 0, 2);

        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true, Margin = new(0, 4, 0, 6) };
        var refresh = ActionButton("Refresh");
        actions.Controls.AddRange([start, stop, refresh]);
        start.Click += (_, _) => RunLifecycle(startBridge);
        stop.Click += (_, _) => RunLifecycle(stopBridge);
        refresh.Click += (_, _) => RefreshState();
        layout.Controls.Add(actions, 0, 3);
        layout.Controls.Add(counts, 0, 4);
        layout.Controls.Add(lastRequest, 0, 5);
        var warning = WrapLabel("Stopping the bridge does not stop simulation or an operation already running.");
        warning.Margin = new(0, 4, 0, 6);
        layout.Controls.Add(warning, 0, 6);
        message.Visible = false;
        layout.Controls.Add(message, 0, 7);

        var tabs = new TabControl { Dock = DockStyle.Fill, Margin = Padding.Empty, MinimumSize = new Size(0, 280) };
        tabs.TabPages.Add(BuildActivityPage());
        tabs.TabPages.Add(BuildToolsPage());
        tabs.TabPages.Add(BuildSetupPage());
        layout.Controls.Add(tabs, 0, 8);
        Controls.Add(layout);
        SizeChanged += (_, _) => layout.MinimumSize = new Size(0, System.Math.Max(0, ClientSize.Height - Padding.Vertical));
        layout.MinimumSize = new Size(0, System.Math.Max(0, ClientSize.Height - Padding.Vertical));
        poll.Tick += (_, _) => RefreshState();
        VisibleChanged += (_, _) =>
        {
            poll.Enabled = Visible && !IsDisposed;
            if (Visible) RefreshState();
        };
        poll.Start();
        RefreshState();
    }

    private Control LabeledFields()
    {
        var table = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Dock = DockStyle.Fill, Margin = Padding.Empty };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        table.Controls.Add(new Label { Text = "Endpoint", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new(0, 3, 8, 3) }, 0, 0);
        table.Controls.Add(endpoint, 1, 0);
        table.Controls.Add(new Label { Text = "Station", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new(0, 3, 8, 3) }, 0, 1);
        table.Controls.Add(station, 1, 1);
        return table;
    }

    private TabPage BuildActivityPage()
    {
        var page = new TabPage("Activity") { Padding = new Padding(8) };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(WrapLabel("Recent requests · metadata only. Pending includes queued requests."), 0, 0);
        history.Columns.Add(new DataGridViewTextBoxColumn { Name = "Time", HeaderText = "Time", FillWeight = 20, MinimumWidth = 52 });
        history.Columns.Add(new DataGridViewTextBoxColumn { Name = "Operation", HeaderText = "Tool", FillWeight = 35, MinimumWidth = 90 });
        history.Columns.Add(new DataGridViewTextBoxColumn { Name = "Result", HeaderText = "Result", FillWeight = 29, MinimumWidth = 64 });
        history.Columns.Add(new DataGridViewTextBoxColumn { Name = "Duration", HeaderText = "ms", ToolTipText = "Elapsed request time in milliseconds", FillWeight = 16, MinimumWidth = 50 });
        layout.Controls.Add(history, 0, 1);
        var clear = ActionButton("Clear history");
        clear.Click += (_, _) => { activity.ClearHistory(); RefreshState(); };
        var footer = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        clear.Anchor = AnchorStyles.Left;
        footer.Controls.Add(clear, 0, 0);
        footer.Controls.Add(WrapLabel("Lifetime counts and pending requests are kept."), 0, 1);
        layout.Controls.Add(footer, 0, 2);
        page.Controls.Add(layout);
        return page;
    }

    private TabPage BuildToolsPage()
    {
        var page = new TabPage($"Tools ({ToolCatalog.Length})") { Padding = new Padding(8) };
        var grid = ReadOnlyGrid();
        grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells;
        grid.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Tool", FillWeight = 38, MinimumWidth = 130 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Description", FillWeight = 62, MinimumWidth = 170 });
        foreach (var group in ToolCatalog.GroupBy(tool => tool.Group))
        {
            var groupRow = grid.Rows[grid.Rows.Add(group.Key, string.Empty)];
            groupRow.DefaultCellStyle.Font = OwnedFont(Font.FontFamily, Font.Size, FontStyle.Bold);
            foreach (var tool in group) grid.Rows.Add(tool.Name, tool.Description);
        }
        grid.ClearSelection();
        page.Controls.Add(grid);
        return page;
    }

    private TabPage BuildSetupPage()
    {
        var page = new TabPage("MCP setup") { Padding = new Padding(8), AutoScroll = true };
        var table = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1 };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        table.Controls.Add(WrapLabel("Register the local stdio wrapper with Claude Code. Choose the built wrapper DLL, then copy the PowerShell command."));
        table.Controls.Add(wrapperPath);
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true };
        var browse = ActionButton("Browse wrapper…");
        var copy = ActionButton("Copy MCP setup");
        browse.Click += (_, _) => ChooseWrapper();
        copy.Click += (_, _) =>
        {
            if (string.IsNullOrEmpty(wrapperPath.Text) && !ChooseWrapper()) return;
            try
            {
                Clipboard.SetText(BuildMcpCommand(wrapperPath.Text));
                setupMessage.Text = "PowerShell command copied. Paste it into your terminal when ready.";
            }
            catch (Exception exception) { setupMessage.Text = "Could not copy the command: " + exception.Message; }
        };
        buttons.Controls.AddRange([browse, copy]);
        table.Controls.Add(buttons);
        table.Controls.Add(new Label { Text = "PowerShell command", AutoSize = true, Margin = new(0, 8, 0, 4) });
        table.Controls.Add(setupCommand);
        table.Controls.Add(setupMessage);
        table.Controls.Add(WrapLabel("This copies text only. It does not execute a command or copy the bridge token. Requests use the local token file automatically."));
        page.Controls.Add(table);
        return page;
    }

    private bool ChooseWrapper()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Choose the RobotStudio MCP wrapper",
            Filter = "RobotStudio MCP wrapper (RobotStudioOffline.Mcp.dll)|RobotStudioOffline.Mcp.dll",
            FileName = "RobotStudioOffline.Mcp.dll", CheckFileExists = true, Multiselect = false, RestoreDirectory = true
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return false;
        if (!string.Equals(Path.GetFileName(dialog.FileName), "RobotStudioOffline.Mcp.dll", StringComparison.OrdinalIgnoreCase))
        {
            setupMessage.Text = "Choose RobotStudioOffline.Mcp.dll from the built MCP folder.";
            return false;
        }
        wrapperPath.Text = dialog.FileName;
        setupCommand.Text = BuildMcpCommand(dialog.FileName);
        setupMessage.Text = string.Empty;
        return true;
    }

    public static string BuildMcpCommand(string wrapperDll) =>
        "claude mcp add --transport stdio --scope local robotstudio-offline -- dotnet '" + wrapperDll.Replace("'", "''", StringComparison.Ordinal) + "'";

    private void RunLifecycle(Action action)
    {
        actionError = null;
        try { action(); }
        catch (Exception exception) { actionError = exception.Message; }
        RefreshState();
    }

    public void RefreshState()
    {
        if (IsDisposed || Disposing) return;
        try
        {
            var state = getState();
            var snapshot = activity.Snapshot();
            status.Text = state.Stopping ? "Stopping · waiting for pending requests" : state.Listening ? "Listening · localhost only" : state.LastError is null ? "Stopped" : "Stopped · error";
            endpoint.Text = state.Endpoint;
            station.Text = state.StationName ?? "No station open";
            start.Enabled = !state.Listening && !state.Stopping && snapshot.Pending == 0;
            stop.Enabled = state.Listening && !state.Stopping;
            counts.Text = $"{snapshot.Total} requests  ·  {snapshot.Completed} completed  ·  {snapshot.Failed} failed  ·  {snapshot.Pending} pending";
            var last = snapshot.Entries.FirstOrDefault();
            lastRequest.Text = last is null ? "No request history yet." : $"Latest: {last.Operation} · {last.State} · {last.StartUtc.ToLocalTime():HH:mm:ss}";
            var error = actionError ?? state.LastError;
            message.Text = error is null ? string.Empty : "Error: " + error;
            message.Visible = error is not null;

            var key = string.Join("|", snapshot.Entries.Select(entry => $"{entry.Id}:{entry.State}:{entry.EndUtc?.Ticks}:{entry.ErrorCode}"));
            if (key != historyKey)
            {
                historyKey = key;
                var scroll = history.FirstDisplayedScrollingRowIndex;
                history.Rows.Clear();
                foreach (var entry in snapshot.Entries)
                {
                    var index = history.Rows.Add(entry.StartUtc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture), entry.Operation,
                        entry.ErrorCode is null ? entry.State.ToString() : "Failed: " + entry.ErrorCode,
                        entry.ElapsedMs?.ToString("0", CultureInfo.InvariantCulture) ?? "—");
                    history.Rows[index].Cells[2].ToolTipText = entry.ErrorCode ?? entry.State.ToString();
                }
                history.ClearSelection();
                if (scroll >= 0 && scroll < history.Rows.Count) history.FirstDisplayedScrollingRowIndex = scroll;
            }
        }
        catch (Exception exception)
        {
            status.Text = "Status unavailable";
            message.Text = "Could not refresh bridge status: " + exception.Message;
            message.Visible = true;
        }
    }

    public void ApplyTheme(Color background, Color foreground, Color surface)
    {
        Apply(this);
        void Apply(Control control)
        {
            control.ForeColor = foreground;
            control.BackColor = control is TextBox or DataGridView ? surface : background;
            if (control is DataGridView grid)
            {
                grid.BackgroundColor = surface;
                grid.DefaultCellStyle.BackColor = surface;
                grid.DefaultCellStyle.ForeColor = foreground;
                grid.DefaultCellStyle.SelectionBackColor = SystemColors.Highlight;
                grid.DefaultCellStyle.SelectionForeColor = SystemColors.HighlightText;
                grid.EnableHeadersVisualStyles = false;
                grid.ColumnHeadersDefaultCellStyle.BackColor = background;
                grid.ColumnHeadersDefaultCellStyle.ForeColor = foreground;
                grid.GridColor = SystemColors.ControlDark;
            }
            foreach (Control child in control.Controls) Apply(child);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            poll.Stop();
            poll.Dispose();
            foreach (var font in ownedFonts) font.Dispose();
        }
        base.Dispose(disposing);
    }

    private Font OwnedFont(FontFamily family, float size, FontStyle style)
    {
        var font = new Font(family, size, style);
        ownedFonts.Add(font);
        return font;
    }
    private static TextBox ReadOnlyBox() => new() { ReadOnly = true, Dock = DockStyle.Fill, Margin = new(0, 2, 0, 2) };
    private static Label WrapLabel(string text = "") => new() { Text = text, AutoSize = true, Dock = DockStyle.Fill, Margin = new(0, 2, 0, 4), MaximumSize = new Size(10000, 0) };
    private static Button ActionButton(string text) => new() { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(8, 3, 8, 3), Margin = new(0, 2, 6, 2), UseVisualStyleBackColor = true };
    private static DataGridView ReadOnlyGrid() => new()
    {
        ReadOnly = true, Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        AllowUserToOrderColumns = false, AllowUserToResizeRows = false, RowHeadersVisible = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        MultiSelect = false, BackgroundColor = SystemColors.Window, BorderStyle = BorderStyle.FixedSingle,
        CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal, ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize
    };

    private static readonly (string Group, string Name, string Description)[] ToolCatalog =
    [
        ("Station and motion", "station", "Inspect robots, tools, work objects, targets and paths."),
        ("Station and motion", "robot_pose", "Read joints and TCP in the requested tool and work object."),
        ("Station and motion", "reach", "Check a target's kinematic reach and configuration."),
        ("Station and motion", "jump", "Place the station robot at a target or joint pose."),
        ("Station and motion", "collisions", "Inspect collision sets or check one temporary pose."),
        ("Station and motion", "screenshot", "Capture the active station graphics view."),
        ("Development", "path_details", "Read full station instruction arguments and resolved data."),
        ("Development", "validate_targets", "Check a batch of targets; optional single-pose collision checks."),
        ("Development", "preview_targets", "Show or clear temporary target frames and connecting lines."),
        ("Development", "edit_targets", "Create or edit station targets in one undoable step."),
        ("Development", "data_compare", "Compare selected station data with its local virtual controller."),
        ("Development", "data_sync", "Synchronize explicitly selected data in a chosen direction."),
        ("Geometry", "geometry_create", "Create and place a box, cylinder or sphere in the station."),
        ("Geometry", "geometry_import", "Import and place a local SAT, STEP or STL file in the station."),
        ("Simulation and I/O", "simulation", "Read simulation state; start, stop or restore its initial state."),
        ("Simulation and I/O", "io_read", "Read selected virtual-controller signals or a paged inventory."),
        ("Simulation and I/O", "io_write", "Write one virtual signal; optionally simulate a physical input."),
        ("Simulation and I/O", "io_pulse", "Request a native digital pulse; completion is not awaited."),
        ("Geometry", "geometry", "Find named CAD parts, bodies, IDs and world bounds."),
        ("Geometry", "raycast", "Measure a tool-frame ray against station parts."),
        ("Geometry", "collision_set", "Create an active collision set from two groups of components."),
        ("Simulation and I/O", "simulation_speed", "Read or set the target simulation speed."),
        ("Run evidence", "tcp_record", "Arm, mark or stop a sampled TCP run."),
        ("Run evidence", "run_review", "Check waypoints, forbidden volumes, height and pair clearance."),
        ("Run evidence", "run_view", "Render the cell with the recorded trace and zones."),
        ("RAPID", "rapid_load", "Stop the selected virtual task, load modules and check the result."),
        ("RAPID", "rapid_check", "Check the selected virtual task and report source diagnostics."),
        ("RAPID", "rapid_diff", "Compare module text and persistent values. Real reads require owner approval."),
        ("RAPID", "rapid_sync_real", "Load clone-verified files onto a stopped real task after owner approval.")
    ];
}
