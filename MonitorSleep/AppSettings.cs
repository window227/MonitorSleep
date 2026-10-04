using System.Text.Json.Serialization;

namespace MonitorSleep;

/// <summary>一条全局热键绑定：Ctrl/Alt/Shift/Win 的任意组合 + 一个主键。</summary>
public sealed class HotKeyBinding
{
    public bool Ctrl { get; set; }
    public bool Alt { get; set; }
    public bool Shift { get; set; }
    public bool Win { get; set; }

    /// <summary>System.Windows.Forms.Keys 的数值；0 表示未绑定。</summary>
    public int Key { get; set; }

    [JsonIgnore]
    public bool IsValid => Key != 0 && (Ctrl || Alt || Shift || Win);

    public static HotKeyBinding Create(bool ctrl, bool alt, bool shift, Keys key) =>
        new() { Ctrl = ctrl, Alt = alt, Shift = shift, Key = (int)key };

    public override string ToString()
    {
        if (Key == 0) return "（未绑定）";
        var parts = new List<string>(4);
        if (Ctrl) parts.Add("Ctrl");
        if (Alt) parts.Add("Alt");
        if (Shift) parts.Add("Shift");
        if (Win) parts.Add("Win");
        parts.Add(((Keys)Key).ToString());
        return string.Join(" + ", parts);
    }
}

/// <summary>仅使用电池供电时是否允许自动关屏。</summary>
public enum PowerPolicy
{
    /// <summary>交流/电池都自动关屏。</summary>
    Always = 0,
    /// <summary>只在电池供电时自动关屏。</summary>
    BatteryOnly = 1,
    /// <summary>只在插电时自动关屏。</summary>
    AcOnly = 2,
}

public sealed class AppSettings
{
    /// <summary>
    /// 配置结构版本，用来把旧版本留下的、有风险的默认值迁移掉。
    /// 默认 1 表示"v1 语义"；SettingsStore 读到小于当前版本时会做一次纠正并写回。
    /// </summary>
    public int SettingsVersion { get; set; } = 1;

    // ── 热键 ──
    public bool HotKeysEnabled { get; set; } = true;

    /// <summary>
    /// 关屏热键 —— 只保留这一个。
    /// 唤醒本来靠任意键鼠输入就能完成，"切换"也没有必要，少两个热键就少两处冲突。
    /// </summary>
    public HotKeyBinding SleepHotKey { get; set; } = HotKeyBinding.Create(true, true, false, Keys.O);

    /// <summary>电脑睡眠热键。和关屏热键配成一对：O = 关屏，S = 睡眠。</summary>
    public HotKeyBinding SystemSleepHotKey { get; set; } = HotKeyBinding.Create(true, true, false, Keys.S);

    // ── 空闲自动关屏 ──
    public bool IdleAutoOffEnabled { get; set; } = true;
    public int IdleMinutes { get; set; } = 10;
    /// <summary>解锁 / 从睡眠恢复后的静默期，此期间不自动关屏。</summary>
    public int GraceAfterUnlockSeconds { get; set; } = 60;
    public PowerPolicy PowerPolicy { get; set; } = PowerPolicy.Always;

    // ── 接管系统关屏 ──
    /// <summary>
    /// 把系统"在此时间后关闭显示"设为"从不"，改由本程序判断。
    ///
    /// 默认关闭。这是个"失效危险"的选项：系统超时一旦变成"从不"，
    /// 本程序被强杀、被删除、或判断出错时，屏幕就再也不会自动关闭 —— 比原设置更费电。
    /// Windows 本身并不笨（浏览器放视频、播放器、PPT 放映都会主动抑制关屏），
    /// 所以接管多数情况下是多余的，只在特定程序需要屏幕长亮时才值得打开。
    /// </summary>
    public bool TakeOverSystemTimeout { get; set; }

    /// <summary>
    /// 关屏期间阻止系统进入睡眠（下载 / 转码 / 远程连接场景）。
    /// 默认关闭：不改变 Windows 原有的睡眠行为，需要长任务时再手动打开。
    /// </summary>
    public bool PreventSystemSleep { get; set; }

    // ── 智能避让 ──
    public bool BlockOnFullScreen { get; set; } = true;

    /// <summary>
    /// 正在播放音频时保持常亮。
    ///
    /// 默认关闭：本程序只能用音量峰值判断"有没有声音"，分不清视频和纯音乐，
    /// 于是听歌时也会一直不关屏。而 Windows 分得清 —— 它只给视频发显示唤醒锁，
    /// 听音乐时照样关屏。开着这条规则反而比系统行为更费电。
    /// </summary>
    public bool BlockOnAudio { get; set; }

    public bool BlockOnCapture { get; set; } = true;
    public bool BlockOnRemoteSession { get; set; } = true;
    public List<string> BlockProcesses { get; set; } = new();

    // ── 关屏行为 ──
    public bool ShowCountdown { get; set; } = true;
    public int CountdownSeconds { get; set; } = 5;
    /// <summary>唤醒时轻微抖动鼠标 1 像素（部分驱动不响应 SC_MONITORPOWER(-1)）。</summary>
    public bool JiggleOnWake { get; set; } = true;
    /// <summary>关屏失败时改用全屏黑窗遮盖兜底。</summary>
    public bool OverlayFallback { get; set; } = true;

    /// <summary>
    /// 伪唤醒抑制：屏幕被"没有真实输入"的事件点亮时，自动把它关回去。
    ///
    /// 典型场景：无线鼠标每隔几分钟切换一次节能模式，产生一条零位移报告，
    /// Windows 把它当成"用户回来了"，于是把刚关掉的屏幕又点亮。
    ///
    /// 默认关闭 —— 它会主动关屏，属于比较强势的行为，需要用户明确同意。
    /// </summary>
    public bool SuppressSpuriousWake { get; set; }

    // ── 系统集成 ──
    public bool AutoStart { get; set; }
    /// <summary>是否已经给用户看过"托盘图标可能被折叠"的首次运行提示。</summary>
    public bool TrayGuidanceShown { get; set; }

    // ── 内部状态：电源方案接管的恢复凭据 ──
    /// <summary>true 表示系统关屏设置已被本程序改写、尚未还原。启动时据此自恢复。</summary>
    public bool PowerSchemeRestorePending { get; set; }
    public uint SavedAcVideoIdle { get; set; }
    public uint SavedDcVideoIdle { get; set; }
}
