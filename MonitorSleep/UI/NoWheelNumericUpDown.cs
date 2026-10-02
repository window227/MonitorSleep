using System.Runtime.InteropServices;

namespace MonitorSleep.UI;

/// <summary>
/// 忽略鼠标滚轮的数字输入框。
///
/// NumericUpDown 默认会被滚轮直接改值：用户在设置页里往下滚，
/// 指针只要划过「空闲 10 分钟」这类输入框，数值就被悄悄改掉了，
/// 而且界面毫无提示 —— 很容易第二天才发现自动关屏变成了别的时长。
///
/// 这里吃掉滚轮事件，并把它原样转发给父容器，让页面照常滚动。
/// </summary>
internal sealed class NoWheelNumericUpDown : NumericUpDown
{
    private const int WM_MOUSEWHEEL = 0x020A;

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        // 关键：故意不调用 base —— base 正是那个会改数值的实现。

        if (Parent is not { IsHandleCreated: true } parent) return;

        try
        {
            // 把滚轮转发给父容器，页面滚动不受影响
            Point inParent = parent.PointToClient(PointToScreen(e.Location));
            IntPtr wParam = (IntPtr)(e.Delta << 16);
            IntPtr lParam = (IntPtr)((inParent.Y << 16) | (inParent.X & 0xFFFF));
            SendMessage(parent.Handle, WM_MOUSEWHEEL, wParam, lParam);
        }
        catch
        {
            // 转发失败也无所谓：数值不会变才是重点
        }
    }
}
