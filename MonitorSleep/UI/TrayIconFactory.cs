using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace MonitorSleep.UI;

/// <summary>
/// 程序化绘制图标，免去携带 .ico 资源文件。
///
/// 刻意用"实心块"而不是细线条：托盘实际显示尺寸往往只有 16×16，
/// 2~3 像素的描边缩下去会糊成一团，实心形状则一直清晰可辨。
///
/// 设计基准是 32×32，其它尺寸按比例缩放 —— 设置界面顶部的品牌区
/// 用的是同一套画法，保证和托盘图标长得一模一样。
/// </summary>
internal static class TrayIconFactory
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    /// <summary>按指定边长画一张图标位图（调用方负责释放）。</summary>
    public static Bitmap Draw(int size, bool displayOff)
    {
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        float s = size / 32f;   // 以 32px 设计稿为基准

        Color body = displayOff
            ? Color.FromArgb(255, 150, 154, 162)   // 关屏：灰色
            : Color.FromArgb(255, 32, 150, 243);    // 亮屏：蓝色

        using (var brush = new SolidBrush(body))
        {
            g.FillRectangle(brush, 3f * s, 6f * s, 26f * s, 17f * s);   // 屏幕外框
            g.FillRectangle(brush, 14f * s, 23f * s, 4f * s, 4f * s);   // 支架
            g.FillRectangle(brush, 8f * s, 27f * s, 16f * s, 3f * s);   // 底座
        }

        // 屏幕内挖深色，形成"显示器"的轮廓
        using (var inner = new SolidBrush(displayOff
            ? Color.FromArgb(255, 58, 60, 66)
            : Color.FromArgb(255, 10, 32, 52)))
        {
            g.FillRectangle(inner, 6f * s, 9f * s, 20f * s, 11f * s);
        }

        if (displayOff)
        {
            // 关屏状态：一道醒目的红色斜杠，和"亮屏"一眼分得开
            using var slash = new Pen(Color.FromArgb(255, 235, 72, 72), 4.5f * s)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
            };
            g.DrawLine(slash, 7f * s, 26f * s, 25f * s, 6f * s);
        }
        else
        {
            // 亮屏状态：一块浅色高光，让它在图标堆里更好认
            using var glow = new SolidBrush(Color.FromArgb(255, 140, 214, 255));
            g.FillRectangle(glow, 9f * s, 12f * s, 7f * s, 4f * s);
        }

        return bmp;
    }

    public static Icon Create(bool displayOff)
    {
        using var bmp = Draw(32, displayOff);

        IntPtr handle = bmp.GetHicon();
        try
        {
            // Clone 一份脱离原生句柄，之后才能安全 DestroyIcon
            using var temp = Icon.FromHandle(handle);
            return (Icon)temp.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }
}
