namespace MonitorSleep.UI;

/// <summary>
/// 兜底方案：当系统不响应 SC_MONITORPOWER（例如全屏独占程序吞掉了广播消息）时，
/// 用一块置顶全屏黑窗把画面盖住。背光仍然亮着，省电有限，但保证"看不见内容"。
/// 点击或按任意键即可退出。
/// </summary>
internal sealed class BlackoutForm : Form
{
    private readonly Label _hint;
    private readonly System.Windows.Forms.Timer _hintTimer;
    private readonly System.Windows.Forms.Timer _safetyTimer;

    public BlackoutForm(Screen screen)
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = screen.Bounds;
        BackColor = Color.Black;
        ShowInTaskbar = false;
        TopMost = true;
        KeyPreview = true;
        Cursor = Cursors.Default;

        _hint = new Label
        {
            Dock = DockStyle.Fill,
            ForeColor = Color.FromArgb(70, 70, 70),
            BackColor = Color.Transparent,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Microsoft YaHei UI", 11f),
            Text = "点击屏幕或按任意键即可恢复",
        };
        Controls.Add(_hint);

        _hintTimer = new System.Windows.Forms.Timer { Interval = 3000 };
        _hintTimer.Tick += (_, _) => { _hintTimer.Stop(); _hint.Visible = false; };

        // 安全网：无论发生什么，最多 8 小时后自我解除，避免把用户困在黑屏里
        _safetyTimer = new System.Windows.Forms.Timer { Interval = (int)TimeSpan.FromHours(8).TotalMilliseconds };
        _safetyTimer.Tick += (_, _) => Close();
    }

    protected override bool ShowWithoutActivation => false;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW
            return cp;
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Activate();
        _hintTimer.Start();
        _safetyTimer.Start();
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        Close();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        Close();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hintTimer.Dispose();
            _safetyTimer.Dispose();
        }
        base.Dispose(disposing);
    }
}
