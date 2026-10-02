using System.Runtime.InteropServices;
using MonitorSleep.Interop;

namespace MonitorSleep.Core;

/// <summary>
/// 承载全局热键的消息窗口。热键消息 (WM_HOTKEY) 是投递到窗口消息队列的，
/// 因此消息专用窗口（HWND_MESSAGE）也收得到，适合这种不可见的后台窗口。
/// </summary>
internal sealed class HotKeyWindow : NativeWindow, IDisposable
{
    private static readonly IntPtr HWND_MESSAGE = new(-3);

    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN = 0x0008;
    private const uint MOD_NOREPEAT = 0x4000;

    /// <summary>只保留两个：关屏与睡眠。唤醒靠键鼠输入即可，切换没有必要。</summary>
    public const int IdSleep = 1;
    public const int IdSystemSleep = 2;

    private readonly Dictionary<int, Action> _handlers = new();
    private readonly List<string> _failures = new();
    private bool _disposed;

    public IReadOnlyList<string> Failures => _failures;

    public HotKeyWindow()
    {
        var cp = new CreateParams
        {
            Caption = "MonitorSleep.HotKeyWindow",
            Style = 0,
            ExStyle = 0,
            Parent = HWND_MESSAGE,
            X = 0, Y = 0, Width = 0, Height = 0,
        };
        CreateHandle(cp);
    }

    /// <summary>注册一条热键。失败不抛异常，只是记进 Failures。</summary>
    public bool Register(int id, HotKeyBinding binding, string displayName, Action handler)
    {
        if (!binding.IsValid) return false;

        uint mods = MOD_NOREPEAT;
        if (binding.Ctrl) mods |= MOD_CONTROL;
        if (binding.Alt) mods |= MOD_ALT;
        if (binding.Shift) mods |= MOD_SHIFT;
        if (binding.Win) mods |= MOD_WIN;

        if (!NativeMethods.RegisterHotKey(Handle, id, mods, (uint)binding.Key))
        {
            int err = Marshal.GetLastWin32Error();
            _failures.Add($"{displayName}（{binding}）注册失败，错误码 {err}（可能已被其它程序占用）");
            return false;
        }

        _handlers[id] = handler;
        return true;
    }

    public void UnregisterAll()
    {
        foreach (int id in _handlers.Keys)
        {
            try { NativeMethods.UnregisterHotKey(Handle, id); }
            catch { /* 忽略 */ }
        }
        _handlers.Clear();
        _failures.Clear();
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == NativeMethods.WM_HOTKEY)
        {
            int id = m.WParam.ToInt32();
            if (_handlers.TryGetValue(id, out var action))
            {
                try { action(); }
                catch { /* 热键处理里绝不抛异常 */ }
                return;
            }
        }
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        UnregisterAll();
        if (Handle != IntPtr.Zero)
        {
            try { DestroyHandle(); } catch { /* 忽略 */ }
        }
    }
}
