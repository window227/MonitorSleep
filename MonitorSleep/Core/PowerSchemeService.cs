using MonitorSleep.Interop;

namespace MonitorSleep.Core;

/// <summary>
/// "接管系统关屏"：把电源方案里的"在此时间后关闭显示"改成"从不"，
/// 由本程序按智能判断决定何时关屏；退出 / 崩溃后下次启动自动还原。
///
/// 这是本程序的核心价值：系统自带的定时器会在看视频、开会时照关不误，
/// 于是大多数人干脆设成"从不"，结果反而更费电。
/// </summary>
internal sealed class PowerSchemeService
{
    private AppSettings _settings;

    public PowerSchemeService(AppSettings settings) => _settings = settings;

    /// <summary>设置对象可被整体替换（用户在设置窗口点「确定」时）。</summary>
    public AppSettings Settings
    {
        get => _settings;
        set => _settings = value;
    }

    public string DescribeCurrent()
    {
        if (!NativeMethods.TryReadVideoIdle(out uint ac, out uint dc))
            return "无法读取当前电源方案";
        return $"当前系统关屏超时：交流 {FormatSeconds(ac)} / 电池 {FormatSeconds(dc)}";
    }

    public string DescribeSaved()
    {
        if (!_settings.PowerSchemeRestorePending)
            return "系统关屏设置未被改写";
        return $"待还原的原始值：交流 {FormatSeconds(_settings.SavedAcVideoIdle)} / 电池 {FormatSeconds(_settings.SavedDcVideoIdle)}";
    }

    private static string FormatSeconds(uint seconds) =>
        seconds == 0 ? "从不" : seconds < 60 ? $"{seconds} 秒" : $"{seconds / 60} 分钟";

    /// <summary>把系统关屏设为"从不"。成功时把原值记进 settings 供还原。</summary>
    public bool TakeOver(out string message)
    {
        if (_settings.PowerSchemeRestorePending)
        {
            message = "已经处于接管状态，无需重复操作。";
            return true;
        }

        if (!NativeMethods.TryReadVideoIdle(out uint ac, out uint dc))
        {
            message = "读取电源方案失败，未做任何改动。";
            return false;
        }

        // 本来就是"从不"，无需改写，也就不留下需要还原的状态
        if (ac == 0 && dc == 0)
        {
            message = "系统关屏本来就是「从不」，无需接管。";
            return true;
        }

        if (!NativeMethods.TryWriteVideoIdle(0, 0, out uint oldAc, out uint oldDc))
        {
            message = "写入电源方案失败（可能被组策略或权限限制），未做任何改动。";
            return false;
        }

        _settings.SavedAcVideoIdle = oldAc;
        _settings.SavedDcVideoIdle = oldDc;
        _settings.PowerSchemeRestorePending = true;
        message = $"已接管：系统关屏超时由 交流 {FormatSeconds(oldAc)} / 电池 {FormatSeconds(oldDc)} 改为「从不」。";
        return true;
    }

    /// <summary>还原系统关屏设置。返回是否执行了还原动作。</summary>
    public bool Restore(out string message)
    {
        if (!_settings.PowerSchemeRestorePending)
        {
            message = "无需还原（系统设置未被改写）。";
            return false;
        }

        uint ac = _settings.SavedAcVideoIdle;
        uint dc = _settings.SavedDcVideoIdle;

        if (!NativeMethods.TryWriteVideoIdle(ac, dc, out _, out _))
        {
            message = "还原失败（可能被组策略或权限限制）。设置仍标记为待还原。";
            return false;
        }

        _settings.PowerSchemeRestorePending = false;
        message = $"已还原系统关屏超时：交流 {FormatSeconds(ac)} / 电池 {FormatSeconds(dc)}。";
        return true;
    }

    /// <summary>
    /// 启动时调用：如果上次没有正常退出（崩溃 / 强杀），系统关屏可能仍停在"从不"，
    /// 这里补一次还原，避免用户以为系统坏了。
    /// </summary>
    public bool RestoreIfPendingOnStartup(out string message)
    {
        if (!_settings.PowerSchemeRestorePending)
        {
            message = string.Empty;
            return false;
        }
        return Restore(out message);
    }
}
