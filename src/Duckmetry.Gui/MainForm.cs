using System.Diagnostics;
using System.Globalization;
using System.Windows.Forms.VisualStyles;
using Duckmetry.Core.Configuration;
using Duckmetry.Core.Converter;
using Duckmetry.Core.DuckDb;
using Duckmetry.Core.Models;
using Duckmetry.Core.Scanner;

namespace Duckmetry.Gui;

/// <summary>
/// The main window: sessions list, options, convert, settings, and the door into auto
/// mode (the tray). All logic lives in Core; this form is a thin layer over it.
/// </summary>
internal sealed class MainForm : Form
{
    private readonly AppSettings _settings;
    private readonly TextBox _sourceBox, _destBox;
    private readonly ListView _list;
    private readonly ComboBox _rateCombo;
    private readonly CheckBox _duckNames, _svm, _sorted;
    private readonly Button _ratesButton;
    private readonly Button _convertButton, _autoButton, _settingsButton, _rescanButton;
    private bool _allChecked;
    private Rectangle _headerCheckRect;
    private readonly ProgressBar _progress;
    private readonly Label _statusLabel;
    private readonly TextBox _log;
    private TrayIcon? _tray;
    private bool _converting;

    private List<ConversionItem> _items = new();

    public MainForm()
    {
        _settings = AppSettings.Load();

        Text = "Duckmetry — DuckDB to i2 converter";
        Icon = Icons.App;
        Width = 980;
        Height = 720;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(820, 560);

        var top = new TableLayoutPanel { Dock = DockStyle.Top, Height = 96, ColumnCount = 3, Padding = new Padding(10, 8, 10, 0) };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        // AutoSize rows: without them the cells split the fixed height evenly and the
        // browse/rescan buttons get clipped ("the buttons are kinda cropped").
        top.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        top.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        top.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        top.Controls.Add(new Label { Text = "Source telemetry folder:", Anchor = AnchorStyles.Left, Margin = new Padding(0, 4, 4, 0) }, 0, 0);
        _sourceBox = new TextBox { Dock = DockStyle.Fill, ReadOnly = true, Margin = new Padding(0, 2, 6, 2) };
        top.Controls.Add(_sourceBox, 1, 0);
        var browseSrc = new Button { Text = "Browse…", AutoSize = true, Margin = new Padding(0, 2, 0, 2) };
        browseSrc.Click += (_, _) => PickFolder(setting => _settings.SourceFolder = setting, _sourceBox);
        top.Controls.Add(browseSrc, 2, 0);

        top.Controls.Add(new Label { Text = "Destination folder:", Anchor = AnchorStyles.Left, Margin = new Padding(0, 4, 4, 0) }, 0, 1);
        _destBox = new TextBox { Dock = DockStyle.Fill, ReadOnly = true, Margin = new Padding(0, 2, 6, 2) };
        top.Controls.Add(_destBox, 1, 1);
        var browseDst = new Button { Text = "Browse…", AutoSize = true, Margin = new Padding(0, 2, 0, 2) };
        browseDst.Click += (_, _) => PickFolder(setting => _settings.DestinationFolder = setting, _destBox);
        top.Controls.Add(browseDst, 2, 1);

        _rescanButton = new Button { Text = "Rescan", AutoSize = true, Margin = new Padding(0, 2, 0, 2) };
        _rescanButton.Click += (_, _) => PopulateList();
        top.Controls.Add(_rescanButton, 2, 2);

        _sourceBox.Text = _settings.SourceFolder;
        _destBox.Text = _settings.DestinationFolder;
        Controls.Add(top);

        _list = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            CheckBoxes = true,
            FullRowSelect = true,
            GridLines = true,
            MultiSelect = true,
        };
        _list.Columns.Add("", 34);
        _list.Columns.Add("Status", 110);
        _list.Columns.Add("Track", 230);
        _list.Columns.Add("Car", 250);
        _list.Columns.Add("Driver", 130);
        _list.Columns.Add("Modified", 130);
        _list.Columns.Add("Size", 95);
        Controls.Add(_list);
        SetupHeaderCheckbox();

        var options = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 64, Padding = new Padding(10, 6, 10, 2), AutoSize = false };
        options.Controls.Add(new Label { Text = "Sample rate:", Anchor = AnchorStyles.Left, AutoSize = true, Margin = new Padding(0, 6, 4, 0) });
        _rateCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300, Margin = new Padding(0, 2, 14, 0) };
        _rateCombo.Items.AddRange(new object[]
        {
            "Optimised (default)",
            "Source (every sample the game recorded)",
            "Reduced (each channel / 2)",
            "Pro (per-channel rates)",
        });
        _rateCombo.SelectedIndex = (int)_settings.RateMode;
        _rateCombo.SelectedIndexChanged += (_, _) => { _settings.RateMode = (RateMode)_rateCombo.SelectedIndex; _settings.Save(); UpdateRateButton(); };
        options.Controls.Add(_rateCombo);

        _ratesButton = new Button
        {
            Text = "Set rates…",
            AutoSize = true,
            Margin = new Padding(0, 3, 0, 0),
            Visible = false,
        };
        _ratesButton.Click += (_, _) => OpenRateEditor();
        options.Controls.Add(_ratesButton);

        _duckNames = new CheckBox { Text = "Game channel names (DuckDB spelling)", AutoSize = true, Margin = new Padding(0, 6, 10, 0), Checked = _settings.UseDuckDbChannelNames };
        _duckNames.CheckedChanged += (_, _) => { _settings.UseDuckDbChannelNames = _duckNames.Checked; _settings.Save(); };
        options.Controls.Add(_duckNames);

        _svm = new CheckBox { Text = "Car setup (.svm)", AutoSize = true, Margin = new Padding(0, 6, 10, 0), Checked = _settings.ExportCarSetup };
        _svm.CheckedChanged += (_, _) => { _settings.ExportCarSetup = _svm.Checked; _settings.Save(); };
        options.Controls.Add(_svm);

        _sorted = new CheckBox { Text = "Sorted output (track/class folders)", AutoSize = true, Margin = new Padding(0, 6, 0, 0), Checked = _settings.OutputLayout == OutputLayout.Sorted };
        _sorted.CheckedChanged += (_, _) => { _settings.OutputLayout = _sorted.Checked ? OutputLayout.Sorted : OutputLayout.Flat; _settings.Save(); PopulateList(); };
        options.Controls.Add(_sorted);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 74, Padding = new Padding(10, 4, 10, 4) };
        _convertButton = new Button { Text = "Convert selected", Width = 140, Height = 30 };
        _convertButton.Click += (_, _) => ConvertSelected();
        buttons.Controls.Add(_convertButton);
        _autoButton = new Button { Text = "Auto mode → tray", Width = 140, Height = 30 };
        _autoButton.Click += (_, _) => EnterAutoMode();
        buttons.Controls.Add(_autoButton);
        _settingsButton = new Button { Text = "Settings…", Width = 100, Height = 30 };
        _settingsButton.Click += (_, _) => OpenSettings();
        buttons.Controls.Add(_settingsButton);

        _progress = new ProgressBar { Width = 300, Height = 20, Margin = new Padding(18, 4, 8, 0) };
        buttons.Controls.Add(_progress);
        _statusLabel = new Label { Text = "", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 8, 0, 0) };
        buttons.Controls.Add(_statusLabel);
        Controls.Add(buttons);

        // NOTE: every panel must be added before SetChildIndex below, or WinForms
        // throws "'child' is not a child control of this parent" (seen in the wild:
        // the options panel was built, filled, and forgotten).
        Controls.Add(options);

        _log = new TextBox { Dock = DockStyle.Bottom, Height = 110, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = Color.FromArgb(30, 30, 34), ForeColor = Color.LightGray, Font = new Font("Consolas", 9) };
        Controls.Add(_log);
        Controls.SetChildIndex(_log, 0);
        Controls.SetChildIndex(buttons, 0);
        Controls.SetChildIndex(options, 0);
        Controls.SetChildIndex(_list, 0);

        if (string.IsNullOrWhiteSpace(_settings.SourceFolder) || string.IsNullOrWhiteSpace(_settings.DestinationFolder))
        {
            AppendLog("First run: pick your folders in Settings, then Rescan.");
            OpenSettings();
        }
        PopulateList();

        // Subscribe AFTER the constructor has finished building and PopulateList has
        // populated the list — otherwise adding checked items mid-construction raises
        // ItemChecked while some fields are still null (crash 2026-10-19, twice).
        _list.ItemChecked += (_, _) => UpdateRateButton();
        FormClosing += OnFormClosing;
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        // In auto mode the window lives in the tray; only the tray's Quit really exits.
        if (_tray is { AutoModeActive: true } && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            AppendLog("Still running in the tray — use the tray menu to quit.");
        }
    }

    private void PickFolder(Action<string> set, TextBox box)
    {
        using var dlg = new FolderBrowserDialog
        {
            Description = box == _sourceBox ? "Where the game writes its .duckdb files" : "Where the converted .ld/.ldx/.svm should go",
            SelectedPath = !string.IsNullOrWhiteSpace(box.Text) && Directory.Exists(box.Text) ? box.Text : null,
        };
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            set(dlg.SelectedPath);
            _settings.Save();
            box.Text = dlg.SelectedPath;
            PopulateList();
        }
    }

    internal void OpenSettings()
    {
        using var dlg = new SettingsForm(_settings);
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            _sourceBox.Text = _settings.SourceFolder;
            _destBox.Text = _settings.DestinationFolder;
            PopulateList();
        }
    }

    /// <summary>Refreshes the sessions list from the scanner.</summary>
    public void PopulateList()
    {
        _list.BeginUpdate();
        _list.Items.Clear();
        try
        {
            if (string.IsNullOrWhiteSpace(_settings.SourceFolder) || !Directory.Exists(_settings.SourceFolder))
            {
                _statusLabel.Text = "No source folder set.";
                return;
            }

            _items = TelemetryScanner.ScanDirectories(_settings.SourceFolder, _settings.DestinationFolder, _settings.OutputLayout);
            foreach (var item in _items)
            {
                var lvi = new ListViewItem("");
                bool done = item.Status == ConversionStatus.AlreadyConverted;
                lvi.SubItems.Add(done ? "converted" : "ready");
                lvi.SubItems.Add(item.Track);
                lvi.SubItems.Add(item.Car);
                lvi.SubItems.Add(item.Driver);
                lvi.SubItems.Add(item.LastModified.ToString(CultureInfo.CurrentCulture));
                lvi.SubItems.Add((item.FileSizeBytes / 1048576.0).ToString("N2", CultureInfo.CurrentCulture) + " MB");
                lvi.Checked = !done;
                if (done) lvi.ForeColor = Color.Gray;
                _list.Items.Add(lvi);
            }
            _statusLabel.Text = $"{_items.Count} session(s) — {_items.Count(i => i.Status == ConversionStatus.Pending)} to convert.";
            _allChecked = false;
            _list.Invalidate();
            UpdateRateButton();
        }
        finally
        {
            _list.EndUpdate();
        }
    }

    /// <summary>
    /// The Pro-mode rate editor appears when Pro is selected and at least one session is
    /// checked — it builds the channel list from those files.
    /// </summary>
    private void UpdateRateButton()
    {
        // The button state is cosmetic; under no circumstance may it crash the window.
        // It can fire mid-construction (the first-run scan, a check change) before every
        // field exists — the NRE crash 2026-10-19/21:16 was this method, twice. Guarded
        // and clamped so any timing leaves the button hidden, never a dead window.
        if (_list is null || _rateCombo is null || _ratesButton is null || _items is null) return;
        try
        {
            bool pro = _rateCombo.SelectedIndex == (int)RateMode.Pro;
            bool anyChecked = false;
            int n = Math.Min(_list.Items.Count, _items.Count);
            for (int i = 0; i < n; i++)
            {
                if (_list.Items[i].Checked && _items[i].Status == ConversionStatus.Pending)
                {
                    anyChecked = true;
                    break;
                }
            }
            _ratesButton.Visible = pro && anyChecked;
        }
        catch
        {
            // Cosmetic toggle — never take the window down for it.
        }
    }

    /// <summary>
    /// Select/deselect all lives on the check column's header (the box at the top of
    /// the column), not a separate button — that is where people expect it.
    /// </summary>
    private void SetupHeaderCheckbox()
    {
        _list.OwnerDraw = true;
        _list.DrawColumnHeader += OnDrawColumnHeader;
        _list.MouseUp += OnHeaderMouseUp;
    }

    private void OnDrawColumnHeader(object? sender, DrawListViewColumnHeaderEventArgs e)
    {
        if (e.ColumnIndex != 0)
        {
            e.DrawDefault = true;
            return;
        }

        // The check column's header: just a checkbox, centred in the 34 px column.
        var box = new Rectangle(
            e.Bounds.Left + (e.Bounds.Width - 13) / 2,
            e.Bounds.Top + (e.Bounds.Height - 13) / 2,
            13, 13);
        _headerCheckRect = box;
        CheckBoxRenderer.DrawCheckBox(e.Graphics, box.Location,
            _allChecked ? CheckBoxState.CheckedNormal : CheckBoxState.UncheckedNormal);
    }

    private void OnHeaderMouseUp(object? sender, MouseEventArgs e)
    {
        if (_headerCheckRect.Contains(e.Location))
        {
            ToggleSelectAll();
        }
    }

    /// <summary>
    /// Selects or deselects every convertible session. The header box reflects the
    /// state; rescan resets it.
    /// </summary>
    private void ToggleSelectAll()
    {
        if (_list is null || _items is null) return;
        _allChecked = !_allChecked;
        int n = Math.Min(_list.Items.Count, _items.Count);
        for (int i = 0; i < n; i++)
        {
            if (_items[i].Status == ConversionStatus.Pending)
            {
                _list.Items[i].Checked = _allChecked;
            }
        }
        UpdateRateButton();
        _list.Invalidate();
    }

    private void OpenRateEditor()
    {
        var files = new List<string>();
        for (int i = 0; i < _list.Items.Count; i++)
        {
            if (_list.Items[i].Checked && _items[i].Status == ConversionStatus.Pending)
            {
                files.Add(_items[i].DuckDbFilePath);
            }
        }
        if (files.Count == 0) return;

        _statusLabel.Text = $"Reading channels from {files.Count} session(s)…";
        Task.Run(() =>
        {
            var perFile = new List<Dictionary<string, int>>();
            foreach (var file in files)
            {
                try
                {
                    using var reader = new DuckDbReader(file);
                    var frequencies = reader.ReadChannelFrequencies();
                    if (frequencies.Count > 0) perFile.Add(frequencies);
                }
                catch
                {
                    // Unreadable (locked by the game?) — it simply contributes no channels.
                }
            }
            return RatePlanner.MergeCeilings(perFile);
        }).ContinueWith(t =>
        {
            var ceilings = t.IsCompletedSuccessfully
                ? t.Result
                : new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            _statusLabel.Text = "";
            if (ceilings.Count == 0)
            {
                AppendLog("Could not read the channels of the selected sessions (are they still being written?).");
                return;
            }

            AppendLog($"Opened the rate editor: {ceilings.Count} channels from {files.Count} session(s).");
            using var dlg = new RateEditorForm(ceilings, _settings);
            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
                AppendLog($"Saved per-channel rates for {ceilings.Count} channels (Pro mode).");
            }
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void ConvertSelected()
    {
        if (_converting) return;
        var selected = new List<ConversionItem>();
        for (int i = 0; i < _list.Items.Count; i++)
        {
            if (_list.Items[i].Checked && _items[i].Status == ConversionStatus.Pending)
            {
                selected.Add(_items[i]);
            }
        }

        if (selected.Count == 0)
        {
            AppendLog("Nothing selected to convert.");
            return;
        }

        _converting = true;
        _convertButton.Enabled = false;
        _progress.Value = 0;
        AppendLog($"Converting {selected.Count} session(s)…");

        var progress = new Progress<(int, string)>(t =>
        {
            _progress.Value = Math.Min(100, Math.Max(0, t.Item1));
            if (!string.IsNullOrEmpty(t.Item2)) _statusLabel.Text = t.Item2;
            _statusLabel.Text = t.Item2;
        });

        Task.Run(() =>
        {
            int ok = 0;
            for (int i = 0; i < selected.Count; i++)
            {
                int index = i;
                var fileProgress = new Progress<int>(p =>
                    ((IProgress<(int, string)>)progress).Report((p, $"{selected[index].FileName} — {p}%")));

                bool converted = TelemetryConverter.ConvertSingleFile(
                    selected[index].DuckDbFilePath, _settings.DestinationFolder, BuildOptions(),
                    fileProgress, m => AppendLog(m));
                if (converted) ok++;
                ((IProgress<(int, string)>)progress).Report((100, $"{(converted ? "OK   " : "FAIL")} {selected[index].FileName}"));
            }
            AppendLog($"Done: {ok}/{selected.Count} converted.");
        }).ContinueWith(_ =>
        {
            _converting = false;
            _convertButton.Enabled = true;
            PopulateList();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private TelemetryConverter.ConversionOptions BuildOptions() => new()
    {
        ExtractCarSetup = _settings.ExportCarSetup,
        RateMode = _settings.RateMode,
        ReducedDivisor = _settings.ReducedDivisor,
        RateOverrides = _settings.RateOverrides,
        UseDuckDbChannelNames = _settings.UseDuckDbChannelNames,
        OutputLayout = _settings.OutputLayout,
        OverwriteExisting = _settings.OverwriteExisting,
    };

    /// <summary>Thread-safe log line (called from the conversion and watcher threads).</summary>
    public void AppendLog(string line)
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            try
            {
                BeginInvoke(new Action<string>(AppendLog), line);
            }
            catch (ObjectDisposedException) { /* window is gone */ }
            return;
        }
        string stamp = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        _log.AppendText($"[{stamp}] {line}{Environment.NewLine}");
    }

    public void EnterAutoMode()
    {
        if (string.IsNullOrWhiteSpace(_settings.SourceFolder))
        {
            AppendLog("Set a source folder before entering auto mode.");
            OpenSettings();
            return;
        }
        _tray ??= new TrayIcon(this, _settings);
        _tray.Start();
        Hide();
        AppendLog("Auto mode running in the tray — it watches the game and converts each finished session.");
    }

    public AppSettings Settings => _settings;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tray?.Dispose();
        }
        base.Dispose(disposing);
    }
}