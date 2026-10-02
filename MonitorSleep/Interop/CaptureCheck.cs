using Microsoft.Win32;

namespace MonitorSleep.Interop;

/// <summary>
/// 判断摄像头 / 麦克风当前是否被占用（即：是否正在开会 / 视频通话）。
/// 数据来源是 Windows 隐私设置自己维护的 ConsentStore 注册表键：
/// LastUsedTimeStop == 0 且 LastUsedTimeStart != 0 表示"仍在使用中"。
/// </summary>
internal static class CaptureCheck
{
    private const string ConsentStoreRoot =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore";

    public static bool IsCameraInUse() => IsDeviceInUse("webcam");

    public static bool IsMicrophoneInUse() => IsDeviceInUse("microphone");

    private static bool IsDeviceInUse(string deviceKeyName)
    {
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey($@"{ConsentStoreRoot}\{deviceKeyName}");
            if (root is null) return false;

            foreach (string childName in root.GetSubKeyNames())
            {
                using var child = root.OpenSubKey(childName);
                if (child is null) continue;

                // 打包应用直接挂在下面；非打包（传统 Win32）应用多一层 NonPackaged。
                if (string.Equals(childName, "NonPackaged", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (string appName in child.GetSubKeyNames())
                    {
                        using var app = child.OpenSubKey(appName);
                        if (IsActive(app)) return true;
                    }
                }
                else if (IsActive(child))
                {
                    return true;
                }
            }
        }
        catch
        {
            // 读不到就当没占用
        }
        return false;
    }

    private static bool IsActive(RegistryKey? key)
    {
        if (key is null) return false;
        try
        {
            long start = ReadFileTime(key, "LastUsedTimeStart");
            long stop = ReadFileTime(key, "LastUsedTimeStop");
            return start != 0 && stop == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>该值在不同 Windows 版本下可能是 REG_BINARY(8 字节小端) 或 REG_QWORD。</summary>
    private static long ReadFileTime(RegistryKey key, string name)
    {
        object? raw = key.GetValue(name);
        return raw switch
        {
            null => 0L,
            long l => l,
            int i => i,
            byte[] b when b.Length >= 8 => BitConverter.ToInt64(b, 0),
            _ => 0L,
        };
    }
}
