using System.Diagnostics;
using MonitorSleep.Core;

namespace MonitorSleep.UI;

/// <summary>托盘外壳：持有通知图标、上下文菜单，并把 UI 能力注入给控制器。</summary>
internal sealed class TrayContext : ApplicationContext
{
    private readonly SettingsStore _store = new();
    private readonly MonitorController _controller;
    private readonly NotifyIcon _icon;
    private readonly Icon _iconOn;
    private readonly Icon _iconOff;
    private readonly ContextMenuStrip _menu;
    private readonly EventWaitHandle _showSettingsEvent;
    private readonly System.Windows.Forms.Timer _showSettingsPoller;
    private readonly List<Form> _overlays = new();
    private CountdownForm? _countdown;
    private bool _disposed;

    public MonitorController Controller => _controller;

    public TrayContext(EventWaitHandle showSettingsEvent)
    {
        _showSettingsEvent = showSettingsEvent;
        _controller = new MonitorController(_store);

        _iconOn = TrayIconFactory.Create(displayOff: false);
        _iconOff = TrayIconFactory.Create(displayOff: true);

        _menu = new ContextMenuStrip { Font = new Font("Microsoft YaHei UI", 9f) };
        _menu.Opening += (_, _) => RebuildMenu();

        _icon = new NotifyIcon
        {
            Icon = _iconOn,
            Text = Truncate(_controller.TooltipText, 120),
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => ShowSettings();

        // 注入 UI 能力，Core 层不直接依赖窗体
        _controller.CountdownPresenter = ShowCountdown;
        _controller.OverlayPresenter = ShowOverlay;
        _controller.Notifier = Notify;
        _controller.StateChanged += OnStateChanged;

        // 第二个实例会 Set 这个事件，我们把设置窗口弹出来
        _showSettingsPoller = new System.Windows.Forms.Timer { Interval = 500 };
        _showSettingsPoller.Tick += (_, _) =>
        {
            try { if (_showSettingsEvent.WaitOne(0)) ShowSettings(); }
            catch { /* 忽略 */ }
        };
        _showSettingsPoller.Start();

        Microsoft.Win32.SystemEvents.SessionSwitch += OnSessionSwitch;
        Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;
        Microsoft.Win32.SystemEvents.SessionEnding += OnSessionEnding;

        RebuildMenu();
        _controller.Start();

        ShowStartupGuidance();
    }

    /// <summary>
    /// 启动自检。
    ///
    /// 托盘图标注册失败时是**完全静默**的（.NET 的 NotifyIcon 会吞掉 Shell_NotifyIcon 的返回值），
    /// 用户只会看到"程序好像启动了，但哪儿都没有"。所以这里主动查一次返回值：
    /// 失败就把原因和正确的启动方式讲清楚，成功再提示 Windows 11 会折叠新图标。
    /// </summary>
    private void ShowStartupGuidance()
    {
        bool trayOk = MonitorSleep.Interop.EnvironmentProbe.CanAddTrayIcon(out int trayError);
        string integrity = MonitorSleep.Interop.EnvironmentProbe.DescribeIntegrityLevel();

        if (!trayOk)
        {
            // 受限环境（低完整性）下系统一律禁止注册托盘图标，程序绕不开。
            // 解释一次就够了；之后改为直接把设置窗口打开 —— 否则这个程序一旦
            // 在托盘里"消失"，用户就再也没有任何入口能碰到它了。
            if (!_controller.Settings.TrayGuidanceShown)
            {
                _controller.Settings.TrayGuidanceShown = true;
                _controller.SaveSettings();
                TrayGuidance.ShowEnvironmentBlocked(
                    trayError,
                    MonitorSleep.Interop.EnvironmentProbe.DescribeIntegrityLevel(),
                    _controller.Store.LastError,
                    Path.Combine(AppContext.BaseDirectory, "MonitorSleep.exe"));
            }

            ShowSettings();
            return;
        }

        if (!_controller.Settings.TrayGuidanceShown)
        {
            _controller.Settings.TrayGuidanceShown = true;
            _controller.SaveSettings();
            TrayGuidance.ShowFirstRun();
        }
    }

    // ───────────────────────── 菜单 ─────────────────────────

    private void RebuildMenu()
    {
        _menu.Items.Clear();

        var s = _controller.Settings;

        var status = new ToolStripMenuItem(_controller.StatusText) { Enabled = false };
        _menu.Items.Add(status);

        if (_controller.ScheduledOffAt is DateTime at)
        {
            int mins = Math.Max(0, (int)(at - DateTime.Now).TotalMinutes);
            _menu.Items.Add(new ToolStripMenuItem($"「{mins} 分钟后关屏」已排定") { Enabled = false });
        }

        _menu.Items.Add(new ToolStripSeparator());

        var sleepItem = new ToolStripMenuItem(
            _controller.Settings.ShowCountdown ? $"关屏（{_controller.Settings.CountdownSeconds} 秒倒计时）" : "关屏", null,
            (_, _) => _controller.SleepNow(SleepTrigger.Manual));
        _menu.Items.Add(sleepItem);
        _menu.Items.Add(new ToolStripMenuItem("唤醒屏幕", null,
            (_, _) => _controller.WakeNow()));
        _menu.Items.Add(new ToolStripMenuItem("让电脑睡眠", null,
            (_, _) => _controller.SystemSleep()));

        var timed = new ToolStripMenuItem("定时关屏");
        foreach (int minutes in new[] { 5, 15, 30, 45, 60, 90, 120 })
        {
            int m = minutes;
            timed.DropDownItems.Add(new ToolStripMenuItem($"{m} 分钟后", null,
                (_, _) => _controller.ScheduleOff(TimeSpan.FromMinutes(m))));
        }
        timed.DropDownItems.Add(new ToolStripSeparator());
        var cancelTimer = new ToolStripMenuItem("取消定时", null, (_, _) => _controller.CancelScheduledOff())
        {
            Enabled = _controller.ScheduledOffAt is not null,
        };
        timed.DropDownItems.Add(cancelTimer);
        _menu.Items.Add(timed);

        _menu.Items.Add(new ToolStripSeparator());

        var idleItem = new ToolStripMenuItem("空闲自动关屏") { Checked = s.IdleAutoOffEnabled };
        idleItem.Click += (_, _) =>
        {
            _controller.Settings.IdleAutoOffEnabled = !_controller.Settings.IdleAutoOffEnabled;
            _controller.SaveSettings();
            OnStateChanged();
        };
        _menu.Items.Add(idleItem);

        var takeOverItem = new ToolStripMenuItem("接管系统关屏") { Checked = s.TakeOverSystemTimeout };
        takeOverItem.Click += (_, _) =>
        {
            _controller.Settings.TakeOverSystemTimeout = !_controller.Settings.TakeOverSystemTimeout;
            _controller.SaveSettings();
            _controller.ApplyTakeOverSetting();
        };
        _menu.Items.Add(takeOverItem);

        var preventSleepItem = new ToolStripMenuItem("关屏时保持系统运行") { Checked = s.PreventSystemSleep };
        preventSleepItem.Click += (_, _) =>
        {
            _controller.Settings.PreventSystemSleep = !_controller.Settings.PreventSystemSleep;
            _controller.SaveSettings();
            OnStateChanged();
        };
        _menu.Items.Add(preventSleepItem);

        var blackoutItem = new ToolStripMenuItem("黑屏遮盖（兜底）", null, (_, _) => ShowOverlay());
        _menu.Items.Add(blackoutItem);

        _menu.Items.Add(new ToolStripSeparator());

        _menu.Items.Add(new ToolStripMenuItem("设置…", null, (_, _) => ShowSettings()));
        _menu.Items.Add(new ToolStripMenuItem("让图标常驻通知区域…", null, (_, _) => TrayGuidance.OpenTaskbarSettings()));
        _menu.Items.Add(new ToolStripMenuItem("打开配置文件夹", null, (_, _) => OpenDataFolder()));
        _menu.Items.Add(new ToolStripMenuItem("关于", null, (_, _) => ShowAbout()));

        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem("退出", null, (_, _) => ExitApplication()));
    }

    // ───────────────────────── 注入给控制器用的 UI 能力 ─────────────────────────

    private void Notify(string title, string text)
    {
        try
        {
            _icon.ShowBalloonTip(6000, title, Truncate(text, 250), ToolTipIcon.Info);
        }
        catch
        {
            // 通知失败不影响主流程
        }
    }

    private bool ShowCountdown(int seconds, string textTemplate, Action onElapsed, Action onCancelled)
    {
        try
        {
            _countdown?.Close();
            var form = new CountdownForm(seconds, textTemplate, onElapsed, onCancelled);
            _countdown = form;
            form.FormClosed += (_, _) => { if (ReferenceEquals(_countdown, form)) _countdown = null; };
            form.Show();
            return true;
        }
        catch
        {
            _countdown = null;
            return false;
        }
    }

    private bool ShowOverlay()
    {
        try
        {
            CloseOverlays();
            foreach (var screen in Screen.AllScreens)
            {
                var form = new BlackoutForm(screen);
                form.FormClosed += (_, _) => _overlays.Remove(form);
                _overlays.Add(form);
                form.Show();
            }
            return _overlays.Count > 0;
        }
        catch
        {
            return false;
        }
    }

    private void CloseOverlays()
    {
        foreach (var form in _overlays.ToArray())
        {
            try { form.Close(); } catch { /* 忽略 */ }
        }
        _overlays.Clear();
    }

    private void ShowSettings(string? initialTab = null)
    {
        try
        {
            using var form = new SettingsForm(_controller, initialTab);
            form.ShowDialog();
            OnStateChanged();
        }
        catch (Exception ex)
        {
            // 这里绝不能用气泡提示：托盘图标注册失败时气泡会石沉大海，
            // 用户只会看到"点了没反应"，连出错都不知道。
            try
            {
                MessageBox.Show(
                    "打开设置窗口失败。\n\n" + ex,
                    "显示器睡眠助手", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch
            {
                // 连弹框都失败就只能放弃了
            }
        }
    }

    private void OpenDataFolder()
    {
        try
        {
            Directory.CreateDirectory(_store.DataDirectory);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_store.DataDirectory}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Notify("打开配置文件夹失败", ex.Message);
        }
    }

    /// <summary>「关于」现在是设置窗口里的一个标签页，直接切过去即可。</summary>
    private void ShowAbout() => ShowSettings("关于");

    private void ExitApplication()
    {
        try
        {
            CloseOverlays();
            _countdown?.Close();
            _icon.Visible = false;
        }
        catch { /* 忽略 */ }
        ExitThread();
    }

    // ───────────────────────── 状态刷新 ─────────────────────────

    private void OnStateChanged()
    {
        try
        {
            _icon.Icon = _controller.IsDisplayOff ? _iconOff : _iconOn;
            _icon.Text = Truncate(_controller.TooltipText, 120);
        }
        catch
        {
            // 忽略
        }
    }

    // ───────────────────────── 系统事件 ─────────────────────────

    private void OnSessionSwitch(object sender, Microsoft.Win32.SessionSwitchEventArgs e)
    {
        if (e.Reason is Microsoft.Win32.SessionSwitchReason.SessionUnlock
                     or Microsoft.Win32.SessionSwitchReason.ConsoleConnect
                     or Microsoft.Win32.SessionSwitchReason.RemoteConnect)
        {
            _controller.NoteUserReturned();
        }
    }

    private void OnPowerModeChanged(object sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        if (e.Mode == Microsoft.Win32.PowerModes.Resume)
            _controller.NoteResumed();
    }

    private void OnSessionEnding(object sender, Microsoft.Win32.SessionEndingEventArgs e)
    {
        // 注销 / 关机：把系统关屏设置还回去
        _controller.PrepareForShutdown();
    }

    // ───────────────────────── 清理 ─────────────────────────

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;

            Microsoft.Win32.SystemEvents.SessionSwitch -= OnSessionSwitch;
            Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            Microsoft.Win32.SystemEvents.SessionEnding -= OnSessionEnding;

            _showSettingsPoller.Stop();
            _showSettingsPoller.Dispose();

            _controller.StateChanged -= OnStateChanged;
            _controller.Dispose();

            CloseOverlays();

            _icon.Visible = false;
            _icon.Dispose();
            _menu.Dispose();
            _iconOn.Dispose();
            _iconOff.Dispose();
        }
        base.Dispose(disposing);
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];
}
