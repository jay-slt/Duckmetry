using Duckmetry.Core.Configuration;

namespace Duckmetry.Gui;

/// <summary>
/// The settings dialog: folders, overwrite, and auto-start. Edits the caller's
/// <see cref="AppSettings"/> in place on OK (folder changes re-save immediately so the
/// main window's browse buttons and this dialog always agree).
/// </summary>
internal sealed class SettingsForm : Form
{
    private readonly AppSettings _settings;
    private readonly TextBox _source, _dest;
    private readonly CheckBox _overwrite, _autoStart;

    public SettingsForm(AppSettings settings)
    {
        _settings = settings;
        Text = "Settings";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(640, 240);
        MinimumSize = new Size(560, 220);

        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, Padding = new Padding(14, 14, 14, 8) };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        grid.Controls.Add(new Label { Text = "Source telemetry folder:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 6, 0) }, 0, 0);
        _source = new TextBox { Text = _settings.SourceFolder, Dock = DockStyle.Fill, Margin = new Padding(0, 3, 6, 3) };
        grid.Controls.Add(_source, 1, 0);
        AddBrowse(grid, 2, 0, _source);

        grid.Controls.Add(new Label { Text = "Destination folder:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 6, 0) }, 0, 1);
        _dest = new TextBox { Text = _settings.DestinationFolder, Dock = DockStyle.Fill, Margin = new Padding(0, 3, 6, 3) };
        grid.Controls.Add(_dest, 1, 1);
        AddBrowse(grid, 2, 1, _dest);

        _overwrite = new CheckBox
        {
            Text = "Overwrite existing .ld files",
            AutoSize = true,
            Checked = _settings.OverwriteExisting,
            Margin = new Padding(0, 14, 0, 0),
        };
        grid.Controls.Add(_overwrite, 1, 2);

        _autoStart = new CheckBox
        {
            Text = "Start in auto mode (tray) at launch",
            AutoSize = true,
            Checked = _settings.AutoStartInAutoMode,
            Margin = new Padding(0, 6, 0, 0),
        };
        grid.Controls.Add(_autoStart, 1, 3);

        var ok = new Button { Text = "Save", Width = 90, DialogResult = DialogResult.OK };
        ok.Click += (_, _) => Apply();
        var cancel = new Button { Text = "Cancel", Width = 90, DialogResult = DialogResult.Cancel };
        var row = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 46, Padding = new Padding(10, 8, 10, 8) };
        row.Controls.Add(cancel);
        row.Controls.Add(ok);

        Controls.Add(grid);
        Controls.Add(row);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    private void AddBrowse(TableLayoutPanel grid, int col, int row, TextBox box)
    {
        var b = new Button { Text = "Browse…", AutoSize = true, Margin = new Padding(0, 3, 0, 3) };
        b.Click += (_, _) =>
        {
            using var dlg = new FolderBrowserDialog
            {
                SelectedPath = Directory.Exists(box.Text) ? box.Text : null,
            };
            if (dlg.ShowDialog(this) == DialogResult.OK) box.Text = dlg.SelectedPath;
        };
        grid.Controls.Add(b, col, row);
    }

    private void Apply()
    {
        if (!string.IsNullOrWhiteSpace(_source.Text)) _settings.SourceFolder = _source.Text.Trim().Trim('"', '\'');
        if (!string.IsNullOrWhiteSpace(_dest.Text))
        {
            _settings.DestinationFolder = _dest.Text.Trim().Trim('"', '\'');
            Directory.CreateDirectory(_settings.DestinationFolder);
        }
        _settings.OverwriteExisting = _overwrite.Checked;
        _settings.AutoStartInAutoMode = _autoStart.Checked;
        _settings.Save();
    }
}