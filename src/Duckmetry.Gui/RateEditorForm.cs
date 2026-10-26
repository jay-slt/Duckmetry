using System.Globalization;
using Duckmetry.Core.Configuration;
using Duckmetry.Core.Models;

namespace Duckmetry.Gui;

/// <summary>
/// The Pro-mode per-channel rate editor. Takes the merged ceilings built from the
/// selected sessions (highest valid rate per channel = the lowest the sessions recorded)
/// and lets the user set each channel's output rate up to that ceiling. Saving writes
/// the choices into <see cref="AppSettings.RateOverrides"/> so they persist and can be
/// edited again the same way.
/// </summary>
internal sealed class RateEditorForm : Form
{
    private readonly AppSettings _settings;
    private readonly Dictionary<string, int> _ceilings;
    private readonly DataGridView _grid;
    private readonly Label _summary;

    public RateEditorForm(Dictionary<string, int> ceilings, AppSettings settings)
    {
        _ceilings = ceilings;
        _settings = settings;

        Text = "Per-channel rates (Pro mode)";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(640, 440);
        MinimumSize = new Size(520, 300);

        // On small screens / high DPI the dialog can open with its top edge off-screen
        // (seen: "the top of the table is hidden"). Clamp it into the working area.
        Shown += (_, _) =>
        {
            var wa = Screen.FromControl(this).WorkingArea;
            if (Width > wa.Width) Width = wa.Width;
            if (Height > wa.Height) Height = wa.Height;
            if (Left < wa.Left) Left = wa.Left;
            if (Top < wa.Top) Top = wa.Top;
            if (Right > wa.Right) Left = wa.Right - Width;
            if (Bottom > wa.Bottom) Top = wa.Bottom - Height;
        };

        _summary = new Label
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(10, 8, 10, 0),
            Text = "Highest possible rate per channel is the lowest rate the selected sessions recorded it at. " +
                   "Set each channel's rate, then Save — the choices persist in your settings and can be edited again here.",
        };

        _grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = false,
            EditMode = DataGridViewEditMode.EditOnEnter,   // click a cell -> edit immediately
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            RowHeadersVisible = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            BackgroundColor = System.Drawing.SystemColors.Window,
        };
        var channelCol = new DataGridViewTextBoxColumn
        {
            HeaderText = "Channel",
            ReadOnly = true,
            FillWeight = 60,
        };
        var sourceCol = new DataGridViewTextBoxColumn
        {
            HeaderText = "Recorded (Hz)",
            ReadOnly = true,
            FillWeight = 15,
        };
        var rateCol = new DataGridViewTextBoxColumn
        {
            HeaderText = "Rate (Hz)",
            ReadOnly = false,
            FillWeight = 15,
        };
        _grid.Columns.AddRange(channelCol, sourceCol, rateCol);
        Controls.Add(_grid);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 48,
            Padding = new Padding(10, 8, 12, 8),
        };
        var save = new Button { Text = "Save", Width = 100 };
        save.Click += (_, _) => SaveRates();
        var cancel = new Button { Text = "Cancel", Width = 100, DialogResult = DialogResult.Cancel };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(save);
        Controls.Add(buttons);

        // DOCK ORDER: controls added later dock FIRST. The grid (Fill) and the button
        // row (Bottom) must dock before the summary (Top), or the summary would overlay
        // the grid's top — the "description sits on top of the table" bug. So the
        // summary is added LAST, even though it belongs at the top of the window.
        Controls.Add(_summary);

        AcceptButton = save;
        CancelButton = cancel;

        Populate();

        // Land the keyboard on the first Rate cell so editing can start immediately.
        ActiveControl = _grid;
        if (_grid.Rows.Count > 0) _grid.CurrentCell = _grid.Rows[0].Cells[2];
    }

    private void Populate()
    {
        _grid.Rows.Clear();
        foreach (var (name, ceiling) in _ceilings.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            // The saved choice persists; anything above today's ceiling is clamped at
            // conversion time anyway, so show what the user chose.
            int chosen = _settings.RateOverrides.TryGetValue(name, out int saved) && saved > 0
                ? saved
                : ceiling;
            int row = _grid.Rows.Add(name, ceiling.ToString(CultureInfo.InvariantCulture), chosen.ToString(CultureInfo.InvariantCulture));
            _grid.Rows[row].Cells[2].Tag = ceiling; // the cap for validation
            _grid.Rows[row].Cells[2].ReadOnly = false;
        }
    }

    private void SaveRates()
    {
        var overrides = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (DataGridViewRow row in _grid.Rows)
        {
            var name = (string?)row.Cells[0].Value;
            int ceiling = row.Cells[2].Tag is int cap ? cap : 0;
            string? text = row.Cells[2].Value as string;

            if (string.IsNullOrWhiteSpace(name)) continue;
            if (!int.TryParse(text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int rate)
                || rate < 1 || rate > ceiling)
            {
                MessageBox.Show(this,
                    $"\"{name}\": the rate must be a whole number between 1 and {ceiling} Hz " +
                    "(the highest the selected sessions recorded).",
                    "Per-channel rates", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                _grid.CurrentCell = row.Cells[2];
                return;
            }
            overrides[name] = rate;
        }

        _settings.RateOverrides = overrides;
        _settings.Save();
        DialogResult = DialogResult.OK;
    }
}