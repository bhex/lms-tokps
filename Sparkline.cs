using System.Drawing.Drawing2D;

namespace LmsTokps;

/// <summary>
/// Stateless sparkline renderer. Drawn directly on the host form's Graphics so it
/// is captured by both on-screen paint and off-screen (PrintWindow) rendering.
/// The most recent value sits on the right; nulls are gaps (idle server).
/// </summary>
public static class Sparkline
{
    /// <summary>
    /// Smooths the points into a Catmull-Rom spline (as cubic Béziers) so the
    /// line flows instead of stepping between per-second samples.
    /// </summary>
    private static GraphicsPath CatmullRomPath(List<PointF> pts)
    {
        var path = new GraphicsPath();
        path.StartFigure();
        for (int i = 0; i < pts.Count - 1; i++)
        {
            PointF p0 = pts[Math.Max(0, i - 1)];
            PointF p1 = pts[i];
            PointF p2 = pts[i + 1];
            PointF p3 = pts[Math.Min(pts.Count - 1, i + 2)];
            PointF c1 = new(p1.X + (p2.X - p0.X) / 6f, p1.Y + (p2.Y - p0.Y) / 6f);
            PointF c2 = new(p2.X - (p3.X - p1.X) / 6f, p2.Y - (p3.Y - p1.Y) / 6f);
            path.AddBezier(p1, c1, c2, p2);
        }
        return path;
    }

    public static void Draw(Graphics g, Rectangle r, IEnumerable<double?> data, Color color)
    {
        int w = r.Width, h = r.Height;
        if (w < 6 || h < 6) return;

        var oldState = g.Save();
        g.TranslateTransform(r.X, r.Y);
        g.SetClip(new RectangleF(0, 0, w, h));
        g.SmoothingMode = SmoothingMode.AntiAlias;
        try
        {
            using (var pen = new Pen(Color.FromArgb(45, 255, 255, 255)))
                g.DrawLine(pen, 0, h - 1, w - 1, h - 1);

            List<double?> list = data.ToList();
            int n = list.Count;
            if (n < 2) return;

            double[] vals = list.Where(x => x is not null).Select(x => x!.Value).ToArray();
            if (vals.Length < 2) return;

            double top = (vals.Max() <= 0 ? 100 : vals.Max()) * 1.15;
            double step = (w - 2) / (n - 1);

            var pts = new List<PointF>();
            int idx = 0;
            foreach (var v in list)
            {
                int x = 1 + (int)Math.Round(step * idx);
                idx++;
                if (v is null) continue;
                float y = (float)((h - 2) - (h - 5) * (v.Value / top));
                y = MathF.Max(2f, MathF.Min(h - 2, y));
                pts.Add(new PointF(x, y));
            }
            if (pts.Count < 2) return;

            var curve = CatmullRomPath(pts);
            using (curve)
            {
                using (var pen = new Pen(color, 2.2f) { LineJoin = LineJoin.Round })
                    g.DrawPath(pen, curve);

                var head = pts[^1];
                using (var b = new SolidBrush(color))
                    g.FillEllipse(b, head.X - 2.5f, head.Y - 2.5f, 5f, 5f);
                using (var b = new SolidBrush(Color.FromArgb(150, 23, 26, 32)))
                    g.FillEllipse(b, head.X - 1.2f, head.Y - 1.2f, 2.4f, 2.4f);
            }
        }
        finally
        {
            g.Restore(oldState);
        }
    }
}
