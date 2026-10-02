using System.Runtime.InteropServices;

namespace MonitorSleep.Interop;

/// <summary>
/// 运行环境自检。
///
/// 低完整性级别（Low IL，S-1-16-4096）的进程会被 Windows 拒绝三件事：
/// 注册托盘图标、写 %APPDATA%、改写系统电源方案。
/// 症状看起来毫不相干，根因却是同一个 —— 所以启动时统一查一次，把话说清楚。
/// </summary>
internal static class EnvironmentProbe
{
    // ───────────────────────── 完整性级别 ─────────────────────────

    private const int TokenQuery = 0x0008;
    private const int TokenIntegrityLevel = 25;

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, int desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(
        IntPtr tokenHandle, int tokenInformationClass, IntPtr tokenInformation, int tokenInformationLength, out int returnLength);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    /// <summary>0x1000 = 低，0x2000 = 中，0x3000 = 高；读取失败返回 -1。</summary>
    public static int CurrentIntegrityRid()
    {
        IntPtr token = IntPtr.Zero;
        IntPtr buffer = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), TokenQuery, out token)) return -1;

            // 第一次调用只用来问"需要多大缓冲区"，它按设计就会返回 FALSE，不能当作失败
            GetTokenInformation(token, TokenIntegrityLevel, IntPtr.Zero, 0, out int length);
            if (length <= 0) return -1;

            buffer = Marshal.AllocHGlobal(length);
            if (!GetTokenInformation(token, TokenIntegrityLevel, buffer, length, out _)) return -1;

            IntPtr sid = Marshal.ReadIntPtr(buffer);
            if (sid == IntPtr.Zero) return -1;

            byte subAuthorityCount = Marshal.ReadByte(sid, 1);
            if (subAuthorityCount == 0) return -1;

            return Marshal.ReadInt32(sid, 8 + (subAuthorityCount - 1) * 4);
        }
        catch
        {
            return -1;
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            if (token != IntPtr.Zero) CloseHandle(token);
        }
    }

    public static string DescribeIntegrityLevel()
    {
        int rid = CurrentIntegrityRid();
        if (rid < 0) return "读取失败";

        string name = rid switch
        {
            0x0000 => "Untrusted（不可信）",
            0x1000 => "Low（低）",
            0x2000 => "Medium（中）",
            0x3000 => "High（高）",
            0x4000 => "System（系统）",
            _ => "未知",
        };
        return $"{name}  S-1-16-{rid}";
    }

    // ───────────────────────── 托盘图标能力 ─────────────────────────

    private const int NIM_ADD = 0;
    private const int NIM_DELETE = 2;
    private const int NIF_MESSAGE = 0x0001;
    private const int NIF_ICON = 0x0002;
    private const int NIF_TIP = 0x0004;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public int uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Shell_NotifyIconW(int dwMessage, ref NOTIFYICONDATAW lpData);

    /// <summary>
    /// 真正调一次 Shell_NotifyIcon(NIM_ADD) 并检查返回值。
    /// 必须自己查：.NET 的 NotifyIcon 会把 Shell_NotifyIcon 的失败结果整个吞掉，
    /// 所以"Visible = true 没抛异常"根本不能说明图标注册成功了。
    /// </summary>
    public static bool CanAddTrayIcon(out int errorCode)
    {
        errorCode = 0;
        NativeWindow? window = null;
        try
        {
            window = new NativeWindow();
            window.CreateHandle(new CreateParams
            {
                Caption = "MonitorSleep.TrayProbe",
                Style = 0,
                ExStyle = 0,
                Parent = new IntPtr(-3), // HWND_MESSAGE
                X = 0,
                Y = 0,
                Width = 0,
                Height = 0,
            });

            var nid = new NOTIFYICONDATAW
            {
                cbSize = Marshal.SizeOf<NOTIFYICONDATAW>(),
                hWnd = window.Handle,
                uID = 0x7FFE,
                uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
                uCallbackMessage = 0x8000 + 0x7FE,
                hIcon = System.Drawing.SystemIcons.Application.Handle,
                szTip = "MonitorSleep tray probe",
            };

            bool ok = Shell_NotifyIconW(NIM_ADD, ref nid);
            if (ok)
            {
                Shell_NotifyIconW(NIM_DELETE, ref nid);
            }
            else
            {
                errorCode = Marshal.GetLastWin32Error();
            }
            return ok;
        }
        catch
        {
            return false;
        }
        finally
        {
            try { window?.DestroyHandle(); }
            catch { /* 忽略 */ }
        }
    }
}
