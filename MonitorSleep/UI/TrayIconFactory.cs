using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace MonitorSleep.UI;

/// <summary>
/// 程序化绘制托盘图标，免去携带 .ico 资源文件。
///
/// 刻意用"实心块"而不是细线条：托盘实际显示尺寸往往只有 16×16，
/// 2~3 像素的描边缩下去会糊成一团，实心形状则一直清晰可辨。
/// </summary>
internal static class TrayIconFactory
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    public static Icon Create(bool displayOff)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            Color body = displayOff
                ? Color.FromArgb(255, 150, 154, 162)   // 关屏：灰色
                : Color.FromArgb(255, 32, 150, 243);    // 亮屏：蓝色

            using (var brush = new SolidBrush(body))
            {
                g.FillRectangle(brush, 3f, 6f, 26f, 17f);   // 屏幕外框
                g.FillRectangle(brush, 14f, 23f, 4f, 4f);   // 支架
                g.FillRectangle(brush, 8f, 27f, 16f, 3f);   // 底座
            }

            // 屏幕内挖深色，形成"显示器"的轮廓
            using (var inner = new SolidBrush(displayOff
                ? Color.FromArgb(255, 58, 60, 66)
                : Color.FromArgb(255, 10, 32, 52)))
            {
                g.FillRectangle(inner, 6f, 9f, 20f, 11f);
            }

            if (displayOff)
            {
                // 关屏状态：一道醒目的红色斜杠，和"亮屏"一眼分得开
                using var slash = new Pen(Color.FromArgb(255, 235, 72, 72), 4.5f)
                {
                    StartCap = LineCap.Round,
                    EndCap = LineCap.Round,
                };
                g.DrawLine(slash, 7f, 26f, 25f, 6f);
            }
            else
            {
                // 亮屏状态：一块浅色高光，让它在图标堆里更好认
                using var glow = new SolidBrush(Color.FromArgb(255, 140, 214, 255));
                g.FillRectangle(glow, 9f, 12f, 7f, 4f);
            }
        }

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
