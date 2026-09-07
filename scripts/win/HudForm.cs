using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace HuashuWinUse;

public class HudForm : Form
{
    private const int WS_EX_TOPMOST = 0x00000008;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

    [DllImport("user32.dll")]
    private static extern uint SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

    private readonly string _text;
    private readonly double _durationMs;
    private readonly DateTime _startTime;
    private readonly System.Windows.Forms.Timer _timer;
    private float _pulse = 1.0f;

    public HudForm(string text, double durationMs)
    {
        _text = text;
        _durationMs = durationMs;
        _startTime = DateTime.UtcNow;

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        BackColor = Color.Black;
        TransparencyKey = Color.Black;
        TopMost = true;

        var screen = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
        Bounds = screen;

        DoubleBuffered = true;

        _timer = new System.Windows.Forms.Timer { Interval = 33 };
        _timer.Tick += (s, e) =>
        {
            double elapsed = (DateTime.UtcNow - _startTime).TotalMilliseconds;
            if (elapsed >= _durationMs)
            {
                _timer.Stop();
                Close();
                return;
            }

            _pulse = (float)(0.35 + 0.65 * Math.Abs(Math.Sin(elapsed / 1000.0 * 5.0)));
            Invalidate();
        };
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOPMOST | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_LAYERED | WS_EX_NOACTIVATE;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Exclude HUD from screenshot capture so evidence photos are pristine
        if (Environment.GetEnvironmentVariable("WIN_HUD_CAPTURABLE") != "1")
        {
            _ = SetWindowDisplayAffinity(Handle, WDA_EXCLUDEFROMCAPTURE);
        }
        _timer.Start();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        int alpha = (int)(_pulse * 240);
        using var pen = new Pen(Color.FromArgb(alpha, 217, 90, 46), 10);
        pen.StartCap = LineCap.Round;
        pen.EndCap = LineCap.Round;

        int arm = 140;
        int inset = 8;
        int w = Width;
        int h = Height;

        // Draw 4 corner framing L-shapes
        // Top-Left
        g.DrawLine(pen, inset, inset, inset + arm, inset);
        g.DrawLine(pen, inset, inset, inset, inset + arm);

        // Top-Right
        g.DrawLine(pen, w - inset, inset, w - inset - arm, inset);
        g.DrawLine(pen, w - inset, inset, w - inset, inset + arm);

        // Bottom-Left
        g.DrawLine(pen, inset, h - inset, inset + arm, h - inset);
        g.DrawLine(pen, inset, h - inset, inset, h - inset - arm);

        // Bottom-Right
        g.DrawLine(pen, w - inset, h - inset, w - inset - arm, h - inset);
        g.DrawLine(pen, w - inset, h - inset, w - inset, h - inset - arm);

        // Draw top banner text
        if (!string.IsNullOrWhiteSpace(_text))
        {
            using var font = new Font("Segoe UI", 13, FontStyle.Bold);
            var size = g.MeasureString(_text, font);

            int padX = 20;
            int padY = 8;
            int bw = (int)size.Width + padX * 2;
            int bh = (int)size.Height + padY * 2;
            int bx = (w - bw) / 2;
            int by = 35;

            using var brushBg = new SolidBrush(Color.FromArgb((int)(_pulse * 230), 184, 69, 33));
            using var path = GetRoundedRect(new Rectangle(bx, by, bw, bh), bh / 2);
            g.FillPath(brushBg, path);

            using var brushText = new SolidBrush(Color.White);
            g.DrawString(_text, font, brushText, bx + padX, by + padY);
        }
    }

    private static GraphicsPath GetRoundedRect(Rectangle bounds, int radius)
    {
        int diameter = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
