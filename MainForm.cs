using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace EldenRingEnableGraces;

/// <summary>
/// Main window: pick an Elden Ring regulation.bin, then toggle a checkbox per
/// grace to force-enable it (eventflagId → 76101) or restore its original flag.
/// Save writes the changes back into regulation.bin (with a .bak backup).
/// </summary>
public class MainForm : Form
{
    private readonly TextBox _pathBox = new();
    private readonly TextBox _filterBox = new();
    private readonly DataGridView _grid = new();
    private readonly Label _countLabel = new();
    private readonly ToolStripStatusLabel _statusLabel = new();
    private readonly Button _saveButton = new();

    private string? _regulationPath;
    private List<GraceRow> _rows = new();
    private bool _dirty;
    private bool _bulk; // suppress change-handler noise during programmatic updates

    public MainForm()
    {
        Text = "Elden Ring — Enable Graces";
        Width = 1000;
        Height = 660;
        MinimumSize = new Size(680, 380);

        BuildToolbar();
        BuildFilterBar();
        BuildGrid();
        BuildStatus();

        Load += (_, _) => UpdateStatus();
        FormClosing += MainForm_FormClosing;
    }

    // ---- Toolbar ----

    private void BuildToolbar()
    {
        var panel = new Panel { Dock = DockStyle.Top, Height = 36, Padding = new Padding(8, 4, 8, 4) };

        var browse = new Button { Text = "Browse regulation.bin…", Dock = DockStyle.Left, Width = 150 };
        browse.Click += Browse_Click;

        var enableAll = new Button { Text = "Enable all", Dock = DockStyle.Right, Width = 90 };
        enableAll.Click += (_, _) => SetAllEnabled(true);

        var disableAll = new Button { Text = "Disable all", Dock = DockStyle.Right, Width = 90 };
        disableAll.Click += (_, _) => SetAllEnabled(false);

        _saveButton.Text = "Save";
        _saveButton.Dock = DockStyle.Right;
        _saveButton.Width = 80;
        _saveButton.Enabled = false;
        _saveButton.Click += Save_Click;

        _pathBox.PlaceholderText = "path to regulation.bin";
        _pathBox.Dock = DockStyle.Fill;
        _pathBox.ReadOnly = true;

        // [Browse] [path fill] | [Enable all] [Disable all] [Save]
        panel.Controls.Add(_pathBox);
        panel.Controls.Add(_saveButton);
        panel.Controls.Add(disableAll);
        panel.Controls.Add(enableAll);
        panel.Controls.Add(browse);
        Controls.Add(panel);
    }

    private void BuildFilterBar()
    {
        var panel = new Panel { Dock = DockStyle.Top, Height = 30, Padding = new Padding(8, 2, 8, 4) };
        var label = new Label { Text = "Filter:", Dock = DockStyle.Left, AutoSize = true, TextAlign = ContentAlignment.MiddleLeft };

        _filterBox.PlaceholderText = "type to filter by ID or name…";
        _filterBox.Dock = DockStyle.Fill;
        _filterBox.TextChanged += (_, _) => PopulateGrid();

        _countLabel.Text = "";
        _countLabel.Dock = DockStyle.Right;
        _countLabel.TextAlign = ContentAlignment.MiddleLeft;
        _countLabel.Width = 240;

        panel.Controls.Add(_filterBox);
        panel.Controls.Add(_countLabel);
        panel.Controls.Add(label);
        Controls.Add(panel);
    }

    // ---- Grid ----

    private void BuildGrid()
    {
        _grid.Dock = DockStyle.Fill;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.AllowUserToResizeRows = false;
        _grid.ReadOnly = false; // checkbox column must be editable
        _grid.RowHeadersVisible = false;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.MultiSelect = false;
        _grid.BackgroundColor = SystemColors.Window;
        _grid.ColumnHeadersDefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleLeft;
        _grid.SortCompare += Grid_SortCompare;

        // Make only the checkbox column editable; the rest stay read-only.
        var enabled = new DataGridViewCheckBoxColumn
        {
            Name = "Enabled",
            HeaderText = "Enabled",
            Width = 60,
            SortMode = DataGridViewColumnSortMode.Automatic,
            DefaultCellStyle = { Alignment = DataGridViewContentAlignment.MiddleCenter },
        };
        _grid.Columns.Add(enabled);

        AddTextColumn("Id", "ID", 70, rightAlign: true);
        AddTextColumn("Name", "Name", 430, rightAlign: false);
        AddTextColumn("EventFlag", "Event Flag ID", 110, rightAlign: true);
        AddTextColumn("Original", "Original", 90, rightAlign: true);

        foreach (DataGridViewColumn col in _grid.Columns)
            if (col.Name != "Enabled")
                col.ReadOnly = true;

        // Commit checkbox edits immediately on click.
        _grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (!_bulk && _grid.CurrentCell is DataGridViewCheckBoxCell)
                _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        _grid.CellValueChanged += Grid_CellValueChanged;

        Controls.Add(_grid);
        _grid.BringToFront();
    }

    private void AddTextColumn(string name, string header, int width, bool rightAlign)
    {
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = name,
            HeaderText = header,
            Width = width,
            SortMode = DataGridViewColumnSortMode.Automatic,
            DefaultCellStyle = new DataGridViewCellStyle
            {
                Alignment = rightAlign
                    ? DataGridViewContentAlignment.MiddleRight
                    : DataGridViewContentAlignment.MiddleLeft,
                NullValue = string.Empty,
            },
        });
    }

    private void BuildStatus()
    {
        var status = new StatusStrip { Dock = DockStyle.Bottom };
        status.Items.Add(_statusLabel);
        Controls.Add(status);
    }

    // ---- Actions ----

    private void Browse_Click(object? sender, EventArgs e)
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Select Elden Ring regulation.bin",
            Filter = "Elden Ring regulation (regulation.bin)|regulation.bin|All files (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            _pathBox.Text = dlg.FileName;
            LoadCurrent();
        }
    }

    private void LoadCurrent()
    {
        string path = _pathBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            MessageBox.Show(this, "Choose a valid regulation.bin first.", "No file",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        UseWaitCursor = true;
        _statusLabel.Text = "Decrypting and reading regulation.bin…";
        Application.DoEvents();

        try
        {
            var rows = RegulationReader.ReadBonfireWarpParam(path);

            // Resolve originals from the sidecar; capture on first open.
            string sidecar = RegulationReader.GetOriginalsPath(path);
            Dictionary<int, uint> originals = RegulationReader.LoadOriginals(sidecar);
            if (originals.Count == 0)
            {
                foreach (GraceRow r in rows)
                    originals[r.Id] = r.OriginalEventFlagId;
                RegulationReader.SaveOriginals(sidecar, originals);
            }
            foreach (GraceRow r in rows)
                if (originals.TryGetValue(r.Id, out uint o))
                    r.OriginalEventFlagId = o;

            _regulationPath = path;
            _rows = rows.ToList();
            _dirty = false;
            PopulateGrid();
            _statusLabel.Text = $"Loaded {rows.Count} graces from {Path.GetFileName(path)}.";
        }
        catch (Exception ex)
        {
            _regulationPath = null;
            _rows.Clear();
            PopulateGrid();
            _statusLabel.Text = "Failed to load.";
            MessageBox.Show(this,
                $"Could not read regulation.bin:\n\n{ex.GetBaseException().Message}",
                "Load failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            UseWaitCursor = false;
        }
    }

    private void Save_Click(object? sender, EventArgs e)
    {
        if (_regulationPath is null || _rows.Count == 0)
            return;

        UseWaitCursor = true;
        _statusLabel.Text = "Encrypting and writing regulation.bin…";
        Application.DoEvents();

        try
        {
            RegulationReader.WriteBonfireWarpParam(_regulationPath, _rows);
            _dirty = false;
            UpdateStatus();
            string bak = RegulationReader.GetBackupPath(_regulationPath);
            MessageBox.Show(this,
                $"Saved {Path.GetFileName(_regulationPath)}.\n\nBackup of the original:\n{bak}",
                "Saved", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            _statusLabel.Text = "Save failed.";
            MessageBox.Show(this,
                $"Could not save regulation.bin:\n\n{ex.GetBaseException().Message}",
                "Save failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            UseWaitCursor = false;
        }
    }

    // ---- Toggling ----

    private void Grid_CellValueChanged(object? sender, DataGridViewCellEventArgs e)
    {
        if (_bulk || e.RowIndex < 0 || e.ColumnIndex < 0)
            return;
        if (_grid.Columns[e.ColumnIndex].Name != "Enabled")
            return;

        DataGridViewRow gridRow = _grid.Rows[e.RowIndex];
        if (gridRow.Tag is not GraceRow gr)
            return;

        bool enabled = Convert.ToBoolean(gridRow.Cells["Enabled"].Value);
        gr.CurrentEventFlagId = enabled
            ? GraceRow.EnableEventFlagId
            : gr.OriginalEventFlagId;
        gridRow.Cells["EventFlag"].Value = gr.CurrentEventFlagId.ToString();

        MarkDirty();
    }

    private void SetAllEnabled(bool enabled)
    {
        if (_rows.Count == 0)
            return;

        _bulk = true;
        try
        {
            foreach (GraceRow gr in _rows)
            {
                gr.CurrentEventFlagId = enabled
                    ? GraceRow.EnableEventFlagId
                    : gr.OriginalEventFlagId;
            }
            PopulateGrid(); // refreshes checkbox + Event Flag cells from the model
        }
        finally
        {
            _bulk = false;
        }
        MarkDirty();
    }

    // ---- Grid population / filtering ----

    private void PopulateGrid()
    {
        string q = _filterBox.Text.Trim();

        _grid.SuspendLayout();
        _grid.Rows.Clear();

        foreach (GraceRow gr in _rows)
        {
            if (!string.IsNullOrEmpty(q)
                && !gr.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                && !gr.Id.ToString().Contains(q, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            int newRowIndex = _grid.Rows.Add(
                gr.IsEnabled,                       // Enabled (checkbox)
                gr.Id.ToString(),                   // ID
                gr.Name,                            // Name
                gr.CurrentEventFlagId.ToString(),   // Event Flag ID
                gr.OriginalEventFlagId.ToString()); // Original

            _grid.Rows[newRowIndex].Tag = gr;
        }

        _grid.ResumeLayout();
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        int enabledCount = _rows.Count(r => r.IsEnabled);
        string dirtyText = _dirty ? " • unsaved changes" : "";
        _countLabel.Text = $"{enabledCount} of {_rows.Count} enabled";
        _statusLabel.Text = _regulationPath is null
            ? "Ready. Pick an Elden Ring regulation.bin to begin."
            : $"{enabledCount} of {_rows.Count} graces enabled{dirtyText}.";
        _saveButton.Enabled = _regulationPath is not null && _rows.Count > 0;
    }

    private void MarkDirty()
    {
        _dirty = true;
        UpdateStatus();
    }

    private void Grid_SortCompare(object? sender, DataGridViewSortCompareEventArgs e)
    {
        if (e.Column.Name is "Id" or "EventFlag" or "Original")
        {
            if (long.TryParse(e.CellValue1?.ToString(), out long a) &&
                long.TryParse(e.CellValue2?.ToString(), out long b))
            {
                e.SortResult = a.CompareTo(b);
                e.Handled = true;
            }
        }
    }

    private void MainForm_FormClosing(object? sender, FormClosingEventArgs e)
    {
        if (!_dirty)
            return;

        DialogResult r = MessageBox.Show(this,
            "You have unsaved changes. Exit anyway?",
            "Unsaved changes",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);

        if (r == DialogResult.No)
            e.Cancel = true;
    }
}
