using System.Diagnostics;
using System.Text.Json;
using MonitorSleep.Core;

namespace MonitorSleep.UI;

/// <summary>设置窗口。编辑的是一份副本，点「确定 / 应用」才写回。</summary>
internal sealed class SettingsForm : Form
{
    private readonly MonitorController _controller;
    private AppSettings _draft;

    // 基本
    private readonly CheckBox _idleEnabled = new() { Text = "空闲时自动关屏", AutoSize = true };
    private readonly NoWheelNumericUpDown _idleMinutes = new() { Minimum = 1, Maximum = 600, Width = 90 };
    private readonly NoWheelNumericUpDown _graceSeconds = new() { Minimum = 0, Maximum = 3600, Width = 90 };
    private readonly ComboBox _powerPolicy = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
    private readonly CheckBox _hotKeysEnabled = new() { Text = "启用全局热键", AutoSize = true };
    private readonly HotKeyBox _hkSleep = new();
    private readonly HotKeyBox _hkSystemSleep = new();

    // 避让
    private readonly CheckBox _blockFullScreen = new() { Text = "全屏程序 / 演示模式时保持常亮", AutoSize = true };
    private readonly CheckBox _blockAudio = new() { Text = "正在播放音频时保持常亮（注意：分不清视频和音乐，听歌时也会不关屏）", AutoSize = true };
    private readonly CheckBox _blockCapture = new() { Text = "摄像头或麦克风使用中保持常亮（视频通话）", AutoSize = true };
    private readonly CheckBox _blockRemote = new() { Text = "远程桌面会话中不自动关屏", AutoSize = true };
    private readonly TextBox _blockProcesses = new()
    {
        Multiline = true, Height = 110, ScrollBars = ScrollBars.Vertical,
        PlaceholderText = "每行一个进程名，例如：\r\nobs64\r\nvmware-vmx\r\nsteam",
    };

    // 电源
    private readonly CheckBox _takeOver = new() { Text = "接管系统关屏（把系统超时设为「从不」，改由本程序判断）", AutoSize = true };
    private readonly CheckBox _preventSleep = new() { Text = "屏幕关闭期间阻止系统进入睡眠（下载 / 转码 / 远程连接）", AutoSize = true };
    private readonly Label _powerStatus = new() { AutoSize = true, ForeColor = SystemColors.GrayText };
    private readonly Label _displayState = new() { AutoSize = true, ForeColor = SystemColors.GrayText };
    private readonly Label _autoOffState = new() { AutoSize = true, ForeColor = SystemColors.GrayText };

    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    private readonly FlowLayoutPanel _bottom = new()
    {
        Dock = DockStyle.Bottom,
        FlowDirection = FlowDirection.RightToLeft,   // RightToLeft 下，先添加的排在最右
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        WrapContents = false,
        Padding = new Padding(14, 10, 14, 14),
    };
    private readonly Label _takeOverRisk = new()
    {
        AutoSize = true,
        MaximumSize = new Size(540, 0),
        ForeColor = Color.FromArgb(200, 80, 0),
        Visible = false,
        Margin = new Padding(3, 2, 3, 8),
    };

    // 行为
    private readonly CheckBox _showCountdown = new() { Text = "关屏前显示倒计时（可取消，手动触发也生效）", AutoSize = true };
    private readonly NoWheelNumericUpDown _countdownSeconds = new() { Minimum = 1, Maximum = 120, Width = 90 };
    private readonly CheckBox _overlay = new() { Text = "关屏失败时改用全屏黑窗遮盖兜底", AutoSize = true };
    private readonly CheckBox _autoStart = new() { Text = "开机自动启动", AutoSize = true };
    private readonly CheckBox _suppressWake = new() { Text = "抑制伪唤醒（屏幕被无效输入点亮时自动关回去）", AutoSize = true };
    private readonly CheckBox _resleepWake = new() { Text = "电脑被无效输入唤醒时，让它继续睡", AutoSize = true };

    // ── 定时关屏 / 定时睡眠 ──
    private static readonly int[] TimerMinutes = { 5, 15, 30, 45, 60, 90, 120 };

    private readonly ComboBox _offTimerDelay = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 };
    private readonly ComboBox _sleepTimerDelay = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 };
    // 垂直内边距比普通按钮小 1px：下拉框的高度由字体决定（约 27px），
    // 用 3px 内边距会让「排定」比它高 4px，同一行里顶边就错开了
    private readonly Button _offTimerSet = new() { Text = "排定", AutoSize = true, Padding = new Padding(10, 2, 10, 2) };
    private readonly Button _sleepTimerSet = new() { Text = "排定", AutoSize = true, Padding = new Padding(10, 2, 10, 2) };
    private readonly Button _timerCancel = new() { Text = "取消定时", AutoSize = true, Padding = new Padding(10, 2, 10, 2) };
    private readonly Label _timerStatus = new() { AutoSize = true, ForeColor = SystemColors.GrayText };

    // 顶部品牌区的图标（跟随屏幕开 / 关切换，和托盘图标同一套画法）
    private PictureBox? _brandIcon;
    private bool _brandIconShowsOff;

    /// <param name="initialTab">打开时选中哪个标签页（按标题匹配），null 表示第一个。</param>
    public SettingsForm(MonitorController controller, string? initialTab = null)
    {
        _controller = controller;
        _draft = Clone(controller.Settings);

        Text = "显示器睡眠助手 — 设置";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        // 初始尺寸只是个起点，窗口高度随后会跟着当前页内容自动收放（见 FitToCurrentPage）
        ClientSize = new Size(640, 560);
        ShowInTaskbar = true;

        _powerPolicy.Items.AddRange(new object[]
        {
            "交流 / 电池都自动关屏",
            "只在电池供电时自动关屏",
            "只在插电时自动关屏",
        });

        foreach (int minutes in TimerMinutes)
        {
            _offTimerDelay.Items.Add($"{minutes} 分钟后");
            _sleepTimerDelay.Items.Add($"{minutes} 分钟后");
        }
        _offTimerDelay.SelectedIndex = 0;
        _sleepTimerDelay.SelectedIndex = 0;

        // 定时是立即生效的动作（和「立即关屏」一样），不走「确定 / 应用」
        _timerCancel.Click += (_, _) => _controller.CancelAllScheduled();

        _tabs.TabPages.Add(BuildBasicPage());
        _tabs.TabPages.Add(BuildAutoOffPage());    // 空闲自动关屏从第一页挪走
        _tabs.TabPages.Add(BuildAvoidPage());
        _tabs.TabPages.Add(BuildBehaviorPage());
        _tabs.TabPages.Add(BuildAdvancedPage());   // 会改系统设置的选项放最后，不占显眼位置
        _tabs.TabPages.Add(BuildAboutPage());

        if (initialTab is not null)
        {
            foreach (TabPage page in _tabs.TabPages)
            {
                if (page.Text == initialTab) { _tabs.SelectedTab = page; break; }
            }
        }

        // 表格只占内容高度（见 NewTable），内容超出时才由标签页自己滚动
        foreach (TabPage page in _tabs.TabPages) page.AutoScroll = true;

        // 切页时把窗口高度贴到该页内容高度，短页面就不会留一大片空白
        _tabs.SelectedIndexChanged += (_, _) => FitToCurrentPage();

        // 按钮尺寸交给内容决定：写死 Width/Height 在高 DPI 下会把中文切掉
        static Button MakeButton(string text) => new()
        {
            Text = text,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(104, 34),
            Margin = new Padding(8, 4, 0, 4),
            Padding = new Padding(12, 0, 12, 0),
        };

        // 两个页面各有一条状态行，用一个计时器统一刷新
        var stateTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        stateTimer.Tick += (_, _) => RefreshStateLabels();
        stateTimer.Start();
        Disposed += (_, _) => { stateTimer.Stop(); stateTimer.Dispose(); };

        var ok = MakeButton("确定");
        var cancel = MakeButton("取消");
        var apply = MakeButton("应用");

        ok.Click += (_, _) => { if (Commit()) { DialogResult = DialogResult.OK; Close(); } };
        apply.Click += (_, _) => Commit();
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };

        // 倒着添加，视觉顺序才是「确定 取消 应用」
        _bottom.Controls.Add(apply);
        _bottom.Controls.Add(cancel);
        _bottom.Controls.Add(ok);

        Controls.Add(_tabs);
        Controls.Add(_bottom);

        LoadFromDraft();
    }

    private static AppSettings Clone(AppSettings s) =>
        JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(s)) ?? new AppSettings();

    // ───────────────────────── 页面构建 ─────────────────────────

    private static TableLayoutPanel NewTable()
    {
        // 用 Dock=Top + AutoSize：表格高度严格等于内容高度。
        // 之前用 Dock=Fill 时，内容比容器矮的页面会把多余高度塞到最后一行前面，
        // 看起来就是页面中间凭空多出一大片空白。
        var t = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            Padding = new Padding(14),
        };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 230));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return t;
    }

    /// <summary>分组之间的一条细线，让各组的界限一眼能看出来。</summary>
    private static void Separator(TableLayoutPanel t)
    {
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        int r = t.RowStyles.Count - 1;

        var line = new Panel
        {
            Height = 1,
            BackColor = Color.FromArgb(212, 212, 212),
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
            Margin = new Padding(3, 16, 3, 0),
        };
        t.Controls.Add(line, 0, r);
        t.SetColumnSpan(line, 2);
    }

    private static void Header(TableLayoutPanel t, string text)
    {
        // 第一组之前不画线（页面顶部不需要），之后每组都先来一条
        if (t.RowStyles.Count > 0) Separator(t);

        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        int r = t.RowStyles.Count - 1;
        var lbl = new Label
        {
            Text = text,
            AutoSize = true,
            Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold),
            Margin = new Padding(3, 6, 3, 6),   // 上方的留白交给分隔线提供
        };
        t.Controls.Add(lbl, 0, r);
        t.SetColumnSpan(lbl, 2);
    }

    private static void Row(TableLayoutPanel t, string label, Control c)
    {
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        int r = t.RowStyles.Count - 1;
        var lbl = new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 9, 3, 3) };
        c.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        c.Margin = new Padding(3, 5, 3, 5);
        t.Controls.Add(lbl, 0, r);
        t.Controls.Add(c, 1, r);
    }

    private static void Span(TableLayoutPanel t, Control c)
    {
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        int r = t.RowStyles.Count - 1;
        c.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        c.Margin = new Padding(3, 6, 3, 6);
        t.Controls.Add(c, 0, r);
        t.SetColumnSpan(c, 2);
    }

    /// <summary>
    /// 打开时把每个标签页滚回顶部。
    /// 否则首个获得焦点的控件会把内容往上顶，「快速操作」那一段就被推到视野之外，
    /// 看起来像根本没有那两个按钮。
    /// </summary>
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        ActiveControl = null;

        var pages = Controls.OfType<TabControl>().SelectMany(t => t.TabPages.Cast<TabPage>());
        foreach (Control page in pages)
        {
            if (page is ScrollableControl scrollPage)
                scrollPage.AutoScrollPosition = Point.Empty;

            foreach (Control child in page.Controls)
            {
                // AutoScrollPosition 属于 ScrollableControl，不是所有 Control 都有
                if (child is ScrollableControl scrollChild)
                    scrollChild.AutoScrollPosition = Point.Empty;
            }
        }

        FitToCurrentPage();
    }

    private TabPage BuildBasicPage()
    {
        var t = NewTable();

        Span(t, BuildBrandHeader());

        Header(t, "快速操作");

        // 不放"唤醒屏幕"按钮：动一下键鼠屏幕就亮了，单独一个按钮没有意义
        var sleepNow = new Button { Text = "立即关屏", AutoSize = true, Padding = new Padding(10, 3, 10, 3) };
        var sleepPc = new Button { Text = "电脑睡眠", AutoSize = true, Padding = new Padding(10, 3, 10, 3) };
        sleepNow.Click += (_, _) => _controller.SleepNow(SleepTrigger.Manual);
        sleepPc.Click += (_, _) => _controller.SystemSleep();
        Span(t, InlineRow(sleepNow, sleepPc));

        Header(t, "关屏前倒计时");
        Span(t, _showCountdown);
        Row(t, "倒计时秒数", _countdownSeconds);

        // 定时关屏 / 定时睡眠 —— 和托盘菜单里那两项是同一套逻辑，
        // 放在这里是因为托盘图标可能被 Win11 收进「^」折叠区，不好找。
        Header(t, "定时");
        _offTimerSet.Click += (_, _) => ScheduleDelay(_offTimerDelay, _controller.ScheduleOff);
        _sleepTimerSet.Click += (_, _) => ScheduleDelay(_sleepTimerDelay, _controller.ScheduleSleep);

        Row(t, "关屏", InlineRow(_offTimerDelay, _offTimerSet));
        Row(t, "睡眠", InlineRow(_sleepTimerDelay, _sleepTimerSet));
        Row(t, "", InlineRow(_timerCancel));   // 放进值列，和上面的下拉框左对齐；不要拉满整行
        Span(t, _timerStatus);

        Header(t, "全局热键");
        Span(t, _hotKeysEnabled);
        Row(t, "关屏", _hkSleep);
        Row(t, "电脑睡眠", _hkSystemSleep);
        Span(t, new Label
        {
            Text = "点进输入框后直接按组合键即可录入；按 Backspace 或 Delete 清除。至少需要一个修饰键。\r\n"
                 + "唤醒屏幕不需要热键 —— 动一下键鼠就会亮。",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
        });

        return new TabPage("基本") { Controls = { t } };
    }

    /// <summary>
    /// 「自动关屏」页 —— 从第一页挪过来的。
    /// 空闲时长和倒计时属于"调好了就很少动"的参数，占着首页反而挤掉了常用操作。
    /// </summary>
    /// <summary>
    /// 「基本」页顶部的品牌区：程序图标 + 名称 + 当前状态。
    ///
    /// 图标复用托盘那套绘制代码（TrayIconFactory），所以这里和托盘长得一模一样，
    /// 而且会跟着屏幕开关变色。
    /// </summary>
    private Control BuildBrandHeader()
    {
        _brandIconShowsOff = _controller.IsDisplayOff;

        _brandIcon = new PictureBox
        {
            Image = TrayIconFactory.Draw(48, _brandIconShowsOff),
            SizeMode = PictureBoxSizeMode.AutoSize,
            Margin = new Padding(0, 2, 14, 0),
        };

        var text = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Margin = new Padding(0, 4, 0, 0),
        };
        text.Controls.Add(new Label
        {
            Text = "显示器睡眠助手",
            AutoSize = true,
            Font = new Font("Microsoft YaHei UI", 12f, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 1),
        });
        text.Controls.Add(_displayState);   // 「屏幕开启 / 屏幕已关闭」

        var row = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Margin = new Padding(3, 2, 3, 8),
        };
        row.Controls.Add(_brandIcon);
        row.Controls.Add(text);
        return row;
    }

    private TabPage BuildAutoOffPage()
    {
        var t = NewTable();

        Header(t, "当前状态");
        Span(t, _autoOffState);

        Header(t, "空闲自动关屏");
        Span(t, _idleEnabled);
        Row(t, "空闲多少分钟后关屏", _idleMinutes);
        Row(t, "解锁后的静默期（秒）", _graceSeconds);
        Row(t, "电源策略", _powerPolicy);

        Header(t, "伪唤醒抑制");
        Span(t, _suppressWake);
        Span(t, _resleepWake);
        Span(t, new Label
        {
            Text = "有些无线鼠标每隔几分钟会切换一次节能模式，期间发出一条「零位移」报告。\n"
                 + "Windows 把它当成用户回来了，于是把刚关掉的屏幕点亮、甚至把电脑从睡眠里叫醒。\n"
                 + "打开后本程序会识别这种无效输入：屏幕被点亮就关回去，电脑被叫醒就让它继续睡。\n"
                 + "只有真实的鼠标移动、按键或键盘操作才会打断它；连续抑制 8 次 / 送回睡眠 3 次后自动放弃。\n"
                 + "恢复后会留 6 秒反应时间，够你动一下鼠标把它取消。\n"
                 + "先试设备管理器和鼠标驱动的省电设置，那些才是根治；这一项是兜底。",
            AutoSize = true,
            MaximumSize = new Size(540, 0),
            ForeColor = SystemColors.GrayText,
        });

        return new TabPage("自动关屏") { Controls = { t } };
    }

    private TabPage BuildAvoidPage()
    {
        var t = NewTable();
        Header(t, "什么时候不要关屏");
        Span(t, _blockFullScreen);
        Span(t, _blockAudio);
        Span(t, _blockCapture);
        Span(t, _blockRemote);

        Header(t, "进程黑名单");
        Span(t, new Label
        {
            Text = "以下进程在运行时保持屏幕常亮（每行一个，可省略 .exe）：",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
        });
        Span(t, _blockProcesses);

        return new TabPage("智能避让") { Controls = { t } };
    }

    /// <summary>
    /// 「高级」页 —— 刻意放在最后。
    /// 这里的选项会改动系统级设置、而且有失效风险，不该出现在最显眼的位置。
    /// </summary>
    private TabPage BuildAdvancedPage()
    {
        var t = NewTable();

        Header(t, "接管系统关屏");
        Span(t, new Label
        {
            // 只用 \n\n 分段，行内不手动断行 —— 交给 MaximumSize 去换行，否则排版会碎
            Text = "先说清楚：Windows 自带的「N 分钟后关闭显示」并不笨。"
                 + "浏览器播视频、播放器、PowerPoint 放映都会主动告诉系统「现在别关屏」，"
                 + "所以大多数情况下并不需要接管。\n\n"
                 + "而且接管有风险：它把系统超时改成「从不」，一旦本程序被强杀、被删除、"
                 + "或判断出错，屏幕就再也不会自动关闭 —— 反而比原来的设置更费电。"
                 + "只有「某个不主动抑制关屏的程序确实需要屏幕长亮」时才值得打开。\n\n"
                 + "退出本程序会把系统设置还回去；崩溃后下次启动也会自动还原。",
            AutoSize = true,
            MaximumSize = new Size(540, 0),
            ForeColor = SystemColors.GrayText,
        });
        Span(t, _takeOver);
        Span(t, _powerStatus);
        Span(t, _takeOverRisk);

        var restoreNow = new Button { Text = "立即还原系统关屏设置", AutoSize = true, Padding = new Padding(10, 3, 10, 3) };
        restoreNow.Click += (_, _) =>
        {
            bool prev = _draft.TakeOverSystemTimeout;
            _draft.TakeOverSystemTimeout = false;
            _controller.ApplySettings(_draft);
            _controller.ApplyTakeOverSetting();
            _draft.TakeOverSystemTimeout = prev;
            _takeOver.Checked = false;
            RefreshPowerStatus();
            MessageBox.Show(this, "已还原系统关屏设置。", "显示器睡眠助手",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        };
        // 用 InlineRow 包一层：Span 会把这个按钮拉伸到整行宽，和上面的左对齐文字不搭
        Span(t, InlineRow(restoreNow));

        Header(t, "系统睡眠");
        Span(t, _preventSleep);
        Span(t, new Label
        {
            Text = "只在屏幕已关闭期间生效；屏幕一亮就把睡眠权还给 Windows。",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
        });

        // 这三项任一变化都会影响"接管是否划算"，随手刷新提示
        _takeOver.CheckedChanged += (_, _) => RefreshPowerStatus();
        _idleEnabled.CheckedChanged += (_, _) => RefreshPowerStatus();
        _idleMinutes.ValueChanged += (_, _) => RefreshPowerStatus();

        return new TabPage("高级") { Controls = { t } };
    }

    private TabPage BuildBehaviorPage()
    {
        var t = NewTable();
        Header(t, "兜底");
        Span(t, _overlay);

        Header(t, "启动");
        Span(t, _autoStart);
        Span(t, new Label
        {
            Text = "开机自启写在当前用户的注册表启动项里，不需要管理员权限。",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
        });

        return new TabPage("行为") { Controls = { t } };
    }

    /// <summary>「关于」页 —— 版本、热键、设计说明与联系方式。</summary>
    private TabPage BuildAboutPage()
    {
        var t = NewTable();

        Header(t, "显示器睡眠助手");
        Span(t, new Label
        {
            Text = $"版本 {typeof(SettingsForm).Assembly.GetName().Version?.ToString(3) ?? "0.1.0"}",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
        });
        Span(t, new Label
        {
            Text = "按需关闭显示器省电，并在不该关屏时保持屏幕常亮；也可以让整台电脑进入睡眠。",
            AutoSize = true,
            MaximumSize = new Size(540, 0),
            Margin = new Padding(3, 2, 3, 8),
        });

        Header(t, "当前热键");
        Row(t, "关屏", new Label { Text = DescribeBinding(_controller.Settings.SleepHotKey), AutoSize = true });
        Row(t, "电脑睡眠", new Label { Text = DescribeBinding(_controller.Settings.SystemSleepHotKey), AutoSize = true });
        Span(t, new Label
        {
            Text = _controller.Settings.HotKeysEnabled
                ? "可在「基本」页自定义。"
                : "全局热键当前已停用（见「基本」页）。",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
        });

        Header(t, "几点说明");
        Span(t, new Label
        {
            // 只按条目换行，条目内部交给控件自动折行 —— 混着手工换行会出现半行孤字
            Text = "· 关屏 ≠ 锁屏：本程序只关显示器，系统和程序继续运行。\n"
                 + "· 「接管系统关屏」默认关闭 —— 保留 Windows 自己的超时作为兜底，"
                 + "即使本程序出错或没在运行，屏幕也不会永远不关。\n"
                 + "· 退出或崩溃后，被改动过的系统关屏设置都会自动还原。",
            AutoSize = true,
            MaximumSize = new Size(540, 0),
        });

        Header(t, "联系方式");
        // 昵称与邮箱同一行：昵称是普通文字，邮箱是可点的链接
        var contact = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Margin = new Padding(3, 2, 3, 4),
        };
        contact.Controls.Add(new Label
        {
            Text = "见义勇为的猫",
            AutoSize = true,
            Margin = new Padding(3, 5, 12, 0),
        });
        var mail = new LinkLabel
        {
            Text = "34219585@qq.com",
            AutoSize = true,
            Margin = new Padding(0, 5, 3, 0),
        };
        mail.LinkClicked += (_, _) => OpenMailTo(mail.Text);
        contact.Controls.Add(mail);
        Span(t, contact);

        Header(t, "配置目录");
        Span(t, new Label
        {
            // 用 %APPDATA% 缩写：完整路径在窗口里会从中间断开，很难看
            Text = ShortenPath(_controller.Store.DataDirectory),
            AutoSize = true,
            MaximumSize = new Size(540, 0),
            ForeColor = SystemColors.GrayText,
        });
        var openFolder = new Button { Text = "打开配置文件夹", AutoSize = true, Padding = new Padding(10, 3, 10, 3) };
        openFolder.Click += (_, _) => OpenFolder(_controller.Store.DataDirectory);
        Span(t, InlineRow(openFolder));   // 同上：不要拉满整行

        return new TabPage("关于") { Controls = { t } };
    }

    private static string DescribeBinding(HotKeyBinding binding) =>
        binding.IsValid ? binding.ToString() : "（未绑定）";

    /// <summary>
    /// 把路径缩写成短形式，省得在界面上从中间折断。
    ///
    /// 正常安装时配置在 %APPDATA%\MonitorSleep；
    /// 受限环境写不进去，会回退到 exe 旁边的 MonitorSleep-data。
    /// 两种都缩写。
    /// </summary>
    private static string ShortenPath(string path)
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (appData.Length > 0 && path.StartsWith(appData, StringComparison.OrdinalIgnoreCase))
            return "%APPDATA%" + path.Substring(appData.Length);

        string baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        if (baseDir.Length > 0 && path.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase))
            return "程序目录" + path.Substring(baseDir.Length);

        return path;
    }

    private static void OpenMailTo(string address)
    {
        try
        {
            Process.Start(new ProcessStartInfo($"mailto:{address}") { UseShellExecute = true });
        }
        catch
        {
            // 系统里没有邮件客户端就算了，地址本身在页面上可见
        }
    }

    private static void OpenFolder(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{directory}\"") { UseShellExecute = true });
        }
        catch
        {
            // 忽略
        }
    }

    // ───────────────────────── 数据绑定 ─────────────────────────

    private void LoadFromDraft()
    {
        _idleEnabled.Checked = _draft.IdleAutoOffEnabled;
        _idleMinutes.Value = Clamp(_draft.IdleMinutes, _idleMinutes);
        _graceSeconds.Value = Clamp(_draft.GraceAfterUnlockSeconds, _graceSeconds);
        _powerPolicy.SelectedIndex = (int)_draft.PowerPolicy;

        _hotKeysEnabled.Checked = _draft.HotKeysEnabled;
        _hkSleep.Binding = _draft.SleepHotKey;
        _hkSystemSleep.Binding = _draft.SystemSleepHotKey;

        _blockFullScreen.Checked = _draft.BlockOnFullScreen;
        _blockAudio.Checked = _draft.BlockOnAudio;
        _blockCapture.Checked = _draft.BlockOnCapture;
        _blockRemote.Checked = _draft.BlockOnRemoteSession;
        _blockProcesses.Lines = _draft.BlockProcesses.ToArray();

        _takeOver.Checked = _draft.TakeOverSystemTimeout;
        _preventSleep.Checked = _draft.PreventSystemSleep;

        _showCountdown.Checked = _draft.ShowCountdown;
        _countdownSeconds.Value = Clamp(_draft.CountdownSeconds, _countdownSeconds);
        _overlay.Checked = _draft.OverlayFallback;
        _suppressWake.Checked = _draft.SuppressSpuriousWake;
        _resleepWake.Checked = _draft.ResleepAfterSpuriousWake;
        _autoStart.Checked = AutostartService.IsEnabled();

        RefreshStateLabels();
        RefreshPowerStatus();
    }

    /// <summary>
    /// 把窗口高度贴到当前页的内容高度。
    /// 各页内容长短差很多，固定尺寸必然让短页面留一大片空白；
    /// 跟着内容收放，每页看起来才都是"刚好"。
    /// </summary>
    private void FitToCurrentPage()
    {
        if (_tabs.SelectedTab is not { } page) return;

        // 表格只占内容高度（见 NewTable），它自己报的高度就是这一页真正需要的高度
        int content = 0;
        foreach (Control child in page.Controls)
        {
            if (child is TableLayoutPanel table)
                content = Math.Max(content, table.PreferredSize.Height);
        }
        if (content <= 0) return;

        int chrome = _tabs.DisplayRectangle.Top + _bottom.Height;   // 标签头 + 按钮栏
        int target = content + chrome + 10;

        int maxHeight = Screen.FromControl(this).WorkingArea.Height - 80;
        target = Math.Min(target, Math.Max(maxHeight, 360));
        target = Math.Max(target, 340);

        if (Math.Abs(target - ClientSize.Height) < 4) return;
        ClientSize = new Size(ClientSize.Width, target);
    }

    private void RefreshStateLabels()
    {
        _displayState.Text = _controller.DisplayStateText;
        _autoOffState.Text = _controller.AutoOffStatusText;
        RefreshBrandIcon();
        RefreshTimerStatus();
    }

    /// <summary>屏幕开关状态变了就重画顶部图标 —— 只在切换时重画，不是每秒都来一次。</summary>
    private void RefreshBrandIcon()
    {
        if (_brandIcon is null) return;

        bool off = _controller.IsDisplayOff;
        if (off == _brandIconShowsOff) return;

        _brandIconShowsOff = off;
        var previous = _brandIcon.Image;
        _brandIcon.Image = TrayIconFactory.Draw(48, off);
        previous?.Dispose();
    }

    /// <summary>刷新「定时」那一块的排定状态与取消按钮可用性。</summary>
    private void RefreshTimerStatus()
    {
        var parts = new List<string>(2);

        if (_controller.ScheduledOffAt is DateTime offAt)
            parts.Add($"{Math.Max(0, (int)Math.Ceiling((offAt - DateTime.Now).TotalMinutes))} 分钟后关屏");

        if (_controller.ScheduledSleepAt is DateTime sleepAt)
            parts.Add($"{Math.Max(0, (int)Math.Ceiling((sleepAt - DateTime.Now).TotalMinutes))} 分钟后睡眠");

        _timerStatus.Text = parts.Count > 0
            ? "已排定：" + string.Join(" · ", parts)
            : "当前没有排定的定时。";

        _timerCancel.Enabled = parts.Count > 0;
    }

    /// <summary>
    /// 把若干控件横排成一行，保持各自的原生尺寸（不会被拉伸）。
    ///
    /// 关键是清掉控件自带的 3px 外边距：否则整行会比同列的单控件右移几像素，
    /// 和上一行的输入框对不齐，看着就是"错乱"。
    /// </summary>
    private static FlowLayoutPanel InlineRow(params Control[] items)
    {
        var row = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Margin = new Padding(3, 2, 3, 2),
        };

        for (int i = 0; i < items.Length; i++)
        {
            items[i].Margin = new Padding(0, 0, i == items.Length - 1 ? 0 : 8, 0);
            row.Controls.Add(items[i]);
        }
        return row;
    }

    private static void ScheduleDelay(ComboBox delay, Action<TimeSpan> schedule)
    {
        int index = Math.Clamp(delay.SelectedIndex, 0, TimerMinutes.Length - 1);
        schedule(TimeSpan.FromMinutes(TimerMinutes[index]));
    }

    private void RefreshPowerStatus()
    {
        _powerStatus.Text = _controller.PowerSchemeSummary + Environment.NewLine + _controller.PowerSchemeSavedSummary;
        _countdownSeconds.Enabled = _showCountdown.Checked;
        _idleMinutes.Enabled = _idleEnabled.Checked;

        // 用"当前控件状态"而不是已保存的配置来判断风险，
        // 这样用户一勾选、一改分钟数，就能立刻看到会不会亏
        var probe = Clone(_draft);
        probe.TakeOverSystemTimeout = _takeOver.Checked;
        probe.IdleAutoOffEnabled = _idleEnabled.Checked;
        probe.IdleMinutes = (int)_idleMinutes.Value;

        string? risk = MonitorController.DescribeTakeOverRisk(probe);
        _takeOverRisk.Visible = risk is not null;
        _takeOverRisk.Text = risk is null ? string.Empty : "⚠ " + risk;
    }

    private static decimal Clamp(int value, NumericUpDown box) =>
        Math.Min(box.Maximum, Math.Max(box.Minimum, value));

    private static decimal Clamp(decimal value, NumericUpDown box) =>
        Math.Min(box.Maximum, Math.Max(box.Minimum, value));

    // ───────────────────────── 提交 ─────────────────────────

    private bool Commit()
    {
        _draft.IdleAutoOffEnabled = _idleEnabled.Checked;
        _draft.IdleMinutes = (int)_idleMinutes.Value;
        _draft.GraceAfterUnlockSeconds = (int)_graceSeconds.Value;
        _draft.PowerPolicy = (PowerPolicy)Math.Max(0, _powerPolicy.SelectedIndex);

        _draft.HotKeysEnabled = _hotKeysEnabled.Checked;
        _draft.SleepHotKey = _hkSleep.Binding;
        _draft.SystemSleepHotKey = _hkSystemSleep.Binding;

        _draft.BlockOnFullScreen = _blockFullScreen.Checked;
        _draft.BlockOnAudio = _blockAudio.Checked;
        _draft.BlockOnCapture = _blockCapture.Checked;
        _draft.BlockOnRemoteSession = _blockRemote.Checked;
        _draft.BlockProcesses = _blockProcesses.Lines
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();

        bool takeOverChanged = _draft.TakeOverSystemTimeout != _takeOver.Checked;
        _draft.TakeOverSystemTimeout = _takeOver.Checked;
        _draft.PreventSystemSleep = _preventSleep.Checked;

        _draft.ShowCountdown = _showCountdown.Checked;
        _draft.CountdownSeconds = (int)_countdownSeconds.Value;
        _draft.OverlayFallback = _overlay.Checked;
        _draft.SuppressSpuriousWake = _suppressWake.Checked;
        _draft.ResleepAfterSpuriousWake = _resleepWake.Checked;

        // 开机自启直接落到注册表
        if (_autoStart.Checked != AutostartService.IsEnabled())
        {
            if (!AutostartService.SetEnabled(_autoStart.Checked, out string msg))
            {
                MessageBox.Show(this, msg, "显示器睡眠助手", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                _autoStart.Checked = AutostartService.IsEnabled();
            }
        }
        _draft.AutoStart = _autoStart.Checked;

        _controller.ApplySettings(_draft);

        if (takeOverChanged)
            _controller.ApplyTakeOverSetting();

        RefreshPowerStatus();
        return true;
    }
}

/// <summary>只读的按键录入框：聚焦后直接按下组合键即可。</summary>
internal sealed class HotKeyBox : TextBox
{
    private HotKeyBinding _binding = new();

    public HotKeyBox()
    {
        ReadOnly = true;
        Cursor = Cursors.Hand;
        TextAlign = HorizontalAlignment.Center;
        Width = 200;
    }

    public HotKeyBinding Binding
    {
        get => _binding;
        set
        {
            _binding = value ?? new HotKeyBinding();
            Text = _binding.ToString();
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        e.SuppressKeyPress = true;
        e.Handled = true;

        Keys key = e.KeyCode;
        if (key is Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.LWin or Keys.RWin or Keys.None)
            return;

        if (key is Keys.Back or Keys.Delete or Keys.Escape)
        {
            Binding = new HotKeyBinding();
            return;
        }

        Keys mods = Control.ModifierKeys;
        Binding = new HotKeyBinding
        {
            Ctrl = (mods & Keys.Control) != 0,
            Alt = (mods & Keys.Alt) != 0,
            Shift = (mods & Keys.Shift) != 0,
            Win = false,
            Key = (int)key,
        };
    }
}
