using System.Runtime.InteropServices;
using Duckmetry.Core.Configuration;
using Duckmetry.Core.Live;

namespace Duckmetry.Gui;

/// <summary>
/// The tray controller: the NotifyIcon, its context menu, and the AutoWatcher. Auto
/// mode lives here — the main window hides and the watcher converts finished sessions.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly MainForm _form;
    private readonly AppSettings _settings;
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _autostart;
    private AutoWatcher? _watcher;
    private bool _disposed;

    public TrayIcon(MainForm form, AppSettings settings)
    {
        _form = form;
        _settings = settings;

        _icon = new NotifyIcon { Text = "Duckmetry — auto mode", Icon = Icons.Tray };
        var menu = new ContextMenuStrip();

        var show = new ToolStripMenuItem("Show window");
        show.Click += (_, _) => ShowWindow();
        menu.Items.Add(show);

        _autostart = new ToolStripMenuItem("Start in auto mode at launch")
        {
            CheckOnClick = true,
            Checked = _settings.AutoStartInAutoMode,
        };
        _autostart.Click += (_, _) =>
        {
            _settings.AutoStartInAutoMode = _autostart.Checked;
            _settings.Save();
        };
        menu.Items.Add(_autostart);

        var settingsItem = new ToolStripMenuItem("Settings…");
        settingsItem.Click += (_, _) => _form.BeginInvoke(new Action(_form.OpenSettings));
        menu.Items.Add(settingsItem);

        menu.Items.Add(new ToolStripSeparator());

        var quit = new ToolStripMenuItem("Quit");
        quit.Click += (_, _) => Quit();
        menu.Items.Add(quit);

        _icon.ContextMenuStrip = menu;
        _icon.DoubleClick += (_, _) => ShowWindow();
        _icon.Visible = true;
    }

    public bool AutoModeActive => _watcher is { Started: true };

    public void Start()
    {
        if (_watcher is { Started: true }) return;
        _watcher = new AutoWatcher(_settings);
        _watcher.Log += line => _form.AppendLog(line);
        _watcher.Converted += path =>
        {
            _form.AppendLog($"Converted: {Path.GetFileName(path)}");
            try
            {
                _form.BeginInvoke(new Action(() =>
                    _icon.ShowBalloonTip(3000, "Duckmetry",
                        $"Converted {Path.GetFileName(path)}", ToolTipIcon.Info)));
            }
            catch (ObjectDisposedException) { /* closing */ }
        };
        _watcher.Start();
    }

    private void ShowWindow()
    {
        _form.Show();
        _form.WindowState = FormWindowState.Normal;
        _form.Activate();
        _form.PopulateList();
    }

    private void Quit()
    {
        _watcher?.Dispose();
        _icon.Visible = false;
        Application.Exit();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _watcher?.Dispose();
        _icon.Visible = false;
        _icon.Dispose();
    }
}