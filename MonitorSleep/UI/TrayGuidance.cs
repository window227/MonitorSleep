using System.Diagnostics;

namespace MonitorSleep.UI;

/// <summary>启动提示。把"启动了却什么都没有"变成一句说得清的话。</summary>
internal static class TrayGuidance
{
    /// <summary>
    /// 托盘图标加不上时调用。
    ///
    /// 典型场景：程序被放在低完整性级别的受限环境里运行 —— 例如从 DSH 这类工具里拉起。
    /// 这种情况下配置保存和电源方案接管同样会失败，必须一次性讲明白，
    /// 而且要给出一个**真正可行**的启动办法。
    ///
    /// 关键点：光说"用资源管理器打开"是不够的 ——
    /// 从别的程序里唤起的资源管理器窗口本身也可能受限，
    /// 在它里面双击 exe，孩子进程照样是低完整性。
    /// 所以这里直接给 Win + R 的路径，并把命令行复制到剪贴板。
    /// </summary>
    public static void ShowEnvironmentBlocked(int trayError, string integrityLevel, string? configError, string exePath)
    {
        string directory = Path.GetDirectoryName(exePath) ?? exePath;

        string text =
            "显示器睡眠助手已经启动，但无法显示托盘图标。\n\n" +
            $"系统拒绝注册托盘图标（错误码 {trayError}：拒绝访问）。\n" +
            $"当前进程的完整性级别：{integrityLevel}\n\n" +
            "【真正的原因】\n" +
            "程序所在的目录带有「低完整性」强制标签。\n" +
            "Windows 创建进程时，会取「父进程令牌」和「可执行文件标签」中较低的那个，\n" +
            "所以只要 exe 放在这个目录里，无论用哪种方式启动都必然是低完整性。\n" +
            "而低完整性进程被系统一律禁止：注册托盘图标、写 %APPDATA%、改电源方案。\n\n" +
            $"　当前目录：{directory}\n\n" +
            "【解决办法】只差一步\n" +
            "把这个文件夹整个复制到工作区以外的地方（例如 D:\\01tool\\ 或桌面），\n" +
            "然后从新位置双击 MonitorSleep.exe 即可。\n" +
            "复制出来的文件不再带那个标签，进程会是正常的 Medium 完整性。\n\n" +
            "（本次运行中全局热键仍然有效：Ctrl + Alt + O 关屏。）";

        try
        {
            MessageBox.Show(text, "显示器睡眠助手 — 需要换个位置运行",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch
        {
            // 忽略
        }
    }

    /// <summary>
    /// 托盘图标正常，但 Windows 11 默认会把新图标折叠进「^」溢出区，
    /// 第一次运行时说清楚去哪找。
    /// </summary>
    public static void ShowFirstRun()
    {
        DialogResult result;
        try
        {
            result = MessageBox.Show(
                "显示器睡眠助手已经启动，正在后台运行。\n\n" +
                "你大概在通知区域看不到它的图标 —— 这是 Windows 11 的默认行为：\n" +
                "新出现的托盘图标会被折叠进任务栏右下角的「^」溢出区。\n\n" +
                "　· 图标是一块蓝色的实心显示器；屏幕关闭时会变成灰色加一道红色斜杠\n" +
                "　· 点一下那个 ^，就能在里面找到它\n" +
                "　· 想让它常驻显示：右键任务栏 → 任务栏设置 → 其他系统托盘图标 → 打开\n",
                "显示器睡眠助手 — 首次运行",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch
        {
            return;
        }
    }

    /// <summary>打开 Windows 的任务栏设置页（图标是否常驻在那里开关）。</summary>
    public static void OpenTaskbarSettings()
    {
        StartUri("ms-settings:taskbar");
        StartUri("ms-settings:personalization-taskbar");
    }

    private static bool TryCopyToClipboard(string text)
    {
        // 剪贴板可能被别的程序占着，重试几次
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                Clipboard.SetText(text);
                return true;
            }
            catch
            {
                System.Threading.Thread.Sleep(120);
            }
        }
        return false;
    }

    private static void StartUri(string uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
        }
        catch
        {
            // 打不开就算了
        }
    }
}
