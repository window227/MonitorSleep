using System.Text;

namespace MonitorSleep.Core;

/// <summary>
/// 运行日志：记下「谁在什么时候做了什么」，用来回答这类问题 ——
/// 屏幕为什么自己亮了？该关的时候为什么没关？昨晚到底睡没睡？
///
/// 几条刻意的设计：
///
/// · **绝不抛异常**。日志失败不能连累程序本身，所有写入都吞掉异常。
/// · **有大小上限**。超过 <see cref="MaxBytes"/> 就轮转成 .1，只留一份历史，
///   免得日志无限长大。
/// · **避让拦截要合并**。避让判定每秒都在跑，逐秒记录会把日志刷爆 ——
///   调用方只在「原因发生变化」时记一条。
/// </summary>
public sealed class EventLog
{
    /// <summary>单个日志文件的大小上限，超过就轮转。</summary>
    public const long MaxBytes = 512 * 1024;

    private const string FileName = "MonitorSleep.log";
    private const string RotatedName = "MonitorSleep.1.log";

    private readonly object _gate = new();
    private readonly string _path;
    private readonly string _rotatedPath;

    private long _bytes;

    public EventLog(string dataDirectory)
    {
        _path = Path.Combine(dataDirectory, FileName);
        _rotatedPath = Path.Combine(dataDirectory, RotatedName);

        try { _bytes = File.Exists(_path) ? new FileInfo(_path).Length : 0; }
        catch { _bytes = 0; }
    }

    /// <summary>关掉后所有写入都变成空操作。</summary>
    public bool Enabled { get; set; } = true;

    public string FilePath => _path;

    /// <summary>文件当前大小（字节），给界面显示用。</summary>
    public long CurrentBytes { get { lock (_gate) return _bytes; } }

    public void Write(string category, string message)
    {
        if (!Enabled) return;

        lock (_gate)
        {
            try
            {
                RotateIfNeeded();

                string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  [{category}]  {message}{Environment.NewLine}";
                var bytes = Encoding.UTF8.GetBytes(line);

                using var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                stream.Write(bytes, 0, bytes.Length);
                _bytes += bytes.Length;
            }
            catch
            {
                // 日志写不进去（磁盘满、权限不足……）不能影响主流程
            }
        }
    }

    public void Write(string category, string message, Exception error) =>
        Write(category, $"{message} —— {error.GetType().Name}: {error.Message}");

    /// <summary>写一条分隔线，让每次启动之间的日志一眼能分开。</summary>
    public void WriteSessionStart(string version)
    {
        if (!Enabled) return;
        Write("程序", $"启动 v{version}");
    }

    private void RotateIfNeeded()
    {
        if (_bytes < MaxBytes) return;

        try
        {
            if (File.Exists(_rotatedPath)) File.Delete(_rotatedPath);
            if (File.Exists(_path)) File.Move(_path, _rotatedPath);
        }
        catch
        {
            // 轮转失败就继续往原文件追加，总比丢日志强
        }

        _bytes = 0;
    }
}
