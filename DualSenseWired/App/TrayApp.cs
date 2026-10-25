// Tray icon: state at a glance, emulation switch, settings window, exit; low battery warnings.

using System.Drawing.Drawing2D;

namespace DualSenseWired.App;

public sealed class TrayApp : ApplicationContext
{
    readonly Settings _settings;
    readonly BridgeHost _host;
    readonly NotifyIcon _tray;
    readonly ToolStripMenuItem _emulationItem;
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 2000 };
    readonly Strings _s = Strings.Current;
    readonly Icon _iconOn = MakeIcon(true), _iconOff = MakeIcon(false);
    MainForm? _form;
    readonly Updater _updater;
    readonly System.Windows.Forms.Timer _updateTimer = new() { Interval = 15000 };
    string? _notifiedUpdate;
    int _warnedAt = 101;
    bool _exiting;

    public TrayApp(Settings settings, bool showSettings)
    {
        _settings = settings;
        _host = new BridgeHost(Logger.Log);
        _updater = new Updater(Logger.Log);

        var menu = new ContextMenuStrip();
        menu.Items.Add(_s.Open, null, (_, _) => ShowSettings());
        _emulationItem = new ToolStripMenuItem(_s.Emulation) { Checked = settings.Emulation, CheckOnClick = true };
        _emulationItem.CheckedChanged += (_, _) =>
        {
            if (_emulationItem.Checked != _settings.Emulation)
            {
                _settings.Emulation = _emulationItem.Checked;
                _settings.Save();
                SetEmulation(_settings.Emulation);
            }
        };
        menu.Items.Add(_emulationItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_s.Exit, null, (_, _) => Exit());

        _tray = new NotifyIcon { Icon = _iconOff, Text = _s.AppName, ContextMenuStrip = menu, Visible = true };
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                ShowSettings();
        };
        _tray.BalloonTipClicked += (_, _) => ShowSettings();

        if (settings.Emulation)
            _host.Start(settings);
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        // Update check shortly after start (the network may come up late after logon), then twice a day.
        _updateTimer.Tick += async (_, _) =>
        {
            _updateTimer.Interval = (int)TimeSpan.FromHours(12).TotalMilliseconds;
            await CheckUpdatesAsync();
        };
        _updateTimer.Start();
        if (showSettings)
            ShowSettings();
    }

    void SetEmulation(bool on)
    {
        _emulationItem.Checked = on;
        // Starting and stopping run usbip and HidHide: keep the UI responsive.
        Task.Run(() =>
        {
            if (on)
                _host.Start(_settings);
            else
                _host.Stop();
        });
    }

    public void ShowSettings()
    {
        if (_form == null || _form.IsDisposed)
        {
            _form = new MainForm(_settings, _host, _updater, on =>
            {
                _settings.Save();
                SetEmulation(on);
            });
        }
        _form.Show();
        if (_form.WindowState == FormWindowState.Minimized)
            _form.WindowState = FormWindowState.Normal;
        _form.Activate();
    }

    public void Exit()
    {
        if (_exiting)
            return;
        _exiting = true;
        _timer.Stop();
        _updateTimer.Stop();
        _tray.Visible = false;
        _form?.Close();
        _host.Dispose();     // unplugs the virtual controller and gives the real one back to Steam
        _tray.Dispose();
        ExitThread();
    }

    async Task CheckUpdatesAsync()
    {
        if (!_settings.CheckUpdates)
            return;
        if (await _updater.CheckAsync() is { } release && release.Tag != _notifiedUpdate)
        {
            _notifiedUpdate = release.Tag;
            _tray.ShowBalloonTip(8000, _s.UpdateTitle, string.Format(_s.UpdateText, release.Tag), ToolTipIcon.Info);
        }
    }

    void Tick()
    {
        var link = _host.Link;
        bool connected = link?.Connected == true;
        _tray.Icon = connected ? _iconOn : _iconOff;
        string state = link == null ? _s.EmulationOff : connected ? _s.Connected : _s.NoController;
        string battery = connected ? "\n" + BatteryText(link!.Battery) : "";
        string text = $"{_s.AppName}\n{state}{battery}";
        _tray.Text = text.Length > 127 ? text[..127] : text;

        // Low battery: warn at 20 % and 10 %, again after it was charged.
        if (connected && link!.Battery is { } b)
        {
            if (b.Charging || b.Full)
                _warnedAt = 101;
            else if (_settings.LowBatteryNotifications && (b.Percent <= 10 && _warnedAt > 10 || b.Percent <= 20 && _warnedAt > 20))
            {
                _warnedAt = b.Percent <= 10 ? 10 : 20;
                _tray.ShowBalloonTip(5000, _s.LowBatteryTitle, $"{b.Percent}%", ToolTipIcon.Warning);
            }
        }
    }

    public static string BatteryText((int Percent, bool Charging, bool Full)? battery)
    {
        var s = Strings.Current;
        if (battery is not { } b)
            return "";
        return $"{s.Battery}: {b.Percent}%" + (b.Full ? $", {s.Full}" : b.Charging ? $", {s.Charging}" : "");
    }

    /// <summary>A small gamepad-like glyph: blue when the controller is connected, grey otherwise.</summary>
    public static Icon MakeIcon(bool active)
    {
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var color = active ? Color.FromArgb(0, 112, 209) : Color.FromArgb(128, 128, 128);
            using var body = new SolidBrush(color);
            using var path = new GraphicsPath();
            path.AddEllipse(1, 8, 14, 20);
            path.AddEllipse(17, 8, 14, 20);
            path.AddRectangle(new Rectangle(8, 8, 16, 13));
            g.FillPath(body, path);
            using var white = new SolidBrush(Color.White);
            g.FillRectangle(white, 6, 13, 6, 2);
            g.FillRectangle(white, 8, 11, 2, 6);
            g.FillEllipse(white, 21, 11, 3, 3);
            g.FillEllipse(white, 21, 16, 3, 3);
        }
        return Icon.FromHandle(bitmap.GetHicon());
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            Exit();
        base.Dispose(disposing);
    }
}
