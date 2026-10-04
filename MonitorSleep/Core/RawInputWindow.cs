using System.Runtime.InteropServices;
using MonitorSleep.Interop;

namespace MonitorSleep.Core;

/// <summary>
/// 一个永不显示的后台窗口，专门接收两样东西：
///
///   · WM_INPUT          —— 鼠标 / 键盘的原始输入。
///                          这是本功能的关键：原始输入带有**真实的位移量**，
///                          因此能把"用户真的动了鼠标"和"设备发来一条零位移报告"区分开。
///
///   · WM_POWERBROADCAST —— 显示器开关状态变化（GUID_CONSOLE_DISPLAY_STATE）。
///                          用它才能知道"屏幕什么时候被关掉、又什么时候被点亮"，
///                          哪怕关屏是 Windows 自己做的、跟我们无关。
///
/// 为什么不用 message-only 窗口（HWND_MESSAGE）：电源设置通知和 Raw Input
/// 都需要一个真实的顶层窗口才能可靠送达。
/// </summary>
internal sealed class RawInputWindow : Form
{
    private readonly List<IntPtr> _displayNotifications = new();

    /// <summary>最近一次「真人操作」发生的时刻（Environment.TickCount）。</summary>
    public uint LastGenuineInputTick { get; private set; }

    /// <summary>显示器当前是否处于关闭状态。</summary>
    public bool IsDisplayOff { get; private set; }

    /// <summary>显示状态通知是否注册成功。false 表示伪唤醒抑制不可用。</summary>
    public bool DisplayStateTrackingAvailable { get; private set; }

    /// <summary>无法启用时的原因（给诊断模式看）。</summary>
    public string UnavailableReason { get; private set; } = string.Empty;

    public RawInputWindow()
    {
        ShowInTaskbar = false;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Location = new Point(-32000, -32000);
        Size = new Size(1, 1);

        // 强制创建窗口句柄。不调用 Show()，所以窗口永远不会出现。
        IntPtr handle = Handle;

        LastGenuineInputTick = unchecked((uint)Environment.TickCount);
        IsDisplayOff = false;
        _lastCursor = Cursor.Position;

        if (!NativeMethods.Is64BitProcess)
        {
            UnavailableReason = "当前不是 64 位进程，Raw Input 结构布局不适用";
            return;
        }

        if (!NativeMethods.RegisterRawInput(handle))
        {
            UnavailableReason = $"RegisterRawInputDevices 失败（错误码 {Marshal.GetLastWin32Error()}）";
            return;
        }

        // 两条通知 GUID 都注册 —— 实测其中一条在有些环境下一条都收不到，
        // 哪条先到都算数。
        foreach (Guid guid in new[] { NativeMethods.GuidConsoleDisplayState, NativeMethods.GuidMonitorPowerOn })
        {
            IntPtr token = NativeMethods.RegisterDisplayStateNotification(handle, guid);
            if (token != IntPtr.Zero) _displayNotifications.Add(token);
        }

        if (_displayNotifications.Count == 0)
        {
            UnavailableReason = $"RegisterPowerSettingNotification 失败（错误码 {Marshal.GetLastWin32Error()}）";
            return;
        }

        DisplayStateTrackingAvailable = true;
    }

    /// <summary>诊断计数：窗口一共收到多少条 WM_INPUT。</summary>
    public int RawInputMessages { get; private set; }

    /// <summary>诊断计数：窗口一共收到多少条 WM_POWERBROADCAST。</summary>
    public int PowerBroadcastMessages { get; private set; }

    /// <summary>诊断计数：其中属于"设置变化"（PBT_POWERSETTINGCHANGE）的有多少条。</summary>
    public int PowerSettingChangeMessages { get; private set; }

    /// <summary>诊断用：最近一条设置变化消息的原始内容。</summary>
    public string LastPowerSettingDetail { get; private set; } = "（一条都没收到）";

    /// <summary>诊断计数：其中属于"显示器开关状态"的有多少条。</summary>
    public int DisplayStateChanges { get; private set; }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == NativeMethods.WM_INPUT)
        {
            RawInputMessages++;
            var raw = NativeMethods.ReadRawInput(m.LParam);
            if (raw is { } input && NativeMethods.IsGenuineInput(in input))
                LastGenuineInputTick = unchecked((uint)Environment.TickCount);
        }
        else if (m.Msg == NativeMethods.WM_POWERBROADCAST)
        {
            PowerBroadcastMessages++;
            if (m.WParam.ToInt32() == NativeMethods.PBT_POWERSETTINGCHANGE)
            {
                PowerSettingChangeMessages++;
                ReadDisplayState(m.LParam);
            }
        }

        base.WndProc(ref m);
    }

    /// <summary>
    /// 解析 POWERBROADCAST_SETTING：
    ///   GUID PowerSetting (16 字节) + DWORD DataLength (4 字节) + Data (1 字节起)
    /// 所以 Data 的第一个字节在偏移 20。0 = 关，1 = 开，2 = 变暗。
    /// </summary>
    private void ReadDisplayState(IntPtr lParam)
    {
        try
        {
            var guidBytes = new byte[16];
            Marshal.Copy(lParam, guidBytes, 0, 16);
            Guid received = new(guidBytes);
            uint dataLength = (uint)Marshal.ReadInt32(lParam, 16);
            byte value = Marshal.ReadByte(lParam, 20);

            // 把原始内容记下来：只有看到真实收到的 GUID，才能判断
            // 是"通知压根没来"还是"来了但我们的比较写错了"
            LastPowerSettingDetail =
                $"最近一条设置变化：GUID={received} 长度={dataLength} 值={value}";

            if (received != NativeMethods.GuidConsoleDisplayState &&
                received != NativeMethods.GuidMonitorPowerOn) return;

            IsDisplayOff = value == 0;   // 变暗(2) 视作仍然点亮
            DisplayStateChanges++;
        }
        catch (Exception ex)
        {
            LastPowerSettingDetail = "解析设置变化失败：" + ex.Message;
        }
    }

    /// <summary>给界面和诊断模式看的可读状态。</summary>
    public string StatusText => DisplayStateTrackingAvailable
        ? "可用"
        : $"不可用：{UnavailableReason}";

    private Point _lastCursor;

    /// <summary>
    /// 兜底判据：直接看光标有没有动过。
    ///
    /// Raw Input 在受限环境里会被 UIPI 拦掉（实测一条都收不到），
    /// 但"光标位置变了没有"这个判断谁都能做，而且正好覆盖本功能要解决的场景：
    /// 无线鼠标切换节能模式时发出的是**零位移**报告，光标一步都不会动。
    ///
    /// 由控制器每个 tick 调用一次。
    /// </summary>
    public void PollCursorFallback()
    {
        Point now = Cursor.Position;
        if (now == _lastCursor) return;

        _lastCursor = now;
        LastGenuineInputTick = unchecked((uint)Environment.TickCount);
    }

    /// <summary>
    /// 实测两条前提是否真的成立 —— 注册成功不等于消息送得到。
    ///
    ///   1) 合成一次 1 像素鼠标移动，看 WM_INPUT 有没有送达（光标只动 1 像素，基本无感）
    ///   2) 把屏幕关掉再打开，看显示状态通知有没有送达（屏幕会黑约 2 秒）
    /// </summary>
    public IReadOnlyList<string> SelfTest(TimeSpan? idleFallback)
    {
        var lines = new List<string>();

        // ── 1) 光标位移判据（兜底，不依赖 Raw Input） ──
        Point origin = Cursor.Position;
        uint before = LastGenuineInputTick;
        Cursor.Position = new Point(origin.X + 2, origin.Y);
        System.Threading.Thread.Sleep(80);
        PollCursorFallback();
        bool cursorOk = LastGenuineInputTick != before;
        Cursor.Position = origin;          // 把光标挪回原处
        System.Threading.Thread.Sleep(80);
        PollCursorFallback();

        lines.Add(cursorOk
            ? "  光标位移判据    : ✅ 可用（能识别真实鼠标移动）"
            : "  光标位移判据    : ❌ 不可用 —— 抑制不会生效");

        // ── 2) Raw Input（增强项：能额外识别键盘） ──
        NativeMethods.JiggleMouse();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 1200 && RawInputMessages == 0)
        {
            Application.DoEvents();
            System.Threading.Thread.Sleep(20);
        }

        lines.Add(RawInputMessages > 0
            ? $"  Raw Input 增强  : ✅ 收到 {RawInputMessages} 条（键盘也能识别）"
            : "  Raw Input 增强  : ⚠ 收不到（受限环境下会被 UIPI 拦掉，但上面那条兜底仍有效）");

        // ── 2) 显示状态通知是否真的送达 ──
        NativeMethods.BroadcastMonitorPower(NativeMethods.MONITOR_OFF);
        sw.Restart();
        bool sawOff = false;
        while (sw.ElapsedMilliseconds < 3000)
        {
            Application.DoEvents();
            System.Threading.Thread.Sleep(50);
            if (IsDisplayOff) { sawOff = true; break; }
        }
        NativeMethods.BroadcastMonitorPower(NativeMethods.MONITOR_ON);

        lines.Add(sawOff
            ? $"  显示状态通知    : ✅ 检测到屏幕被关闭（共 {DisplayStateChanges} 次状态变化）"
            : $"  显示状态通知    : ❌ 没检测到（WM_POWERBROADCAST {PowerBroadcastMessages} 条，"
              + $"其中设置变化 {PowerSettingChangeMessages} 条，匹配显示器状态 {DisplayStateChanges} 条）");
        lines.Add($"  期望的 GUID     : {NativeMethods.GuidConsoleDisplayState} 或 {NativeMethods.GuidMonitorPowerOn}");
        lines.Add($"  {LastPowerSettingDetail}");

        // 通知收不到时靠这条退路，所以它才是"能不能用"的真正依据
        lines.Add(idleFallback is TimeSpan fb
            ? $"  空闲推断退路    : ✅ 系统关屏超时 {fb.TotalSeconds:0} 秒 —— 通知收不到也能工作"
            : "  空闲推断退路    : ❌ 不可用");

        IsDisplayOff = false;   // 自检会打乱状态，复位
        return lines;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (IntPtr token in _displayNotifications)
                NativeMethods.UnregisterDisplayStateNotification(token);
            _displayNotifications.Clear();
        }
        base.Dispose(disposing);
    }
}
