using MonitorSleep.Interop;

namespace MonitorSleep.UI;

/// <summary>
/// 关屏前的倒计时提示：右下角一个小窗，5 秒内可点「取消」。
/// 刻意不抢焦点，避免正在打字的用户被夺走输入。
/// </summary>
internal sealed class CountdownForm : Form
{
    private readonly System.Windows.Forms.Timer _timer;
    private readonly Action _onElapsed;
    private readonly Action _onCancelled;
    private readonly Label _label;
    private readonly string _textTemplate;
    private int _remaining;
    private bool _finished;
    private bool _escapeRegistered;

    private const int VkEscape = 0x1B;
    private const int HotKeyIdCancel = 0xC1;

    /// <param name="textTemplate">倒计时文案，{0} 会被替换成剩余秒数。</param>
    public CountdownForm(int seconds, string textTemplate, Action onElapsed, Action onCancelled)
    {
        _remaining = Math.Max(1, seconds);
        _textTemplate = string.IsNullOrEmpty(textTemplate) ? "将在 {0} 秒后执行" : textTemplate;
        _onElapsed = onElapsed;
        _onCancelled = onCancelled;

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Color.FromArgb(32, 34, 38);
        Size = new Size(320, 84);
        Padding = new Padding(1);

        _label = new Label
        {
            Dock = DockStyle.Fill,
            ForeColor = Color.White,
            BackColor = Color.Transparent,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Microsoft YaHei UI", 11f, FontStyle.Regular),
            Text = BuildText(),
        };

        var cancel = new Button
        {
            Text = "取消 (Esc)",
            Dock = DockStyle.Right,
            Width = 96,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(58, 62, 70),
            ForeColor = Color.White,
            Cursor = Cursors.Hand,
        };
        cancel.FlatAppearance.BorderSize = 0;
        cancel.Click += (_, _) => Finish(cancelled: true);

        Controls.Add(_label);
        Controls.Add(cancel);

        _timer = new System.Windows.Forms.Timer { Interval = 1000 };
        _timer.Tick += (_, _) =>
        {
            _remaining--;
            if (_remaining <= 0) Finish(cancelled: false);
            else _label.Text = BuildText();
        };
    }

    private string BuildText() => string.Format(_textTemplate, _remaining);

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW：不出现在 Alt+Tab
            return cp;
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        PositionBottomRight();
        _timer.Start();
        RegisterEscapeKey();

        // 安全网：万一计时器没跑，最多 3 倍时长后也必须收尾
        var guard = new System.Windows.Forms.Timer { Interval = Math.Max(5000, _remaining * 3000 + 5000) };
        guard.Tick += (_, _) => { guard.Stop(); guard.Dispose(); Finish(cancelled: false); };
        guard.Start();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(Color.FromArgb(90, 62, 166, 246), 1f);
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }

    /// <summary>
    /// 用全局热键接住 ESC。
    ///
    /// 这个窗口是刻意"不抢焦点"的（ShowWithoutActivation），否则会打断正在打字的用户，
    /// 而键盘事件只会送到有焦点的窗口 —— 所以普通按键根本到不了这里。
    /// 全局热键是唯一既能收到 ESC、又不夺走焦点的办法。
    /// </summary>
    private void RegisterEscapeKey()
    {
        if (!IsHandleCreated) return;

        _escapeRegistered = NativeMethods.RegisterHotKey(
            Handle, HotKeyIdCancel, (uint)NativeMethods.HotKeyModifiers.NoRepeat, VkEscape);

        if (!_escapeRegistered)
        {
            // 极少数情况下 MOD_NOREPEAT 不被接受，退回不带该标志再试一次
            _escapeRegistered = NativeMethods.RegisterHotKey(Handle, HotKeyIdCancel, 0, VkEscape);
        }
    }

    private void UnregisterEscapeKey()
    {
        if (!_escapeRegistered || !IsHandleCreated) return;
        try { NativeMethods.UnregisterHotKey(Handle, HotKeyIdCancel); }
        catch { /* 忽略 */ }
        _escapeRegistered = false;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == NativeMethods.WM_HOTKEY && m.WParam.ToInt32() == HotKeyIdCancel)
        {
            Finish(cancelled: true);
            return;
        }
        base.WndProc(ref m);
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        UnregisterEscapeKey();
        base.OnHandleDestroyed(e);
    }

    private void PositionBottomRight()
    {
        var wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        Location = new Point(wa.Right - Width - 16, wa.Bottom - Height - 16);
    }

    private void Finish(bool cancelled)
    {
        if (_finished) return;
        _finished = true;

        _timer.Stop();
        _timer.Dispose();

        Close();

        if (cancelled) _onCancelled();
        else _onElapsed();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer.Dispose();
        base.Dispose(disposing);
    }
}
