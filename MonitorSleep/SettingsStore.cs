using System.Text.Json;

namespace MonitorSleep;

/// <summary>
/// 配置读写。
///
/// 正常情况下落在 %APPDATA%\MonitorSleep\settings.json。
/// 但在受限（低完整性）环境里那个目录是写不进去的 —— 而 exe 自己所在的目录通常可以。
/// 所以这里按顺序探测几个候选位置，用第一个真正能写的：
/// 这样即便在沙箱里跑，设置和统计也还能存下来，而不是每次重启都回到默认值。
/// </summary>
internal sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
    };

    public string DataDirectory { get; }
    public string SettingsPath { get; }

    /// <summary>是否退到了备用位置（%APPDATA% 不可写）。</summary>
    public bool UsedFallbackLocation { get; private set; }

    public SettingsStore()
    {
        var candidates = BuildCandidates();

        DataDirectory = candidates[0];
        for (int i = 0; i < candidates.Count; i++)
        {
            if (!TryEnsureWritable(candidates[i])) continue;
            DataDirectory = candidates[i];
            UsedFallbackLocation = i > 0;
            break;
        }

        SettingsPath = Path.Combine(DataDirectory, "settings.json");
    }

    private static List<string> BuildCandidates()
    {
        var list = new List<string>(4);

        string? overridden = Environment.GetEnvironmentVariable("MONITORSLEEP_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            list.Add(overridden);
        }
        else
        {
            list.Add(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MonitorSleep"));
        }

        // 受限环境里 %APPDATA% 写不了，但 exe 旁边一般可以
        list.Add(Path.Combine(AppContext.BaseDirectory, "MonitorSleep-data"));

        // 最后兜底：临时目录（低完整性进程通常可写）
        list.Add(Path.Combine(Path.GetTempPath(), "MonitorSleep"));

        return list;
    }

    /// <summary>真正写一个探针文件来判断，别只看目录存不存在。</summary>
    private static bool TryEnsureWritable(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            string probe = Path.Combine(directory, ".write-probe");
            File.WriteAllText(probe, "x");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                string json = File.ReadAllText(SettingsPath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (loaded is not null)
                {
                    loaded.BlockProcesses ??= new List<string>();
                    if (Migrate(loaded))
                        Save(loaded);   // 迁移结果立刻落盘，避免每次启动都重算
                    return loaded;
                }
            }
        }
        catch
        {
            // 配置损坏时退回默认值，并把坏文件留个备份
            TryBackupCorrupted();
        }
        return new AppSettings();
    }

    /// <summary>当前配置结构版本。</summary>
    private const int CurrentSettingsVersion = 2;

    /// <summary>
    /// 把旧版本留下的、有风险的默认值一次性纠正掉。
    ///
    /// v1 的默认值是「接管系统关屏 = 开」「播放音频保持常亮 = 开」，两个都是净变差：
    /// 前者会把系统超时改成"从不"，让屏幕比原来亮得更久，程序一旦不跑就再也不关屏；
    /// 后者靠音量峰值判断，分不清视频和音乐，听歌时也不关屏（而 Windows 分得清）。
    ///
    /// 老配置里这两个值通常只是当初的默认值、并非用户主动选择，所以这里直接纠正。
    /// 确实想要的话，到设置里的「高级」页重新打开即可 —— 打开时程序会给出风险提示。
    /// </summary>
    private static bool Migrate(AppSettings settings)
    {
        if (settings.SettingsVersion >= CurrentSettingsVersion) return false;

        settings.TakeOverSystemTimeout = false;
        settings.BlockOnAudio = false;
        settings.SettingsVersion = CurrentSettingsVersion;
        return true;
    }

    /// <summary>最近一次保存失败的原因（没失败则为 null）。配置写不出去，用户应该能知道。</summary>
    public string? LastError { get; private set; }

    public bool Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            string json = JsonSerializer.Serialize(settings, JsonOptions);

            // 先写临时文件再替换，避免掉电/崩溃留下半截 JSON
            string tmp = SettingsPath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, SettingsPath, overwrite: true);
            LastError = null;
            return true;
        }
        catch (Exception ex)
        {
            LastError = $"{SettingsPath} → {ex.Message}";
            return false;
        }
    }

    private void TryBackupCorrupted()
    {
        try
        {
            if (File.Exists(SettingsPath))
                File.Move(SettingsPath, SettingsPath + ".corrupted", overwrite: true);
        }
        catch
        {
            // 忽略
        }
    }
}
