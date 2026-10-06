using System.Runtime.InteropServices;

namespace MonitorSleep.Interop;

/// <summary>
/// 所有 Win32 原生调用的集中定义处。
/// </summary>
internal static class NativeMethods
{
    // ───────────────────────── 关屏：WM_SYSCOMMAND / SC_MONITORPOWER ─────────────────────────

    public static readonly IntPtr HWND_BROADCAST = new(0xFFFF);
    public const uint WM_SYSCOMMAND = 0x0112;
    public const int SC_MONITORPOWER = 0xF170;
    public const uint SMTO_ABORTIFHUNG = 0x0002;

    public const int MONITOR_ON = -1;
    public const int MONITOR_LOWPOWER = 1;
    public const int MONITOR_OFF = 2;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

    /// <summary>向所有顶层窗口广播显示器电源指令。-1 打开 / 1 低功耗 / 2 关闭。</summary>
    public static bool BroadcastMonitorPower(int state)
    {
        try
        {
            var r = SendMessageTimeout(
                HWND_BROADCAST, WM_SYSCOMMAND, (IntPtr)SC_MONITORPOWER, (IntPtr)state,
                SMTO_ABORTIFHUNG, 2000, out _);
            // 广播时返回值是"处理了该消息的窗口数"，0 表示没有任何窗口响应。
            return r != IntPtr.Zero;
        }
        catch
        {
            return false;
        }
    }

    // ───────────────────────── 空闲时长 ─────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    public struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    /// <summary>系统范围内距上次键鼠输入经过的时长。</summary>
    public static TimeSpan GetIdleTime()
    {
        var lii = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref lii))
            return TimeSpan.Zero;

        // dwTime 与 Environment.TickCount 都是 32 位且约 49.7 天回绕一次。
        uint now = unchecked((uint)Environment.TickCount);
        uint elapsed = unchecked(now - lii.dwTime);
        return TimeSpan.FromMilliseconds(elapsed);
    }

    // ───────────────────────── 全屏 / 演示模式检测 ─────────────────────────

    /// <summary>QUERY_USER_NOTIFICATION_STATE</summary>
    public enum UserNotificationState
    {
        NotPresent = 1,
        Busy = 2,
        RunningD3dFullScreen = 3,
        PresentationMode = 4,
        AcceptsNotifications = 5,
        QuietTime = 6,
        App = 7,
    }

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int pquns);

    /// <summary>取当前 shell 状态；失败返回 AcceptsNotifications（即"可以打扰"）。</summary>
    public static UserNotificationState GetNotificationState()
    {
        try
        {
            if (SHQueryUserNotificationState(out int state) == 0)
                return (UserNotificationState)state;
        }
        catch
        {
            // 忽略：降级为"可以打扰"
        }
        return UserNotificationState.AcceptsNotifications;
    }

    // ───────────────────────── 前台窗口是否真的占满整块屏幕 ─────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetClassNameW")]
    private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    /// <summary>
    /// 判断前台窗口是否真的盖住了整块显示器。
    /// SHQueryUserNotificationState 在某些环境下会误报 QUNS_BUSY，
    /// 单靠它会导致"永远不关屏"，所以再核对一次窗口矩形。
    /// </summary>
    public static bool IsForegroundFullScreen(out string description)
    {
        description = "未知";
        try
        {
            IntPtr hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero)
            {
                description = "无前台窗口";
                return false;
            }

            var sb = new System.Text.StringBuilder(256);
            GetClassName(hwnd, sb, sb.Capacity);
            string cls = sb.ToString();

            // 桌面外壳与任务栏本来就铺满屏幕，不算"全屏程序"
            if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Button" or "SysListView32")
            {
                description = $"桌面外壳（{cls}）";
                return false;
            }

            if (!GetWindowRect(hwnd, out RECT wr))
            {
                description = $"{cls}：读取窗口矩形失败";
                return false;
            }

            IntPtr monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(monitor, ref mi))
            {
                description = $"{cls}：读取显示器信息失败";
                return false;
            }

            bool full = wr.Left <= mi.rcMonitor.Left && wr.Top <= mi.rcMonitor.Top
                     && wr.Right >= mi.rcMonitor.Right && wr.Bottom >= mi.rcMonitor.Bottom;

            description = $"{cls} 窗口({wr.Left},{wr.Top})-({wr.Right},{wr.Bottom}) "
                        + $"屏({mi.rcMonitor.Left},{mi.rcMonitor.Top})-({mi.rcMonitor.Right},{mi.rcMonitor.Bottom})"
                        + $" → {(full ? "占满" : "未占满")}";
            return full;
        }
        catch (Exception ex)
        {
            description = "检查失败：" + ex.Message;
            return false;
        }
    }

    // ───────────────────────── 执行状态（阻止系统睡眠 / 关屏） ─────────────────────────

    [Flags]
    public enum ExecutionState : uint
    {
        Continuous = 0x80000000,
        SystemRequired = 0x00000001,
        DisplayRequired = 0x00000002,
        AwayModeRequired = 0x00000040,
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SetThreadExecutionState(uint esFlags);

    /// <summary>
    /// 注意：该状态是"线程级"的，必须由长期存活的线程调用（本程序固定用 UI 线程）。
    /// </summary>
    public static void ApplyExecutionState(ExecutionState state) => SetThreadExecutionState((uint)state);

    // ───────────────────────── 电源方案：关机屏超时（VIDEOIDLE） ─────────────────────────

    public static readonly Guid GUID_VIDEO_SUBGROUP = new("7516b95f-f776-4464-8c53-06167f40cc99");
    public static readonly Guid GUID_VIDEO_POWERDOWN_TIMEOUT = new("3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e");
    public const uint ERROR_SUCCESS = 0;

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern uint PowerReadACValueIndex(
        IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subGroupOfPowerSettingsGuid, ref Guid powerSettingGuid, out uint valueIndex);

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern uint PowerReadDCValueIndex(
        IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subGroupOfPowerSettingsGuid, ref Guid powerSettingGuid, out uint valueIndex);

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern uint PowerWriteACValueIndex(
        IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subGroupOfPowerSettingsGuid, ref Guid powerSettingGuid, uint valueIndex);

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern uint PowerWriteDCValueIndex(
        IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subGroupOfPowerSettingsGuid, ref Guid powerSettingGuid, uint valueIndex);

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern uint PowerSetActiveScheme(IntPtr userRootPowerKey, ref Guid schemeGuid);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    public static Guid? GetActiveScheme()
    {
        if (PowerGetActiveScheme(IntPtr.Zero, out IntPtr pGuid) != ERROR_SUCCESS || pGuid == IntPtr.Zero)
            return null;
        try
        {
            return Marshal.PtrToStructure<Guid>(pGuid);
        }
        finally
        {
            LocalFree(pGuid);
        }
    }

    /// <summary>读取当前方案里"在此时间后关闭显示"的秒数（0 表示从不）。</summary>
    public static bool TryReadVideoIdle(out uint acSeconds, out uint dcSeconds)
    {
        acSeconds = dcSeconds = 0;
        Guid? scheme = GetActiveScheme();
        if (scheme is null) return false;

        Guid s = scheme.Value, sub = GUID_VIDEO_SUBGROUP, set = GUID_VIDEO_POWERDOWN_TIMEOUT;
        uint ac, dc;
        bool okAc = PowerReadACValueIndex(IntPtr.Zero, ref s, ref sub, ref set, out ac) == ERROR_SUCCESS;
        bool okDc = PowerReadDCValueIndex(IntPtr.Zero, ref s, ref sub, ref set, out dc) == ERROR_SUCCESS;
        acSeconds = ac;
        dcSeconds = dc;
        return okAc || okDc;
    }

    /// <summary>把"在此时间后关闭显示"改为指定秒数（0 = 从不）并立即生效。返回旧的交流/直流值。</summary>
    public static bool TryWriteVideoIdle(uint acSeconds, uint dcSeconds, out uint oldAc, out uint oldDc)
    {
        oldAc = oldDc = 0;
        Guid? scheme = GetActiveScheme();
        if (scheme is null) return false;

        Guid s = scheme.Value, sub = GUID_VIDEO_SUBGROUP, set = GUID_VIDEO_POWERDOWN_TIMEOUT;
        if (!TryReadVideoIdle(out oldAc, out oldDc)) return false;

        bool ok = PowerWriteACValueIndex(IntPtr.Zero, ref s, ref sub, ref set, acSeconds) == ERROR_SUCCESS
               && PowerWriteDCValueIndex(IntPtr.Zero, ref s, ref sub, ref set, dcSeconds) == ERROR_SUCCESS
               && PowerSetActiveScheme(IntPtr.Zero, ref s) == ERROR_SUCCESS;
        return ok;
    }

    // ───────────────────────── 全局热键 ─────────────────────────

    public const int WM_HOTKEY = 0x0312;

    [Flags]
    public enum HotKeyModifiers : uint
    {
        None = 0x0000,
        Alt = 0x0001,
        Control = 0x0002,
        Shift = 0x0004,
        Win = 0x0008,
        NoRepeat = 0x4000,
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    // ───────────────────────── 会话 / 电源状态 ─────────────────────────

    public const int SM_REMOTESESSION = 0x1000;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    /// <summary>当前进程是否运行在远程桌面会话中。</summary>
    public static bool IsRemoteSession()
    {
        try { return GetSystemMetrics(SM_REMOTESESSION) != 0; }
        catch { return false; }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;      // 0 = 电池, 1 = 交流, 255 = 未知
        public byte BatteryFlag;
        public byte BatteryLifePercent; // 255 = 未知
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    public static bool TryGetPowerStatus(out SYSTEM_POWER_STATUS status) => GetSystemPowerStatus(out status);

    // ───────────────────────── 唤醒用的合成输入 ─────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct INPUTUNION
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT
    {
        public uint type;
        public INPUTUNION u;
    }

    public const uint INPUT_MOUSE = 0;
    public const uint MOUSEEVENTF_MOVE = 0x0001;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, [In] INPUT[] pInputs, int cbSize);

    /// <summary>
    /// 鼠标相对移动 1 像素后立刻移回，光标净位移为 0。
    /// 用于把显示器从待机状态"顶"回来 —— 部分驱动不响应 SC_MONITORPOWER(-1)。
    /// </summary>
    public static bool JiggleMouse()
    {
        int size = Marshal.SizeOf<INPUT>();
        if (size != 40 && IntPtr.Size == 8)
            return false; // 结构布局异常时宁可不做，也不要乱动光标

        var inputs = new INPUT[2];
        inputs[0].type = INPUT_MOUSE;
        inputs[0].u.mi = new MOUSEINPUT { dx = 1, dy = 0, dwFlags = MOUSEEVENTF_MOVE };
        inputs[1].type = INPUT_MOUSE;
        inputs[1].u.mi = new MOUSEINPUT { dx = -1, dy = 0, dwFlags = MOUSEEVENTF_MOVE };

        return SendInput((uint)inputs.Length, inputs, size) == inputs.Length;
    }

    /// <summary>诊断用：核对 INPUT 结构布局是否符合 Win32 预期（x64 下应为 40 字节）。</summary>
    public static string INPUTSizeDescription()
    {
        int size = Marshal.SizeOf<INPUT>();
        int mouseSize = Marshal.SizeOf<MOUSEINPUT>();
        bool ok = IntPtr.Size != 8 || size == 40;
        return $"INPUT={size} 字节, MOUSEINPUT={mouseSize} 字节, 指针={IntPtr.Size * 8} 位 → {(ok ? "符合预期" : "布局异常，鼠标抖动将被禁用")}";
    }

    // ───────────────────────── 系统睡眠 ─────────────────────────

    private const int TOKEN_ADJUST_PRIVILEGES = 0x0020;
    private const int TOKEN_QUERY = 0x0008;
    private const int SE_PRIVILEGE_ENABLED = 0x0002;
    private const string SE_SHUTDOWN_NAME = "SeShutdownPrivilege";

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID_AND_ATTRIBUTES
    {
        public long Luid;
        public int Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_PRIVILEGES
    {
        public int PrivilegeCount;
        public LUID_AND_ATTRIBUTES Privilege;
    }

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, int desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LookupPrivilegeValue(string? systemName, string name, out long luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(
        IntPtr tokenHandle, bool disableAllPrivileges, ref TOKEN_PRIVILEGES newState,
        int bufferLength, IntPtr previousState, IntPtr returnLength);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    /// <summary>
    /// 让电脑进入睡眠。
    ///
    /// 睡眠需要 SeShutdownPrivilege —— 普通用户令牌里通常持有这个权限，但默认是禁用状态，
    /// 所以先显式打开再调用。失败时退回系统自带的 rundll32 入口再试一次。
    /// </summary>
    public static bool TrySystemSleep(out string error)
    {
        error = string.Empty;
        try
        {
            EnableShutdownPrivilege();

            if (SetSuspendState(false, false, false))
                return true;

            int code = Marshal.GetLastWin32Error();

            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                    "rundll32.exe", "powrprof.dll,SetSuspendState 0,1,0")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                return true;
            }
            catch
            {
                error = $"SetSuspendState 失败（错误码 {code}），备用方式也未能启动。";
                return false;
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static void EnableShutdownPrivilege()
    {
        IntPtr token = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out token)) return;
            if (!LookupPrivilegeValue(null, SE_SHUTDOWN_NAME, out long luid)) return;

            var privileges = new TOKEN_PRIVILEGES
            {
                PrivilegeCount = 1,
                Privilege = new LUID_AND_ATTRIBUTES { Luid = luid, Attributes = SE_PRIVILEGE_ENABLED },
            };
            AdjustTokenPrivileges(token, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero);
        }
        catch
        {
            // 打不开也无所谓，SetSuspendState 自己会给出结果
        }
        finally
        {
            if (token != IntPtr.Zero) CloseHandle(token);
        }
    }

    // ───────────────────── Raw Input：区分"真人操作"与"伪唤醒" ─────────────────────

    public const int WM_INPUT = 0x00FF;
    public const int WM_POWERBROADCAST = 0x0218;
    public const int PBT_POWERSETTINGCHANGE = 0x8015;
    public const uint DEVICE_NOTIFY_WINDOW_HANDLE = 0;

    /// <summary>显示器开关状态变化时，系统会按这个 GUID 通知我们。</summary>
    public static readonly Guid GuidConsoleDisplayState = new("6fe69556-704a-47a0-8f24-c28d936fda47");

    /// <summary>
    /// 较老但更通用的「显示器开 / 关」通知 GUID。
    ///
    /// 实测 GUID_CONSOLE_DISPLAY_STATE 在某些环境下一条通知都收不到，
    /// 所以两条都注册上 —— 哪条先到都算数。
    /// </summary>
    public static readonly Guid GuidMonitorPowerOn = new("02731015-4510-4526-99e6-e5a17ebd1aea");

    private const uint RIDEV_INPUTSINK = 0x00000100;
    private const uint RID_INPUT = 0x10000003;
    private const uint RIM_TYPEMOUSE = 0;
    private const uint RIM_TYPEKEYBOARD = 1;
    private const ushort MOUSE_MOVE_ABSOLUTE = 0x01;

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE
    {
        public ushort UsagePage;
        public ushort Usage;
        public uint Flags;
        public IntPtr Target;
    }

    /// <summary>
    /// 一条 Raw Input。
    ///
    /// 字段偏移是手写固定的，因为这是 x64 下的确切布局：
    /// 头部 = dwType(4) + dwSize(4) + hDevice(8) + wParam(8) = 24 字节，
    /// 鼠标 / 键盘的联合体从偏移 24 开始。
    /// （x86 下头部只有 16 字节，所以本类型只在 64 位进程里使用。）
    /// </summary>
    [StructLayout(LayoutKind.Explicit)]
    public struct RAWINPUT
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(4)] public uint Size;
        [FieldOffset(8)] public IntPtr Device;
        [FieldOffset(16)] public IntPtr WParam;

        // ── 鼠标（联合体起始于 24） ──
        [FieldOffset(24)] public ushort MouseFlags;
        [FieldOffset(28)] public ushort MouseButtonFlags;
        [FieldOffset(36)] public int MouseLastX;
        [FieldOffset(40)] public int MouseLastY;

        // ── 键盘 ──
        [FieldOffset(30)] public ushort KeyboardVKey;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(
        [In] RAWINPUTDEVICE[] devices, uint numDevices, uint size);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputData(
        IntPtr rawInput, uint command, IntPtr data, ref uint size, uint headerSize);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr RegisterPowerSettingNotification(
        IntPtr recipient, ref Guid powerSettingGuid, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterPowerSettingNotification(IntPtr handle);

    /// <summary>本进程是不是 64 位 —— RAWINPUT 的字段偏移只对 64 位成立。</summary>
    public static bool Is64BitProcess => IntPtr.Size == 8;

    /// <summary>让指定窗口即使在后台也能收到鼠标 / 键盘的原始输入。</summary>
    public static bool RegisterRawInput(IntPtr hwnd)
    {
        if (!Is64BitProcess) return false;

        var devices = new[]
        {
            new RAWINPUTDEVICE { UsagePage = 0x01, Usage = 0x02, Flags = RIDEV_INPUTSINK, Target = hwnd },  // 鼠标
            new RAWINPUTDEVICE { UsagePage = 0x01, Usage = 0x06, Flags = RIDEV_INPUTSINK, Target = hwnd },  // 键盘
        };
        return RegisterRawInputDevices(devices, (uint)devices.Length, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
    }

    public static IntPtr RegisterDisplayStateNotification(IntPtr hwnd, Guid powerSetting)
    {
        Guid guid = powerSetting;   // 按 ref 传需要可写变量
        return RegisterPowerSettingNotification(hwnd, ref guid, DEVICE_NOTIFY_WINDOW_HANDLE);
    }

    public static void UnregisterDisplayStateNotification(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return;
        try { UnregisterPowerSettingNotification(handle); } catch { /* 忽略 */ }
    }

    /// <summary>读出 lParam 指向的那条 Raw Input；失败返回 null。</summary>
    public static RAWINPUT? ReadRawInput(IntPtr lParam)
    {
        const uint headerSize = 24;   // x64 的 RAWINPUTHEADER 大小
        uint size = 0;

        // 先问需要多大缓冲区
        if (GetRawInputData(lParam, RID_INPUT, IntPtr.Zero, ref size, headerSize) != 0) return null;
        if (size == 0 || size > 4096) return null;

        IntPtr buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetRawInputData(lParam, RID_INPUT, buffer, ref size, headerSize) != size) return null;
            return Marshal.PtrToStructure<RAWINPUT>(buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// 这条 Raw Input 算不算"真人操作"。
    ///
    /// 关键在鼠标：无线鼠标切换节能模式时会送来一条**零位移**报告，
    /// Windows 因此把显示器点亮，但光标其实一步没动 —— 这种必须判为伪唤醒。
    /// </summary>
    public static bool IsGenuineInput(in RAWINPUT input)
    {
        if (input.Type == RIM_TYPEMOUSE)
        {
            // 绝对坐标设备（数位板、远程桌面）的位移字段其实是坐标，不能按增量判断
            if ((input.MouseFlags & MOUSE_MOVE_ABSOLUTE) != 0) return true;

            // 按键、滚轮一律算真实操作
            if (input.MouseButtonFlags != 0) return true;

            return input.MouseLastX != 0 || input.MouseLastY != 0;
        }

        if (input.Type == RIM_TYPEKEYBOARD)
        {
            // 0xFF 是系统伪键码（用来传递特殊状态），不算真实按键
            return input.KeyboardVKey != 0xFF;
        }

        return false;
    }

    private const uint RIDI_DEVICENAME = 0x20000007;

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetRawInputDeviceInfo(IntPtr device, uint command, IntPtr data, ref uint size);

    /// <summary>
    /// 把原始输入的设备句柄解析成设备接口路径，
    /// 例如 \\?\HID#VID_3554&amp;PID_FA09#...#{884b96c3-...}。
    ///
    /// 这是 Windows 自己不会告诉你的信息 —— 系统把唤醒归因到 USB 主控器，
    /// 不说是挂在它下面的哪个设备；而原始输入里带着确切句柄。
    ///
    /// 注意：RIDI_DEVICENAME 的 size 单位是**字符数**而不是字节，和其它命令不一样。
    /// </summary>
    public static string? DescribeRawInputDevice(IntPtr device)
    {
        if (device == IntPtr.Zero) return null;

        try
        {
            uint size = 0;
            if (GetRawInputDeviceInfo(device, RIDI_DEVICENAME, IntPtr.Zero, ref size) != 0) return null;
            if (size == 0 || size > 1024) return null;

            IntPtr buffer = Marshal.AllocHGlobal((int)size * sizeof(char));
            try
            {
                if (GetRawInputDeviceInfo(device, RIDI_DEVICENAME, buffer, ref size) == 0) return null;
                return Marshal.PtrToStringUni(buffer);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 把设备路径压成人一眼能看懂的形式：
    /// \\?\HID#VID_3554&amp;PID_FA09#7&amp;1234&amp;0&amp;0000#{...} → VID_3554&amp;PID_FA09
    /// 认不出 VID/PID 时原样返回。
    /// </summary>
    public static string DescribeDeviceBriefly(string? devicePath)
    {
        if (string.IsNullOrEmpty(devicePath)) return "未知设备";

        int start = devicePath.IndexOf("VID_", StringComparison.OrdinalIgnoreCase);
        if (start < 0) return devicePath;

        int end = devicePath.IndexOf('#', start);
        return end > start ? devicePath.Substring(start, end - start) : devicePath.Substring(start);
    }
}
