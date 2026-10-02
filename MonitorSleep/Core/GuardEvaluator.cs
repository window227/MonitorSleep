using System.Diagnostics;
using MonitorSleep.Interop;

namespace MonitorSleep.Core;

public sealed class GuardResult
{
    public bool Blocked => Reasons.Count > 0;
    public List<string> Reasons { get; } = new();

    public string ReasonText => Reasons.Count == 0 ? "无" : string.Join("、", Reasons);
}

/// <summary>
/// 智能避让：判断"现在该不该关屏"。
/// 任何一条命中就不同意自动关屏 —— 宁可多耗一点电，也不要打断用户。
/// </summary>
internal sealed class GuardEvaluator
{
    private static readonly TimeSpan ProcessCacheTtl = TimeSpan.FromSeconds(5);
    private DateTime _processCacheAt = DateTime.MinValue;
    private HashSet<string> _runningProcesses = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="lastUnlockAt">最近一次解锁/恢复的时间，用于静默期判断。</param>
    public GuardResult Evaluate(AppSettings s, DateTime? lastUnlockAt)
    {
        var result = new GuardResult();

        // 1. 远程桌面：关的是对面那台机器的屏，通常毫无意义
        if (s.BlockOnRemoteSession && NativeMethods.IsRemoteSession())
            result.Reasons.Add("远程桌面会话中");

        // 2. 全屏 / 演示 / 忙
        if (s.BlockOnFullScreen)
        {
            var state = NativeMethods.GetNotificationState();
            switch (state)
            {
                case NativeMethods.UserNotificationState.RunningD3dFullScreen:
                    result.Reasons.Add("全屏独占程序运行中");
                    break;
                case NativeMethods.UserNotificationState.PresentationMode:
                    result.Reasons.Add("演示模式");
                    break;
                case NativeMethods.UserNotificationState.Busy:
                    // QUNS_BUSY 在部分环境下会误报，必须再确认前台窗口真的占满整块屏幕，
                    // 否则自动关屏会被永久挡住。
                    if (NativeMethods.IsForegroundFullScreen(out _))
                        result.Reasons.Add("全屏程序运行中");
                    break;
            }
        }

        // 3. 正在放声音（看视频 / 听歌 / 开会）
        if (s.BlockOnAudio && AudioMeter.IsPlaying() == true)
            result.Reasons.Add("正在播放音频");

        // 4. 摄像头或麦克风被占用（正在视频通话）
        if (s.BlockOnCapture && (CaptureCheck.IsCameraInUse() || CaptureCheck.IsMicrophoneInUse()))
            result.Reasons.Add("摄像头/麦克风使用中");

        // 5. 用户指定的进程黑名单
        if (s.BlockProcesses.Count > 0 && AnyBlockedProcessRunning(s.BlockProcesses))
            result.Reasons.Add("黑名单进程运行中");

        // 6. 电源策略
        if (s.PowerPolicy != PowerPolicy.Always && NativeMethods.TryGetPowerStatus(out var ps))
        {
            bool onBattery = ps.ACLineStatus == 0;
            if (s.PowerPolicy == PowerPolicy.BatteryOnly && !onBattery)
                result.Reasons.Add("当前为交流供电（策略：仅电池）");
            if (s.PowerPolicy == PowerPolicy.AcOnly && onBattery)
                result.Reasons.Add("当前为电池供电（策略：仅交流）");
        }

        // 7. 解锁后的静默期
        if (s.GraceAfterUnlockSeconds > 0 && lastUnlockAt is DateTime unlock)
        {
            if (DateTime.UtcNow - unlock < TimeSpan.FromSeconds(s.GraceAfterUnlockSeconds))
                result.Reasons.Add("刚解锁不久");
        }

        return result;
    }

    private bool AnyBlockedProcessRunning(List<string> names)
    {
        RefreshProcessCacheIfStale();
        foreach (string raw in names)
        {
            string name = raw.Trim();
            if (name.Length == 0) continue;
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                name = name[..^4];
            if (_runningProcesses.Contains(name)) return true;
        }
        return false;
    }

    private void RefreshProcessCacheIfStale()
    {
        if (DateTime.UtcNow - _processCacheAt < ProcessCacheTtl) return;

        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var p in Process.GetProcesses())
            {
                try { set.Add(p.ProcessName); }
                catch { /* 个别进程读不到名字，跳过 */ }
                finally { p.Dispose(); }
            }
        }
        catch
        {
            // 枚举失败就沿用上一次的结果
        }

        _runningProcesses = set;
        _processCacheAt = DateTime.UtcNow;
    }
}
