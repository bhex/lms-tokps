using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace LmsTokps;

/// <summary>
/// A small always-on-top, draggable card that shows live tokens/second,
/// plus a systray (NotifyIcon) with a menu.
/// </summary>
public sealed class WidgetForm : Form
{
    private static readonly Color Bg = Color.FromArgb(23, 26, 32);
    private static readonly Color Fg = Color.FromArgb(242, 245, 250);
    private static readonly Color Muted = Color.FromArgb(140, 148, 162);
    private static readonly Color Live = Color.FromArgb(76, 195, 138);
    private static readonly Color Idle = Color.FromArgb(96, 106, 122);

    private readonly Settings _settings;

    // ---- display state (drawn by DrawCard, pushed with Present) --------
    private string _statusText = "IDLE";
    private Color _statusColor = Muted;
    private Color _dotColor = Idle;
    private string _valueText = "—";
    private Color _valueColor = Muted;
    private string _subText = "waiting for server activity…";

    private static readonly Font StatusFont = new("Segoe UI", 8.5f, FontStyle.Bold);
    private static readonly Font ValueFont = new("Segoe UI", 11f, FontStyle.Bold);
    private static readonly Font SubFont = new("Segoe UI", 9.5f);

    private readonly Queue<double?> _sparkData = new();
    private int _sparkCap = 60;
    private Color _sparkColor = Muted;

    private NotifyIcon _tray;
    private readonly Icon _iconLive;
    private readonly Icon _iconIdle;
    private readonly ContextMenuStrip _menu;
    private ToolStripMenuItem _toggleItem;
    private ToolStripMenuItem _pauseItem;



    public WidgetForm(Settings settings)
    {
        _settings = settings;
        _iconLive = TrayIcon(Live);
        _iconIdle = TrayIcon(Idle);

        // ---- form chrome ------------------------------------------------
        // The card background is fully transparent (layered window); only the
        // dot, graph and text are painted, straight onto the desktop.
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        ShowInTaskbar = false;
        BackColor = Bg; // only a placeholder; the visible surface is the layered bitmap
        DoubleBuffered = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        Size = new Size(300, 170);
        MinimumSize = Size;

        // ---- tray + menu ------------------------------------------------
        _menu = new ContextMenuStrip();
        _toggleItem = new ToolStripMenuItem("Hide widget", null, (s, e) => SetVisible(false));
        _pauseItem = new ToolStripMenuItem(_settings.Paused ? "Resume updates" : "Pause updates",
            null, (s, e) => TogglePause());
        var dock = new ToolStripMenuItem("Dock bottom-right", null, (s, e) => DockBottomRight());
        var open = new ToolStripMenuItem("Open logs folder", null, (s, e) => OpenLogsFolder());
        var quit = new ToolStripMenuItem("Quit", null, (s, e) => Quit());
        _menu.Items.AddRange(new ToolStripItem[]
        {
            _toggleItem, _pauseItem, dock, new ToolStripSeparator(), open, quit
        });

        _tray = new NotifyIcon
        {
            Icon = _iconIdle,
            Text = "LM Studio  —  waiting for activity",
            Visible = true,
            ContextMenuStrip = _menu,
        };
        _tray.DoubleClick += (s, e) => SetVisible(true);

        // ---- interactions ---------------------------------------------
        // The card itself is not movable (the background is fully transparent
        // and click-through); right-click still opens the menu, and the tray
        // icon offers the full menu including "Dock bottom-right".
        MouseClick += (s, e) =>
        {
            if (e.Button == MouseButtons.Right) _menu.Show(this, Point.Empty);
        };

        FormClosing += (s, e) =>
        {
            if (e.CloseReason == CloseReason.UserClosing && !QuitRequested)
            {
                // Treat a plain "close" (e.g. task manager or Win+D) as hide,
                // not quit.
                e.Cancel = true;
                SetVisible(false);
            }
            else
            {
                _tray.Visible = false;
                _tray.Dispose();
                _settings.WindowX = Location.X;
                _settings.WindowY = Location.Y;
                _settings.Save();
            }
        };

        PlaceInitial();
    }

    private bool QuitRequested { get; set; }

    public void Quit()
    {
        QuitRequested = true;
        Close();
    }

    // ---- tray icon ----------------------------------------------------
    // Bold glyph that fills most of the canvas, bundled at 16/24/32/48 px
    // so the shell uses a native size instead of downscaling a blurry one.
    private static Icon TrayIcon(Color dot)
    {
        int[] sizes = { 16, 24, 32, 48 };
        var images = new byte[sizes.Length][];
        for (int i = 0; i < sizes.Length; i++) images[i] = TrayImage(sizes[i], dot);

        using var ms = new MemoryStream();
        using (var bw = new BinaryWriter(ms))
        {
            bw.Write((short)0);                  // reserved
            bw.Write((short)1);                  // type: icon
            bw.Write((short)sizes.Length);       // image count
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                bw.Write((byte)sizes[i]);        // width  (0 = 256)
                bw.Write((byte)sizes[i]);        // height
                bw.Write((byte)0);               // palette
                bw.Write((byte)0);               // reserved
                bw.Write((short)1);              // planes
                bw.Write((short)32);             // bpp
                bw.Write(images[i].Length);      // data size
                bw.Write(offset);                // data offset
                offset += images[i].Length;
            }
            foreach (var img in images) bw.Write(img);
        }
        var bytes = ms.ToArray();
        using var iconStream = new MemoryStream(bytes);
        return new Icon(iconStream);
    }

    private static byte[] TrayImage(int s, Color dot)
    {
        using var bmp = new Bitmap(s, s);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            float m = s * 0.08f;                  // dot nearly fills the canvas
            using (var br = new SolidBrush(dot))
                g.FillEllipse(br, m, m, s - 2 * m, s - 2 * m);
            using (var pen = new Pen(Color.FromArgb(225, 242, 245, 250),
                                     Math.Max(1.6f, s * 0.10f)))
                g.DrawLines(pen, new[]           // bold "waveform" = throughput
                {
                    new PointF(s * 0.28f, s * 0.66f),
                    new PointF(s * 0.41f, s * 0.41f),
                    new PointF(s * 0.53f, s * 0.72f),
                    new PointF(s * 0.72f, s * 0.38f),
                });
        }

        // Encode as a 32-bpp bottom-up DIB (BITMAPINFOHEADER + pixels + zero AND mask).
        var rect = new Rectangle(0, 0, s, s);
        var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        int stride = data.Stride;
        int rowBytes = s * 4;
        var pixels = new byte[rowBytes * s];
        for (int y = 0; y < s; y++)
        {
            var src = new IntPtr(data.Scan0.ToInt64() + (long)(s - 1 - y) * stride);
            Marshal.Copy(src, pixels, y * rowBytes, rowBytes);
        }
        bmp.UnlockBits(data);

        int andRow = ((s + 31) / 32) * 4;
        using var ms = new MemoryStream();
        using (var bw = new BinaryWriter(ms))
        {
            bw.Write(40);                        // header size
            bw.Write(s);                         // width
            bw.Write(s * 2);                     // height (XOR + AND)
            bw.Write((short)1);                  // planes
            bw.Write((short)32);                 // bpp
            bw.Write(0);                         // no compression
            bw.Write(pixels.Length);             // image size
            bw.Write(0); bw.Write(0);            // ppm, colors
            bw.Write(pixels);
            bw.Write(new byte[s * andRow]);      // zero AND mask (alpha is in the pixels)
        }
        return ms.ToArray();
    }

    private void PlaceInitial()
    {
        if (double.IsNaN(_settings.WindowX) || double.IsNaN(_settings.WindowY) ||
            _settings.WindowX < 0 || _settings.WindowY < 0)
        {
            Location = DockPosition();
        }
        else
        {
            Location = new Point((int)_settings.WindowX, (int)_settings.WindowY);
        }
    }

    private Point DockPosition()
    {
        var work = Screen.PrimaryScreen?.WorkingArea ?? SystemInformation.WorkingArea;
        // WorkingArea already excludes the taskbar; keep a small gap so the card
        // never overlaps the systray.
        return new Point(work.Right - Width - 16, work.Bottom - Height - 16);
    }

    public void DockBottomRight()
    {
        Location = DockPosition();
        _settings.WindowX = Location.X;
        _settings.WindowY = Location.Y;
        _settings.Save();
    }

    // ---- updates (marshal to UI thread) ------------------------------
    public void ApplySample(LogSample? sample)
    {
        if (IsDisposed) return;
        try
        {
            BeginInvoke(() => Render(sample));
        }
        catch (InvalidOperationException) { /* handle disposed */ }
    }

    private void Render(LogSample? sample)
    {
        if (_settings.Paused)
        {
            _statusText = "PAUSED";
            _dotColor = Idle;
            _tray.Icon = _iconIdle;
            Present();
            return;
        }

        if (sample is null || (sample.Throughput is null && sample.PromptTps is null))
        {
            PushSpark(null);
            _sparkColor = Muted;
            _valueText = "—";
            _valueColor = Muted;
            _subText = "waiting for server activity…";
            _statusText = "IDLE";
            _statusColor = Muted;
            _dotColor = Idle;
            _tray.Icon = _iconIdle;
            _tray.Text = "LM Studio  —  waiting for activity";
            Present();
            return;
        }

        double? headline = sample.Throughput;
        double? instant = sample.Instant;
        int? tokens = sample.Tokens;
        double? prompt = sample.PromptTps;
        bool live = sample.Generating;

        string timeText = FormatTime(sample.Timestamp);

        if (live)
        {
            // Actively generating right now: headline is the real current throughput.
            double shown = instant ?? headline ?? 0;
            _valueText = $"{FormatTps(shown)} tok/s";
            _valueColor = ColorFor(shown);
            PushSpark(shown);
            _sparkColor = ColorFor(shown);
            _statusText = "LIVE";
            _statusColor = Muted;
            _dotColor = Live;
            _tray.Icon = _iconLive;
            _tray.Text = $"LM Studio  {FormatTps(shown)} tok/s";

            // Keep the subline to the right of the headline: drop the least
            // important parts first (prompt, then tokens, then avg) until it fits.
            var parts = new List<string>();
            if (instant is not null && headline is not null &&
                Math.Abs(instant.Value - headline.Value) > 0.05)
                parts.Add($"avg {FormatTps(headline)}");
            if (tokens is not null) parts.Add($"{tokens} tok");
            if (prompt is not null) parts.Add($"prompt {FormatTps(prompt)}");
            _subText = BuildSub(parts, timeText);
        }
        else
        {
            // Not generating right now: keep the last rate for reference.
            _sparkColor = Muted;
            _valueText = $"{FormatTps(headline)} tok/s";
            _valueColor = Muted;
            _statusText = "IDLE";
            _statusColor = Muted;
            _dotColor = Idle;
            _tray.Icon = _iconIdle;
            _tray.Text = $"LM Studio  idle  (last {FormatTps(headline)} tok/s)";
            _subText = timeText.Length > 0 ? $"last {timeText}" : string.Empty;
        }

        Present();
    }

    /// <summary>
    /// Builds the right-aligned subline, trimming parts until it fits to the
    /// right of the headline text (no overlap).
    /// </summary>
    private string BuildSub(List<string> parts, string timeText)
    {
        using var m = new Bitmap(1, 1);
        using var mg = Graphics.FromImage(m);
        float maxW = Width - 18 - mg.MeasureString(_valueText, ValueFont).Width - 24;
        string sub = timeText;
        while (parts.Count > 0)
        {
            string candidate = string.Join(" · ", parts) +
                (timeText.Length > 0 ? " · " + timeText : "");
            if (mg.MeasureString(candidate, SubFont).Width <= maxW)
            {
                sub = candidate;
                break;
            }
            parts.RemoveAt(parts.Count - 1);
        }
        return sub;
    }

    private static string FormatTps(double? v) =>
        v is null ? "—" : v.Value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);

    private static string FormatTime(DateTime? ts) =>
        ts is null ? string.Empty
        : ts.Value.ToLocalTime().ToString("HH:mm");

    private static Color ColorFor(double? v)
    {
        if (v is null) return Fg;
        if (v >= 100) return Live;
        if (v >= 40) return Color.FromArgb(242, 201, 79);
        return Color.FromArgb(224, 122, 95);
    }

    // ---- layered (per-pixel transparent) rendering ----------------------
    protected override void OnPaint(PaintEventArgs e)
    {
        // The visible surface is the layered bitmap pushed by Present();
        // nothing is painted by the normal window paint path.
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int ex = Win32.GetWindowLong(Handle, Win32.GWL_EXSTYLE);
        Win32.SetWindowLong(Handle, Win32.GWL_EXSTYLE, ex | Win32.WS_EX_LAYERED);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Present();
    }

    private Rectangle SparkRect => new(16, 38, Width - 32, 92);

    private void PushSpark(double? v)
    {
        _sparkData.Enqueue(v);
        while (_sparkData.Count > _sparkCap) _sparkData.Dequeue();
    }

    private void DrawCard(Graphics g)
    {
        // No background at all: 100% transparent — only the dot, graph and
        // text are composited over the desktop.
        using (var br = new SolidBrush(_dotColor))
            g.FillEllipse(br, 19, 15, 9, 9);
        DrawText(g, _statusText, StatusFont, 36, 9, _statusColor);

        Sparkline.Draw(g, SparkRect, _sparkData, _sparkColor);

        DrawText(g, _valueText, ValueFont, 18, 134, _valueColor);
        if (_subText.Length > 0)
        {
            float subW = g.MeasureString(_subText, SubFont).Width;
            DrawText(g, _subText, SubFont, Width - 18 - subW, 138, Muted);
        }
    }

    private static void DrawText(Graphics g, string text, Font font, float x, float y, Color color)
    {
        using var brush = new SolidBrush(color);
        g.DrawString(text, font, brush, x, y);
    }

    /// <summary>
    /// Renders the card to a 32-bit ARGB bitmap at the window's physical size
    /// and pushes it with UpdateLayeredWindow (per-pixel alpha).
    /// </summary>
    public void Present()
    {
        if (!IsHandleCreated || IsDisposed) return;

        // WinForms rewrites the extended style during Show(), which would drop
        // WS_EX_LAYERED; re-assert it before every UpdateLayeredWindow call.
        int ex = Win32.GetWindowLong(Handle, Win32.GWL_EXSTYLE);
        if ((ex & Win32.WS_EX_LAYERED) == 0)
            Win32.SetWindowLong(Handle, Win32.GWL_EXSTYLE, ex | Win32.WS_EX_LAYERED);

        Win32.RECT rc;
        if (!Win32.GetWindowRect(Handle, out rc)) return;
        int pw = rc.Right - rc.Left;
        int ph = rc.Bottom - rc.Top;
        if (pw < 8 || ph < 8) return;
        float sx = pw / (float)Width;
        float sy = ph / (float)Height;

        // 1) Render top-down in logical coordinates, scaled to physical pixels.
        using var img = new Bitmap(pw, ph, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(img))
        {
            g.Clear(Color.Transparent);
            g.ScaleTransform(sx, sy);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            DrawCard(g);
        }

        // 2) Bottom-up 32-bpp DIB section (what UpdateLayeredWindow expects).
        var dib = new Win32.BITMAPINFO
        {
            bmiHeader = new Win32.BITMAPINFOHEADER
            {
                biSize = 40,
                biWidth = pw,
                biHeight = ph,       // positive = bottom-up
                biPlanes = 1,
                biBitCount = 32,
            },
        };
        IntPtr bits;
        IntPtr hBmp = Win32.CreateDIBSection(IntPtr.Zero, ref dib, 0, out bits, IntPtr.Zero, 0);
        if (hBmp == IntPtr.Zero) return;
        try
        {
            int rowBytes = pw * 4;
            var row = new byte[rowBytes];
            var data = img.LockBits(new Rectangle(0, 0, pw, ph),
                ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                for (int y = 0; y < ph; y++)
                {
                    var s = new IntPtr(data.Scan0.ToInt64() + (long)(ph - 1 - y) * data.Stride);
                    var d = new IntPtr(bits.ToInt64() + (long)y * rowBytes);
                    Marshal.Copy(s, row, 0, rowBytes);
                    Marshal.Copy(row, 0, d, rowBytes);
                }
            }
            finally
            {
                img.UnlockBits(data);
            }

            IntPtr hdcMem = Win32.CreateCompatibleDC(IntPtr.Zero);
            IntPtr old = hdcMem == IntPtr.Zero ? IntPtr.Zero : Win32.SelectObject(hdcMem, hBmp);
            try
            {
                var blend = new Win32.BLENDFUNCTION
                {
                    BlendOp = Win32.AC_SRC_OVER,
                    SourceConstantAlpha = 255,
                    AlphaFormat = Win32.AC_SRC_ALPHA,
                };
                var dst = new Win32.POINT { X = rc.Left, Y = rc.Top };
                var size = new Win32.SIZE { cx = pw, cy = ph };
                var srcPt = new Win32.POINT();
                Win32.UpdateLayeredWindow(Handle, IntPtr.Zero, ref dst, ref size,
                    hdcMem, ref srcPt, 0, ref blend, Win32.ULW_ALPHA);
            }
            finally
            {
                if (hdcMem != IntPtr.Zero)
                {
                    if (old != IntPtr.Zero) Win32.SelectObject(hdcMem, old);
                    Win32.DeleteDC(hdcMem);
                }
            }
        }
        finally
        {
            Win32.DeleteObject(hBmp);
        }
    }

    /// <summary>Win32 interop for layered windows.</summary>
    private static class Win32
    {
        public const int GWL_EXSTYLE = -20;
        public const int WS_EX_LAYERED = 0x00080000;
        public const uint ULW_ALPHA = 0x02;
        public const byte AC_SRC_OVER = 0x00;
        public const byte AC_SRC_ALPHA = 0x01;

        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] public struct SIZE { public int cx, cy; }
        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] public struct BLENDFUNCTION
        { public byte BlendOp; public byte BlendFlags; public byte SourceConstantAlpha; public byte AlphaFormat; }
        [StructLayout(LayoutKind.Sequential)] public struct BITMAPINFOHEADER
        {
            public int biSize, biWidth, biHeight;
            public short biPlanes, biBitCount;
            public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter;
            public uint biClrUsed;
            public int biClrImportant;
        }
        [StructLayout(LayoutKind.Sequential)] public struct BITMAPINFO
        {
            public BITMAPINFOHEADER bmiHeader;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 1)] public byte[] bmiColors;
        }

        [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hwnd, int index);
        [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT lpRect);
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcScreen,
            ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc,
            uint crKey, ref BLENDFUNCTION pblend, uint dwFlags);

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO pbmi, uint usage,
            out IntPtr ppvBits, IntPtr hSection, uint offset);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr h);
        [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr hdc);
    }

    // ---- menu actions -------------------------------------------------
    public void SetVisible(bool show)
    {
        if (show)
        {
            Show();
            WindowState = FormWindowState.Normal;
            TopMost = true;
            Activate();
            _toggleItem.Text = "Hide widget";
        }
        else
        {
            Hide();
            _toggleItem.Text = "Show widget";
        }
    }

    private void TogglePause()
    {
        _settings.Paused = !_settings.Paused;
        _pauseItem.Text = _settings.Paused ? "Resume updates" : "Pause updates";
        _settings.Save();
        Render(null);
    }

    private void OpenLogsFolder()
    {
        try
        {
            string dir = string.IsNullOrWhiteSpace(_settings.Root)
                ? AppContext.BaseDirectory
                : _settings.Root;
            if (Directory.Exists(dir))
                Process.Start("explorer.exe", "\"" + dir + "\"");
        }
        catch { /* ignore */ }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (_tray is not null)
            {
                _tray.Visible = false;
                _tray.Dispose();
            }
            _menu?.Dispose();
        }
        base.Dispose(disposing);
    }
}
