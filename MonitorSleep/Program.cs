using System.Runtime.InteropServices;
using System.Text;
using MonitorSleep.Core;
using MonitorSleep.Interop;
using MonitorSleep.UI;

namespace MonitorSleep;

internal static class Program
{
    private const string MutexName = @"Local\MonitorSleep.SingleInstance";
    private const string ShowSettingsEventName = @"Local\MonitorSleep.ShowSettings";

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int dwProcessId);

    private const int ATTACH_PARENT_PROCESS = -1;

    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] is "--diagnose" or "-d" or "/diagnose")
        {
            Environment.ExitCode = RunDiagnostics(args);
            return;
        }

        RunTray();
    }

    // ───────────────────────── 正常托盘模式 ─────────────────────────

    private static void RunTray()
    {
        using var mutex = new Mutex(initiallyOwned: true, MutexName, out bool isFirstInstance);
        if (!isFirstInstance)
        {
            // 已经有实例在跑：请它把设置窗口弹出来，然后自己退出。
            //
            // 但这里必须给用户一句明确的话 —— 否则会掉进一个很难自查的坑：
            // 如果那个正在运行的实例本身跑在受限环境里（托盘图标注册不了），
            // 用户之后每一次启动都会"立刻退出"，看起来就像新版本毫无效果，
            // 实际上他启动的一直是同一个旧进程。
            bool signaled = false;
            try
            {
                if (EventWaitHandle.TryOpenExisting(ShowSettingsEventName, out var ev))
                {
                    ev.Set();
                    ev.Dispose();
                    signaled = true;
                }
            }
            catch
            {
                // 忽略
            }

            try
            {
                MessageBox.Show(
                    "显示器睡眠助手已经在运行。" +
                    (signaled ? "\n\n已让正在运行的那个实例打开设置窗口。" : "") +
                    "\n\n如果你在通知区域（托盘）看不到它的图标，那么正在运行的实例" +
                    "很可能是在受限环境里启动的 —— 它注册不了托盘图标。\n\n" +
                    "处理方法：打开任务管理器，结束所有 MonitorSleep.exe，" +
                    "然后用 Win + R 重新启动一次。",
                    "显示器睡眠助手", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch
            {
                // 忽略：弹不出来也不能阻塞退出
            }
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        using var showSettingsEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSettingsEventName);

        TrayContext? tray = null;

        Application.ThreadException += (_, e) => ReportFatal(e.Exception, tray);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ReportFatal(e.ExceptionObject as Exception, tray);

        try
        {
            tray = new TrayContext(showSettingsEvent);
            Application.Run(tray);
        }
        catch (Exception ex)
        {
            // 构造托盘外壳时抛的异常以前是静默消失的 —— 连"程序为什么没起来"都查不到。
            // 这里必须落盘。
            ReportFatal(ex, tray);
            throw;
        }
        finally
        {
            tray?.Dispose();
            GC.KeepAlive(mutex);
        }
    }

    /// <summary>
    /// 崩溃路径上最重要的一件事：把系统关屏设置还原。
    /// 否则用户会遇到"显示器再也不会自动关闭"，而且完全不知道是为什么。
    /// </summary>
    private static void ReportFatal(Exception? ex, TrayContext? tray)
    {
        try { tray?.Controller.PrepareForShutdown(); }
        catch { /* 忽略 */ }

        // 优先写在 exe 旁边：受限环境里 TEMP 不一定找得到，用户也更好找
        foreach (string log in new[]
                 {
                     Path.Combine(AppContext.BaseDirectory, "crash.log"),
                     Path.Combine(Path.GetTempPath(), "MonitorSleep-crash.log"),
                 })
        {
            try
            {
                File.AppendAllText(log, $"[{DateTime.Now:O}] {ex}{Environment.NewLine}{Environment.NewLine}");
                break;
            }
            catch
            {
                // 写不进去就换下一个位置
            }
        }
    }

    // ───────────────────────── 诊断模式 ─────────────────────────

    /// <summary>
    /// 无界面自检：把各项系统探测的结果打印出来，供排查"为什么没关屏"。
    /// 用法： MonitorSleep.exe --diagnose [--test-sleep] [--out 文件路径]
    /// </summary>
    private static int RunDiagnostics(string[] args)
    {
        AttachConsole(ATTACH_PARENT_PROCESS);
        try { Console.OutputEncoding = Encoding.UTF8; }
        catch { /* 没有控制台时忽略 */ }

        bool testSleep = args.Any(a => a.Equals("--test-sleep", StringComparison.OrdinalIgnoreCase));
        string? outPath = null;
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals("--out", StringComparison.OrdinalIgnoreCase)) outPath = args[i + 1];
        }

        var store = new SettingsStore();
        var settings = store.Load();
        var sb = new StringBuilder();

        void Line(string s)
        {
            sb.AppendLine(s);
            Console.WriteLine(s);
        }

        Line($"=== 显示器睡眠助手 诊断 {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
        Line($"程序版本      : {typeof(Program).Assembly.GetName().Version}");
        Line($"操作系统      : {Environment.OSVersion}");
        Line($"64 位进程     : {Environment.Is64BitProcess}");
        Line($"数据目录      : {store.DataDirectory}");
        Line($"配置文件      : {store.SettingsPath}（存在：{File.Exists(store.SettingsPath)}）");
        bool configWritable = store.Save(settings);
        Line($"配置写入测试  : {(configWritable ? "成功" : "失败 → " + store.LastError)}");
        Line("");
        Line($"远程桌面会话  : {(NativeMethods.IsRemoteSession() ? "是（会避让）" : "否")}");
        Line($"完整性级别    : {EnvironmentProbe.DescribeIntegrityLevel()}");
        bool trayCapable = EnvironmentProbe.CanAddTrayIcon(out int trayCapabilityError);
        Line($"托盘图标能力  : {(trayCapable
            ? "可注册"
            : $"被系统拒绝（错误码 {trayCapabilityError}：拒绝访问）→ 请从「文件资源管理器」正常启动，不要从受限/沙箱环境拉起")}");

        if (NativeMethods.TryGetPowerStatus(out var ps))
        {
            string ac = ps.ACLineStatus switch { 0 => "电池", 1 => "交流", _ => "未知" };
            string pct = ps.BatteryLifePercent == 255 ? "未知" : ps.BatteryLifePercent + "%";
            Line($"供电状态      : {ac}，电量 {pct}");
        }
        else
        {
            Line("供电状态      : 读取失败");
        }

        bool foregroundFullScreen = NativeMethods.IsForegroundFullScreen(out string foregroundDesc);
        Line($"shell 通知状态: {NativeMethods.GetNotificationState()}");
        Line($"前台窗口      : {foregroundDesc}");
        Line($"前台是否全屏  : {(foregroundFullScreen ? "是" : "否")}");
        Line($"系统空闲时长  : {NativeMethods.GetIdleTime():hh\\:mm\\:ss}");
        Line($"音频播放中    : {Describe(AudioMeter.IsPlaying())}");
        Line($"摄像头占用    : {Describe(CaptureCheck.IsCameraInUse())}");
        Line($"麦克风占用    : {Describe(CaptureCheck.IsMicrophoneInUse())}");
        Line("");
        Line("--- 电源方案 ---");
        var power = new PowerSchemeService(settings);
        Line(power.DescribeCurrent());
        Line(power.DescribeSaved());
        Line($"参数检查      : {NativeMethods.INPUTSizeDescription()}");
        Line("");
        Line("--- 避让判定 ---");
        var guard = new GuardEvaluator().Evaluate(settings, null);
        Line(guard.Blocked ? $"当前会保持常亮，原因：{guard.ReasonText}" : "当前允许自动关屏");
        Line("");
        Line("--- 当前配置摘要 ---");
        Line($"空闲自动关屏  : {(settings.IdleAutoOffEnabled ? settings.IdleMinutes + " 分钟" : "已停用")}");
        Line($"接管系统关屏  : {settings.TakeOverSystemTimeout}");
        Line($"关屏保持系统  : {settings.PreventSystemSleep}");
        Line($"倒计时        : {(settings.ShowCountdown ? settings.CountdownSeconds + " 秒" : "关闭")}");
        Line($"避让开关      : 全屏={settings.BlockOnFullScreen} 音频={settings.BlockOnAudio} " +
             $"采集={settings.BlockOnCapture} 远程={settings.BlockOnRemoteSession} 黑名单={settings.BlockProcesses.Count} 项");
        Line($"热键          : 关屏={settings.SleepHotKey}；电脑睡眠={settings.SystemSleepHotKey}");

        Line("");
        Line("--- 全局热键注册测试（注册后立即释放） ---");
        using (var hk = new HotKeyWindow())
        {
            bool okSleep = hk.Register(HotKeyWindow.IdSleep, settings.SleepHotKey, "关屏", () => { });
            bool okSysSleep = hk.Register(HotKeyWindow.IdSystemSleep, settings.SystemSleepHotKey, "电脑睡眠", () => { });
            Line($"关屏     {settings.SleepHotKey,-24} : {(okSleep ? "可用" : "不可用")}");
            Line($"电脑睡眠 {settings.SystemSleepHotKey,-24} : {(okSysSleep ? "可用" : "不可用")}");
            foreach (string failure in hk.Failures)
                Line("  · " + failure);
        }

        Line("");
        Line("--- 伪唤醒抑制自检 ---");
        try
        {
            using var rawInput = new Core.RawInputWindow();
            foreach (string l in rawInput.SelfTest()) Line(l);
        }
        catch (Exception ex)
        {
            Line($"  ❌ 自检失败：{ex.Message}");
        }

        if (testSleep)
        {
            Line("");
            Line("--- 关屏实测（关闭 2 秒后恢复）---");
            bool off = NativeMethods.BroadcastMonitorPower(NativeMethods.MONITOR_OFF);
            Line($"广播 SC_MONITORPOWER(2) 关屏 : {(off ? "至少有一个窗口响应" : "没有任何窗口响应")}");
            Thread.Sleep(2000);
            bool on = NativeMethods.BroadcastMonitorPower(NativeMethods.MONITOR_ON);
            Line($"广播 SC_MONITORPOWER(-1) 唤醒: {(on ? "至少有一个窗口响应" : "没有任何窗口响应")}");
            bool jiggle = NativeMethods.JiggleMouse();
            Line($"合成鼠标输入（1 像素往返）   : {(jiggle ? "成功" : "失败（结构布局检查未通过）")}");
        }

        string text = sb.ToString();
        try
        {
            Console.WriteLine();
        }
        catch { /* 没有控制台时忽略 */ }

        try
        {
            outPath ??= Path.Combine(store.DataDirectory, "diagnose.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
            // 带 BOM 的 UTF-8：记事本、PowerShell 5.1 等都能正确识别中文
            File.WriteAllText(outPath, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            Console.WriteLine($"报告已写入：{outPath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"报告写入失败：{ex.Message}");
        }

        return 0;
    }

    private static string Describe(bool? value) => value switch
    {
        true => "是",
        false => "否",
        null => "无法判断（无声卡或调用失败）",
    };
}
