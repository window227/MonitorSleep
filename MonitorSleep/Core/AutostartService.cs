using Microsoft.Win32;

namespace MonitorSleep.Core;

/// <summary>开机自启：只写当前用户的 Run 键，不需要管理员权限。</summary>
internal static class AutostartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "MonitorSleep";

    /// <summary>用于写入启动项的命令行（含引号）。</summary>
    public static string? BuildCommandLine()
    {
        string? exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return null;

        // 开发期用 "dotnet run" 启动时 ProcessPath 是 dotnet.exe，需要带上 dll 路径
        if (string.Equals(Path.GetFileName(exe), "dotnet.exe", StringComparison.OrdinalIgnoreCase))
        {
            string? dll = Environment.ProcessPath is null ? null : System.Reflection.Assembly.GetEntryAssembly()?.Location;
            if (string.IsNullOrEmpty(dll)) return null;
            return $"\"{exe}\" \"{dll}\"";
        }

        return $"\"{exe}\"";
    }

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) is string s && s.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    public static bool SetEnabled(bool enabled, out string message)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null)
            {
                message = "无法打开注册表启动项键。";
                return false;
            }

            if (enabled)
            {
                string? cmd = BuildCommandLine();
                if (cmd is null)
                {
                    message = "无法确定程序路径，设置开机自启失败。";
                    return false;
                }
                key.SetValue(ValueName, cmd, RegistryValueKind.String);
                message = $"已设置开机自启：{cmd}";
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                message = "已取消开机自启。";
            }
            return true;
        }
        catch (Exception ex)
        {
            message = $"设置开机自启失败：{ex.Message}";
            return false;
        }
    }
}
