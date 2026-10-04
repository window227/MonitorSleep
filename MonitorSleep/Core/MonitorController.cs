using MonitorSleep.Interop;

namespace MonitorSleep.Core;

internal enum SleepTrigger
{
    /// <summary>用户显式点击 / 热键。</summary>
    Manual,
    /// <summary>空闲自动关屏 —— 走避让判断。</summary>
    Auto,
    /// <summary>"N 分钟后关屏" —— 走避让判断。</summary>
    Scheduled,
}

/// <summary>
/// 应用的大脑：持有配置、守卫规则、电源方案接管、热键，并用一个心跳驱动空闲判断。
/// 所有 UI 动作通过注入的委托回调出去，Core 层不直接依赖窗体。
/// </summary>
internal sealed class MonitorController : IDisposable
{
    /// <summary>关屏前要求键鼠安静这么久，避免"刚关就被自己的输入唤醒"。</summary>
    private static readonly TimeSpan QuietBeforeSleep = TimeSpan.FromMilliseconds(450);

    /// <summary>等待安静的上限：用户一直在动也不能无限期拖下去。</summary>
    private static readonly TimeSpan MaxQuietWait = TimeSpan.FromSeconds(3);

    private const string DisplayOffCountdownText = "显示器将在 {0} 秒后关闭";
    private const string SystemSleepCountdownText = "电脑将在 {0} 秒后进入睡眠";

    private readonly SettingsStore _store;
    private readonly GuardEvaluator _guards = new();
    private readonly PowerSchemeService _powerScheme;
    private readonly HotKeyWindow _hotKeys = new();
    private readonly System.Windows.Forms.Timer _tickTimer;

    private AppSettings _settings;
    private uint? _displayOffTick;
    private uint? _quietWaitSince;
    private SleepTrigger _quietWaitTrigger;
    private DateTime? _lastUnlockAt;
    private DateTime? _scheduledOffAt;
    private DateTime? _scheduledSleepAt;
    private bool _executionStateApplied;
    private bool _sleepPending;
    private bool _systemSleepPending;
    private bool _disposed;

    // ── 伪唤醒抑制 ──
    private RawInputWindow? _rawInput;
    private bool _displayWasOff;
    private uint _displayOffDetectedTick;
    private int _resuppressCount;

    /// <summary>连续抑制次数上限，避免和用户"打架"打成死循环。</summary>
    private const int MaxResuppress = 8;

    // ── 睡眠侧的伪唤醒抑制 ──
    private uint? _resumedTick;
    private int _resleepCount;

    /// <summary>连续送回睡眠的次数上限，同样防止和用户抢。</summary>
    private const int MaxResleep = 3;

    /// <summary>从睡眠恢复后留这么多秒反应时间，期间一有真实输入就放弃重新入睡。</summary>
    private static readonly TimeSpan ResleepGrace = TimeSpan.FromSeconds(6);

    public MonitorController(SettingsStore store)
    {
        _store = store;
        _settings = store.Load();
        _powerScheme = new PowerSchemeService(_settings);

        _tickTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _tickTimer.Tick += (_, _) => OnTick();

        // 后台窗口：收 Raw Input（区分真实输入与零位移伪报告）和显示器开关通知。
        // 起不来也不影响其它功能，只是伪唤醒抑制不可用。
        try { _rawInput = new RawInputWindow(); }
        catch { _rawInput = null; }
    }

    /// <summary>伪唤醒抑制当前是否真的可用（供诊断模式显示）。</summary>
    public string SuppressWakeStatus => _rawInput?.StatusText ?? "不可用：后台窗口创建失败";

    // ── 由 UI 层注入的回调 ──

    /// <summary>(秒数, 文案模板, 到时回调, 取消回调) → 是否已显示倒计时窗口。模板里的 {0} 是秒数。</summary>
    public Func<int, string, Action, Action, bool>? CountdownPresenter { get; set; }

    /// <summary>显示黑屏遮盖兜底。返回 true 表示已接管。</summary>
    public Func<bool>? OverlayPresenter { get; set; }

    /// <summary>(标题, 正文) 气泡通知。</summary>
    public Action<string, string>? Notifier { get; set; }

    /// <summary>状态变化，供托盘刷新提示文字与菜单勾选。</summary>
    public event Action? StateChanged;

    // ── 只读状态 ──

    public AppSettings Settings => _settings;
    public SettingsStore Store => _store;
    public bool IsDisplayOff => _displayOffTick is not null;
    public bool IsSleepQueued => _quietWaitSince is not null;
    public string LastBlockReason { get; private set; } = string.Empty;
    public DateTime? ScheduledOffAt => _scheduledOffAt;

    /// <summary>「N 分钟后睡眠」的到点时刻；null 表示没排定。</summary>
    public DateTime? ScheduledSleepAt => _scheduledSleepAt;
    public string PowerSchemeSummary => _powerScheme.DescribeCurrent();
    public string PowerSchemeSavedSummary => _powerScheme.DescribeSaved();

    /// <summary>屏幕自身的开关状态。</summary>
    public string DisplayStateText => _quietWaitSince is not null
        ? "正在关屏…"
        : _displayOffTick is not null ? "屏幕已关闭" : "屏幕开启";

    /// <summary>空闲自动关屏 / 定时关屏 / 定时睡眠的进度。</summary>
    public string AutoOffStatusText
    {
        get
        {
            var timers = new List<string>(2);
            if (_scheduledOffAt is DateTime offAt)
                timers.Add($"{Math.Max(0, (int)Math.Ceiling((offAt - DateTime.Now).TotalMinutes))} 分钟后关屏");
            if (_scheduledSleepAt is DateTime sleepAt)
                timers.Add($"{Math.Max(0, (int)Math.Ceiling((sleepAt - DateTime.Now).TotalMinutes))} 分钟后睡眠");
            if (timers.Count > 0)
                return string.Join(" · ", timers);

            if (!_settings.IdleAutoOffEnabled)
                return "空闲自动关屏已停用";

            var idle = NativeMethods.GetIdleTime();
            if (idle >= TimeSpan.FromMinutes(Math.Max(1, _settings.IdleMinutes)))
            {
                return LastBlockReason.Length > 0
                    ? $"空闲已达 {_settings.IdleMinutes} 分钟 · 保持中：{LastBlockReason}"
                    : $"空闲已达 {_settings.IdleMinutes} 分钟 · 待关屏";
            }

            return $"空闲 {_settings.IdleMinutes - (int)idle.TotalMinutes} 分钟后自动关屏";
        }
    }

    /// <summary>两段合起来 —— 托盘菜单和提示文字用这个。</summary>
    public string StatusText => $"{DisplayStateText} | {AutoOffStatusText}";

    public string TooltipText => _displayOffTick is not null
        ? "显示器睡眠助手 — 屏幕已关闭"
        : $"显示器睡眠助手 — {(_settings.IdleAutoOffEnabled ? $"空闲 {_settings.IdleMinutes} 分钟关屏" : "自动关屏已停用")}";

    // ── 生命周期 ──

    public void Start()
    {
        // 上次若是崩溃/强杀退出，系统关屏可能还停在"从不"，先补还原
        if (_powerScheme.RestoreIfPendingOnStartup(out string restoreMsg) && restoreMsg.Length > 0)
        {
            _store.Save(_settings);
            Notifier?.Invoke("显示器睡眠助手", "检测到上次未正常退出。" + restoreMsg);
        }

        // 失败必须让用户看见：静默失败会让用户以为"已经在接管了"，其实系统超时照旧生效。
        ApplyTakeOverSetting(quiet: false);
        ApplyHotKeys();
        _tickTimer.Start();
        StateChanged?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _tickTimer.Stop();
        _tickTimer.Dispose();

        _rawInput?.Dispose();
        _rawInput = null;

        // 释放"保持系统唤醒"
        try { NativeMethods.ApplyExecutionState(NativeMethods.ExecutionState.Continuous); }
        catch { /* 忽略 */ }
        _executionStateApplied = false;

        _hotKeys.Dispose();

        // 正常退出时必须把系统关屏设置还回去，否则系统永远不会自动关屏
        try
        {
            if (_powerScheme.Restore(out _))
                _store.Save(_settings);
        }
        catch { /* 忽略 */ }
    }

    // ── 外部动作入口 ──

    public void SleepNow(SleepTrigger trigger)
    {
        if (_displayOffTick is not null || _sleepPending || _quietWaitSince is not null) return;

        if (trigger != SleepTrigger.Manual)
        {
            var guard = _guards.Evaluate(_settings, _lastUnlockAt);
            if (guard.Blocked)
            {
                LastBlockReason = guard.ReasonText;
                Notifier?.Invoke("保持屏幕常亮", $"检测到{guard.ReasonText}，本次不关屏。");
                StateChanged?.Invoke();
                return;
            }
        }

        LastBlockReason = string.Empty;
        BeginSleep(trigger);
    }

    public void WakeNow()
    {
        _quietWaitSince = null;   // 取消排队中的关屏
        EndDisplayOffSession();

        NativeMethods.BroadcastMonitorPower(NativeMethods.MONITOR_ON);

        if (_settings.JiggleOnWake)
        {
            // 部分驱动不认 SC_MONITORPOWER(-1)，用 1 像素的相对移动把显示管线顶起来
            try
            {
                System.Threading.Thread.Sleep(120);
                NativeMethods.JiggleMouse();
            }
            catch { /* 忽略 */ }
        }

        ApplyExecutionState();
        StateChanged?.Invoke();
    }

    public void ScheduleOff(TimeSpan delay)
    {
        _scheduledOffAt = DateTime.Now + delay;
        StateChanged?.Invoke();
    }

    public void CancelScheduledOff()
    {
        _scheduledOffAt = null;
        StateChanged?.Invoke();
    }

    /// <summary>「N 分钟后让电脑睡眠」—— 和定时关屏对称。</summary>
    public void ScheduleSleep(TimeSpan delay)
    {
        _scheduledSleepAt = DateTime.Now + delay;
        StateChanged?.Invoke();
    }

    public void CancelScheduledSleep()
    {
        _scheduledSleepAt = null;
        StateChanged?.Invoke();
    }

    /// <summary>把两个定时器一起取消（托盘菜单里「取消定时」用）。</summary>
    public void CancelAllScheduled()
    {
        _scheduledOffAt = null;
        _scheduledSleepAt = null;
        StateChanged?.Invoke();
    }

    /// <summary>
    /// 检查"接管系统关屏"是否会带来净损失。返回 null 表示没问题。
    ///
    /// 接管把系统超时改成"从不"，所以只要本程序自己的阈值比系统原来的还长，
    /// 接管就是净变差 —— 屏幕反而亮得更久，这正是"省电工具干出反效果"。
    /// 另外若"空闲自动关屏"没开，接管等于让屏幕永远不关。
    /// </summary>
    public static string? DescribeTakeOverRisk(AppSettings settings)
    {
        if (!settings.TakeOverSystemTimeout) return null;

        if (!settings.IdleAutoOffEnabled)
            return "接管已开启，但「空闲自动关屏」是关闭的 —— 系统超时又已被改成「从不」，屏幕从此不会自动关闭。";

        int mineSeconds = Math.Max(1, settings.IdleMinutes) * 60;
        uint originalSeconds = Math.Max(settings.SavedAcVideoIdle, settings.SavedDcVideoIdle);

        if (originalSeconds > 0 && mineSeconds > originalSeconds)
            return $"接管后系统超时变成「从不」，而本程序的阈值是 {mineSeconds / 60} 分钟、" +
                   $"比系统原来的 {originalSeconds / 60} 分钟更长 —— 屏幕会比以前亮得更久。";

        return null;
    }

    /// <summary>
    /// 让电脑进入睡眠。和关屏对称：同样先走可取消的倒计时。
    ///
    /// 刻意不做"空闲自动触发" —— 睡眠会让所有程序暂停，误触的代价比关屏大得多，
    /// 所以只由用户显式发起（热键 / 按钮 / 托盘菜单）。
    /// </summary>
    public void SystemSleep()
    {
        if (_systemSleepPending) return;

        bool useCountdown = _settings.ShowCountdown
                            && _settings.CountdownSeconds > 0
                            && CountdownPresenter is not null;

        if (useCountdown)
        {
            _systemSleepPending = true;
            bool shown = CountdownPresenter!(
                _settings.CountdownSeconds,
                SystemSleepCountdownText,
                () => { _systemSleepPending = false; PerformSystemSleep(); },
                () => { _systemSleepPending = false; StateChanged?.Invoke(); });

            if (shown)
            {
                StateChanged?.Invoke();
                return;
            }
            _systemSleepPending = false;
        }

        PerformSystemSleep();
    }

    private void PerformSystemSleep()
    {
        // 先把显示器关掉，否则有些机器在进入睡眠的过程中屏幕会先亮着闪一下
        NativeMethods.BroadcastMonitorPower(NativeMethods.MONITOR_OFF);
        _displayOffTick = null;

        // 释放"保持系统唤醒"，不然它会挡住在即的睡眠
        if (_executionStateApplied)
        {
            try { NativeMethods.ApplyExecutionState(NativeMethods.ExecutionState.Continuous); }
            catch { /* 忽略 */ }
            _executionStateApplied = false;
        }

        if (!NativeMethods.TrySystemSleep(out string error))
            Notifier?.Invoke("进入睡眠失败", error);
    }

    /// <summary>用户在设置里改了"接管系统关屏"后调用。</summary>
    public void ApplyTakeOverSetting(bool quiet = false)
    {
        if (_settings.TakeOverSystemTimeout)
        {
            if (_powerScheme.TakeOver(out string msg))
            {
                _store.Save(_settings);

                // 接管有净损失时必须说出来，否则用户只会觉得"屏幕怎么不关了"
                string? risk = DescribeTakeOverRisk(_settings);
                if (risk is not null) Notifier?.Invoke("接管系统关屏：请注意", risk);
            }
            else if (!quiet)
            {
                Notifier?.Invoke("接管系统关屏失败", msg);
            }
        }
        else
        {
            if (_powerScheme.Restore(out string msg) && !quiet && msg.Length > 0)
                Notifier?.Invoke("显示器睡眠助手", msg);
            _store.Save(_settings);
        }
        StateChanged?.Invoke();
    }

    public void ApplyHotKeys()
    {
        _hotKeys.UnregisterAll();
        if (!_settings.HotKeysEnabled) return;

        _hotKeys.Register(HotKeyWindow.IdSleep, _settings.SleepHotKey, "关屏", () => SleepNow(SleepTrigger.Manual));
        _hotKeys.Register(HotKeyWindow.IdSystemSleep, _settings.SystemSleepHotKey, "电脑睡眠", SystemSleep);

        if (_hotKeys.Failures.Count > 0)
            Notifier?.Invoke("部分热键注册失败", string.Join(Environment.NewLine, _hotKeys.Failures));
    }

    /// <summary>来自 SystemEvents 的解锁/恢复通知（可能在非 UI 线程上）。</summary>
    public void NoteUserReturned() => _lastUnlockAt = DateTime.UtcNow;

    public void SaveSettings() => _store.Save(_settings);

    /// <summary>用设置窗口产出的新配置整体替换当前配置。</summary>
    public void ApplySettings(AppSettings next)
    {
        _settings = next;
        _powerScheme.Settings = next;
        _store.Save(_settings);
        ApplyExecutionState();
        ApplyHotKeys();
        StateChanged?.Invoke();
    }

    /// <summary>注销 / 关机前调用：务必把系统关屏设置还回去。</summary>
    public void PrepareForShutdown()
    {
        try
        {
            if (_powerScheme.Restore(out _))
                _store.Save(_settings);
        }
        catch
        {
            // 忽略
        }
    }

    // ── 内部实现 ──

    private void BeginSleep(SleepTrigger trigger)
    {
        // 手动触发同样走倒计时：按了热键也该有反悔的机会
        bool useCountdown = _settings.ShowCountdown
                            && _settings.CountdownSeconds > 0
                            && CountdownPresenter is not null;

        if (useCountdown)
        {
            _sleepPending = true;
            bool shown = CountdownPresenter!(
                _settings.CountdownSeconds,
                DisplayOffCountdownText,
                () => { _sleepPending = false; QueueSleep(trigger); },
                () => { _sleepPending = false; StateChanged?.Invoke(); });

            if (shown)
            {
                StateChanged?.Invoke();
                return;
            }
            _sleepPending = false;
        }

        QueueSleep(trigger);
    }

    /// <summary>
    /// 关屏前的最后一关：等键鼠彻底安静下来再关。
    ///
    /// 少了这一步就会"屏幕刚黑就立刻亮回来"：按下热键那一刻只是 KeyDown，
    /// 我们紧接着关屏，而随后松开的 KeyUp 本身就是一次输入，显示器立刻被唤醒。
    /// 托盘菜单的鼠标点击同理。所以先记下请求，等空闲时间达标再真正关屏。
    /// </summary>
    private void QueueSleep(SleepTrigger trigger)
    {
        _quietWaitTrigger = trigger;
        _quietWaitSince = unchecked((uint)Environment.TickCount);
        TryPerformQueuedSleep();
    }

    private bool TryPerformQueuedSleep()
    {
        if (_quietWaitSince is not uint since) return false;

        uint now = unchecked((uint)Environment.TickCount);
        var waited = TimeSpan.FromMilliseconds(unchecked(now - since));
        var idle = NativeMethods.GetIdleTime();

        if (idle < QuietBeforeSleep && waited < MaxQuietWait) return false;

        _quietWaitSince = null;
        PerformSleep(_quietWaitTrigger);
        return true;
    }

    private void PerformSleep(SleepTrigger trigger)
    {
        if (_displayOffTick is not null) return;

        bool ok = NativeMethods.BroadcastMonitorPower(NativeMethods.MONITOR_OFF);
        if (!ok)
        {
            if (_settings.OverlayFallback && OverlayPresenter is not null && OverlayPresenter())
                return;

            Notifier?.Invoke("关屏失败",
                "系统没有响应关屏请求（可能被全屏独占程序拦截）。可在设置中启用「黑屏遮盖兜底」。");
            return;
        }

        _displayOffTick = unchecked((uint)Environment.TickCount);
        ApplyExecutionState();
        StateChanged?.Invoke();
    }

    private void OnTick()
    {
        TrackDisplayState();
        TrackSpuriousWake();
        TrackSpuriousResume();

        // 有关屏请求在排队时用更快的节拍轮询，关起来才跟手
        _tickTimer.Interval = _quietWaitSince is not null ? 100 : 1000;
        TryPerformQueuedSleep();

        // 排队等待关屏期间，不再重复发起其它关屏
        if (_quietWaitSince is not null) return;

        // 「N 分钟后关屏」到点
        if (_scheduledOffAt is DateTime at && DateTime.Now >= at)
        {
            _scheduledOffAt = null;
            SleepNow(SleepTrigger.Scheduled);
        }

        // 「N 分钟后睡眠」到点。
        // 排在关屏之后：两者同时到点时，先关屏再睡，观感上不会闪。
        if (_scheduledSleepAt is DateTime sleepAt && DateTime.Now >= sleepAt)
        {
            _scheduledSleepAt = null;
            SystemSleep();
        }

        // 空闲自动关屏
        if (!_sleepPending && _displayOffTick is null && _settings.IdleAutoOffEnabled)
        {
            var idle = NativeMethods.GetIdleTime();
            if (idle >= TimeSpan.FromMinutes(Math.Max(1, _settings.IdleMinutes)))
            {
                var guard = _guards.Evaluate(_settings, _lastUnlockAt);
                LastBlockReason = guard.Blocked ? guard.ReasonText : string.Empty;
                if (!guard.Blocked) BeginSleep(SleepTrigger.Auto);
            }
            else if (LastBlockReason.Length > 0)
            {
                LastBlockReason = string.Empty;
            }
        }

        ApplyExecutionState();
    }

    /// <summary>
    /// 用"距上次输入的时间"反推屏幕是否被键鼠唤醒了。
    /// </summary>
    private void TrackDisplayState()
    {
        if (_displayOffTick is not uint offTick) return;

        uint now = unchecked((uint)Environment.TickCount);
        var sinceOff = TimeSpan.FromMilliseconds(unchecked(now - offTick));
        var idle = NativeMethods.GetIdleTime();

        // 1.5 秒容差覆盖轮询抖动
        if (idle + TimeSpan.FromSeconds(1.5) >= sinceOff) return;

        _displayOffTick = null;
        ApplyExecutionState();
        StateChanged?.Invoke();
    }

    private void EndDisplayOffSession() => _displayOffTick = null;

    /// <summary>
    /// 从睡眠恢复时由 UI 层调用：记下时刻，随后判断这次唤醒是不是"没人碰过"。
    /// </summary>
    public void NoteResumed()
    {
        NoteUserReturned();

        _resumedTick = _settings.SuppressSpuriousWake && _settings.ResleepAfterSpuriousWake
            ? unchecked((uint)Environment.TickCount)
            : null;
    }

    /// <summary>
    /// 睡眠侧的伪唤醒抑制：电脑被"没人碰过"的事件唤醒时，让它继续睡。
    ///
    /// 判据和屏幕侧完全一样 —— Raw Input 和光标位移都认不出真实输入，
    /// 就说明这次唤醒不是人干的（典型来源还是无线鼠标的节能模式切换）。
    ///
    /// 恢复后先留 6 秒反应时间：真醒了的人会动鼠标或按键，那时立刻放弃。
    /// </summary>
    private void TrackSpuriousResume()
    {
        if (_resumedTick is not uint resumed) return;

        bool genuine = _rawInput is not null &&
                       unchecked((int)(_rawInput.LastGenuineInputTick - resumed)) > 0;

        if (genuine)
        {
            _resumedTick = null;
            _resleepCount = 0;   // 真人回来了，计数归零
            return;
        }

        uint now = unchecked((uint)Environment.TickCount);
        if (TimeSpan.FromMilliseconds(now - resumed) < ResleepGrace) return;

        _resumedTick = null;

        if (_resleepCount >= MaxResleep) return;
        _resleepCount++;

        LastBlockReason = "已把被伪唤醒的电脑送回睡眠";
        PerformSystemSleep();
        StateChanged?.Invoke();
    }

    private bool _inferredDisplayOff;

    /// <summary>
    /// 屏幕当前是不是关着的。
    ///
    /// 优先用系统通知（精确，事实）。但实测在某些环境下
    /// RegisterPowerSettingNotification 注册成功却一条通知都收不到
    /// （两条 GUID 都试过，0 条），所以必须有退路。
    /// </summary>
    private bool CurrentDisplayOff()
    {
        if (_rawInput is null) return false;

        // 收到过通知 → 以通知为准
        if (_rawInput.DisplayStateChanges > 0) return _rawInput.IsDisplayOff;

        return InferDisplayOffFromIdle();
    }

    /// <summary>
    /// 退路：靠空闲时间推断屏幕状态。
    ///
    /// 依据是"空闲时间已经超过系统关屏超时" —— 那屏幕正常就该关了。
    /// 空闲一旦归零，说明刚从关闭状态被唤醒。
    ///
    /// 这是推断不是事实，所以取交流 / 电池两个超时里较小的那个（更早触发，
    /// 宁可多管一次也不要漏掉）。接管模式下系统超时被改成了"从不"，此时返回 false，
    /// 那种情况由我们自己的 _displayOffTick 负责。
    /// </summary>
    private bool InferDisplayOffFromIdle()
    {
        TimeSpan? timeout = SystemVideoTimeout();
        if (timeout is not TimeSpan limit) return false;

        TimeSpan idle = NativeMethods.GetIdleTime();

        if (idle >= limit) { _inferredDisplayOff = true; return true; }
        if (idle < TimeSpan.FromSeconds(2)) { _inferredDisplayOff = false; return false; }

        return _inferredDisplayOff;   // 中间地带维持上一次判断，避免抖动
    }

    private TimeSpan? SystemVideoTimeout() => SystemVideoTimeoutFor(_settings);

    /// <summary>
    /// 从配置里保存的系统超时原值推算当前关屏超时。
    /// 接管模式下系统超时被改成了"从不"，此时返回 null。
    /// </summary>
    public static TimeSpan? SystemVideoTimeoutFor(AppSettings settings)
    {
        if (settings.TakeOverSystemTimeout) return null;

        uint ac = settings.SavedAcVideoIdle;
        uint dc = settings.SavedDcVideoIdle;
        if (ac == 0 && dc == 0) return null;

        uint seconds = ac == 0 ? dc : dc == 0 ? ac : Math.Min(ac, dc);
        return TimeSpan.FromSeconds(Math.Max(30u, seconds));
    }

    /// <summary>
    /// 伪唤醒抑制。
    ///
    /// 思路：显示器从"关"变回"开"的那一刻，回头看这段时间里有没有发生过**真实输入**。
    ///   没有 → 判定为伪唤醒（典型来源：无线鼠标每隔几分钟切换一次节能模式，
    ///          送来一条零位移 HID 报告，Windows 就当作用户回来了），把屏幕关回去。
    ///   有   → 是真人回来了，什么都不做。
    ///
    /// 这里必须用 Raw Input 而不是"距上次输入的时间"：后者只知道"有输入"，
    /// 分不出那是用户动手还是设备自己发的报告。
    /// </summary>
    private void TrackSpuriousWake()
    {
        if (_rawInput is null) return;

        // 兜底判据：光标动过就算真实输入（不依赖 Raw Input）
        _rawInput.PollCursorFallback();

        if (!_settings.SuppressSpuriousWake)
        {
            _displayWasOff = CurrentDisplayOff();
            return;
        }

        bool displayOff = CurrentDisplayOff();

        if (displayOff)
        {
            if (!_displayWasOff)
                _displayOffDetectedTick = unchecked((uint)Environment.TickCount);

            // 注意：这里**不能**清零 _resuppressCount。
            // 抑制成功后屏幕会再关一次、又走一遍这条分支；
            // 若在此清零，上限就永远触发不了，会变成和用户无休止地抢屏幕。
            _displayWasOff = true;
            return;
        }

        if (!_displayWasOff) return;
        _displayWasOff = false;

        bool genuine = unchecked((int)(_rawInput.LastGenuineInputTick - _displayOffDetectedTick)) > 0;
        if (genuine)
        {
            _resuppressCount = 0;   // 用户回来了，计数归零
            return;
        }

        if (_resuppressCount >= MaxResuppress) return;
        _resuppressCount++;

        LastBlockReason = "已抑制一次伪唤醒";
        NativeMethods.BroadcastMonitorPower(NativeMethods.MONITOR_OFF);
        StateChanged?.Invoke();
    }

    /// <summary>
    /// 仅在"屏幕已关闭 + 用户开启该选项"时阻止系统睡眠，
    /// 这样下载 / 转码 / 远程连接能继续跑，而屏幕恢复后又把系统睡眠权还给 Windows。
    /// 注意 SetThreadExecutionState 是线程级的，必须在长期存活的 UI 线程上调用。
    /// </summary>
    private void ApplyExecutionState()
    {
        bool want = _settings.PreventSystemSleep && _displayOffTick is not null;
        if (want == _executionStateApplied) return;

        try
        {
            NativeMethods.ApplyExecutionState(want
                ? NativeMethods.ExecutionState.Continuous | NativeMethods.ExecutionState.SystemRequired
                : NativeMethods.ExecutionState.Continuous);
            _executionStateApplied = want;
        }
        catch
        {
            // 忽略
        }
    }
}
