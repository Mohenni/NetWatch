// NetWatch - colours, fonts, number formatting and small drawing helpers shared by the app and its setup.

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace NetWatch
{
    internal static class Theme
    {
        /// <summary>Screen scaling factor (1.0 at 100 %, 1.25 at 125 % ...).</summary>
        public static float Scale = 1f;

        public static readonly Color Window = Color.FromArgb(18, 20, 26);
        public static readonly Color Panel = Color.FromArgb(24, 27, 34);
        public static readonly Color RowHover = Color.FromArgb(31, 35, 45);
        public static readonly Color RowSelected = Color.FromArgb(38, 44, 60);
        public static readonly Color Line = Color.FromArgb(40, 44, 56);
        public static readonly Color Text = Color.FromArgb(232, 235, 242);
        public static readonly Color TextDim = Color.FromArgb(152, 159, 176);
        public static readonly Color TextFaint = Color.FromArgb(104, 111, 129);
        public static readonly Color Up = Color.FromArgb(255, 163, 72);       // sending
        public static readonly Color Down = Color.FromArgb(86, 168, 255);     // receiving
        public static readonly Color Danger = Color.FromArgb(255, 107, 107);
        public static readonly Color Good = Color.FromArgb(70, 211, 143);
        public static readonly Color Button = Color.FromArgb(44, 49, 63);
        public static readonly Color ButtonHover = Color.FromArgb(58, 64, 82);
        public static readonly Color OnAccent = Color.FromArgb(20, 20, 24);   // text on a coloured button

        public static Font Body, BodyBold, Small, Caps, Big, Title, TabFont;

        const TextFormatFlags BaseFlags = TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding |
                                          TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter;

        static bool _ready;

        public static void Init()
        {
            if (_ready) return;
            _ready = true;
            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
                Scale = g.DpiX / 96f;
            Body = new Font("Segoe UI", 9.5f);
            BodyBold = new Font("Segoe UI Semibold", 9.75f);
            Small = new Font("Segoe UI", 8.5f);
            Caps = new Font("Segoe UI Semibold", 7.5f);
            Big = new Font("Segoe UI Semibold", 17f);
            Title = new Font("Segoe UI Semibold", 12f);
            TabFont = new Font("Segoe UI Semibold", 10.5f);
        }

        public static int S(int v)
        {
            return (int)Math.Round(v * Scale);
        }

        // ---------------------------------------------------------------- text

        public static void Left(Graphics g, string s, Font f, Rectangle r, Color c)
        {
            Draw(g, s, f, r, c, TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        }

        public static void Right(Graphics g, string s, Font f, Rectangle r, Color c)
        {
            Draw(g, s, f, r, c, TextFormatFlags.Right);
        }

        public static void Center(Graphics g, string s, Font f, Rectangle r, Color c)
        {
            Draw(g, s, f, r, c, TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis);
        }

        static void Draw(Graphics g, string s, Font f, Rectangle r, Color c, TextFormatFlags extra)
        {
            if (string.IsNullOrEmpty(s) || r.Width <= 0 || r.Height <= 0) return;
            TextRenderer.DrawText(g, s, f, r, c, BaseFlags | extra);
        }

        /// <summary>Draws text that may wrap over several lines, starting at the top of the rectangle.</summary>
        public static void Wrapped(Graphics g, string s, Font f, Rectangle r, Color c)
        {
            if (string.IsNullOrEmpty(s)) return;
            TextRenderer.DrawText(g, s, f, r, c,
                TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.WordBreak | TextFormatFlags.Left | TextFormatFlags.Top);
        }

        public static int WrappedHeight(string s, Font f, int width)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            Size size = TextRenderer.MeasureText(s, f, new Size(width, 100000),
                TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.WordBreak | TextFormatFlags.Left | TextFormatFlags.Top);
            return size.Height;
        }

        public static int Measure(string s, Font f)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            return TextRenderer.MeasureText(s, f, new Size(100000, 100000),
                TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;
        }

        // ------------------------------------------------------------- numbers

        public static string Bytes(long n)
        {
            if (n < 0) n = 0;
            if (n < 1024) return n.ToString(CultureInfo.InvariantCulture) + " B";
            double v = n / 1024.0;
            string unit = "KB";
            if (v >= 1024) { v /= 1024; unit = "MB"; }
            if (v >= 1024) { v /= 1024; unit = "GB"; }
            if (v >= 1024) { v /= 1024; unit = "TB"; }
            string format = v >= 100 ? "0" : (v >= 10 ? "0.0" : "0.00");
            return v.ToString(format, CultureInfo.InvariantCulture) + " " + unit;
        }

        /// <summary>Speed as text. Anything below one byte per second shows as a dash (idle).</summary>
        public static string Rate(double bytesPerSecond)
        {
            if (bytesPerSecond < 1) return "–";
            return Bytes((long)Math.Round(bytesPerSecond)) + "/s";
        }

        /// <summary>How long ago something happened. Both values are UTC FILETIME ticks.</summary>
        public static string Ago(long whenFileTime, long nowFileTime)
        {
            if (whenFileTime <= 0) return "";
            long seconds = (nowFileTime - whenFileTime) / 10000000L;
            if (seconds < 5) return "now";
            if (seconds < 60) return seconds.ToString(CultureInfo.InvariantCulture) + " s ago";
            if (seconds < 3600) return (seconds / 60).ToString(CultureInfo.InvariantCulture) + " min ago";
            if (seconds < 86400) return (seconds / 3600).ToString(CultureInfo.InvariantCulture) + " h ago";
            return DateTime.FromFileTimeUtc(whenFileTime).ToLocalTime().ToString("d MMM HH:mm", CultureInfo.CurrentCulture);
        }

        public static string Duration(TimeSpan t)
        {
            if (t.TotalSeconds < 60) return ((int)t.TotalSeconds).ToString(CultureInfo.InvariantCulture) + " s";
            if (t.TotalMinutes < 60) return ((int)t.TotalMinutes).ToString(CultureInfo.InvariantCulture) + " min";
            return ((int)t.TotalHours).ToString(CultureInfo.InvariantCulture) + " h " + t.Minutes.ToString(CultureInfo.InvariantCulture) + " min";
        }

        // -------------------------------------------------------------- shapes

        public static GraphicsPath RoundRect(Rectangle r, int radius)
        {
            GraphicsPath p = new GraphicsPath();
            int d = radius * 2;
            if (d > r.Width) d = r.Width;
            if (d > r.Height) d = r.Height;
            if (d <= 0)
            {
                p.AddRectangle(r);
                return p;
            }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static void FillRound(Graphics g, Rectangle r, int radius, Color c)
        {
            SmoothingMode old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath p = RoundRect(r, radius))
            using (SolidBrush b = new SolidBrush(c))
                g.FillPath(b, p);
            g.SmoothingMode = old;
        }

        public static void StrokeRound(Graphics g, Rectangle r, int radius, Color c, float width)
        {
            SmoothingMode old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle inner = new Rectangle(r.X, r.Y, r.Width - 1, r.Height - 1);
            using (GraphicsPath p = RoundRect(inner, radius))
            using (Pen pen = new Pen(c, width))
                g.DrawPath(pen, p);
            g.SmoothingMode = old;
        }

        /// <summary>A vertical arrow: up for sending, down for receiving.</summary>
        public static void Arrow(Graphics g, Rectangle r, bool up, Color c)
        {
            SmoothingMode old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (Pen p = new Pen(c, Math.Max(1.6f, r.Width / 7.5f)))
            {
                p.StartCap = LineCap.Round;
                p.EndCap = LineCap.Round;
                p.LineJoin = LineJoin.Round;
                float cx = r.X + r.Width / 2f;
                float top = r.Y + r.Height * 0.14f;
                float bottom = r.Bottom - r.Height * 0.14f;
                float w = r.Width * 0.30f;
                g.DrawLine(p, cx, top, cx, bottom);
                if (up)
                    g.DrawLines(p, new PointF[] { new PointF(cx - w, top + w), new PointF(cx, top), new PointF(cx + w, top + w) });
                else
                    g.DrawLines(p, new PointF[] { new PointF(cx - w, bottom - w), new PointF(cx, bottom), new PointF(cx + w, bottom - w) });
            }
            g.SmoothingMode = old;
        }

        /// <summary>A circle with a diagonal bar: "blocked".</summary>
        public static void BlockSign(Graphics g, Rectangle r, Color c)
        {
            SmoothingMode old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (Pen p = new Pen(c, Math.Max(1.6f, r.Width / 8f)))
            {
                float inset = r.Width * 0.12f;
                RectangleF circle = new RectangleF(r.X + inset, r.Y + inset, r.Width - 2 * inset, r.Height - 2 * inset);
                g.DrawEllipse(p, circle);
                float k = circle.Width * 0.5f * 0.7071f;
                float cx = circle.X + circle.Width / 2f, cy = circle.Y + circle.Height / 2f;
                g.DrawLine(p, cx - k, cy - k, cx + k, cy + k);
            }
            g.SmoothingMode = old;
        }

        public static void Chevron(Graphics g, Rectangle r, Color c)
        {
            SmoothingMode old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (Pen p = new Pen(c, Math.Max(1.4f, Scale * 1.4f)))
            {
                p.StartCap = LineCap.Round;
                p.EndCap = LineCap.Round;
                p.LineJoin = LineJoin.Round;
                float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f, w = r.Width * 0.28f;
                g.DrawLines(p, new PointF[] { new PointF(cx - w, cy - w / 2), new PointF(cx, cy + w / 2), new PointF(cx + w, cy - w / 2) });
            }
            g.SmoothingMode = old;
        }

        /// <summary>
        /// A small filled line chart. values[0] is the oldest sample; max is the value that reaches the top.
        /// </summary>
        public static void AreaChart(Graphics g, Rectangle r, float[] values, float max, Color c, float lineWidth)
        {
            int count = values.Length;
            if (count < 2 || r.Width < 4 || r.Height < 4) return;
            if (max <= 0) max = 1;
            PointF[] line = new PointF[count];
            float step = (r.Width - 1) / (float)(count - 1);
            float usable = r.Height - 2;
            bool any = false;
            for (int i = 0; i < count; i++)
            {
                float v = values[i] / max;
                if (v > 1) v = 1;
                if (v < 0) v = 0;
                if (v > 0)
                {
                    any = true;
                    // keep even tiny traffic visible as a one-pixel bump
                    float minVisible = 1.5f / usable;
                    if (v < minVisible) v = minVisible;
                }
                line[i] = new PointF(r.X + i * step, r.Bottom - 1 - v * usable);
            }
            if (!any) return;

            SmoothingMode old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            PointF[] area = new PointF[count + 2];
            area[0] = new PointF(r.X, r.Bottom - 1);
            Array.Copy(line, 0, area, 1, count);
            area[count + 1] = new PointF(line[count - 1].X, r.Bottom - 1);
            Rectangle gradientRect = new Rectangle(r.X, r.Y - 1, r.Width, r.Height + 2);
            using (LinearGradientBrush fill = new LinearGradientBrush(gradientRect, Color.FromArgb(120, c), Color.FromArgb(14, c), 90f))
                g.FillPolygon(fill, area);
            using (Pen pen = new Pen(c, lineWidth))
            {
                pen.LineJoin = LineJoin.Round;
                g.DrawLines(pen, line);
            }
            g.SmoothingMode = old;
        }

        // ------------------------------------------------------------- windows

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        [StructLayout(LayoutKind.Sequential)]
        struct WindowRect
        {
            public int Left, Top, Right, Bottom;
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool GetWindowRect(IntPtr hwnd, out WindowRect rect);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

        /// <summary>Saves a picture of one of our own windows as a PNG file (used by the self-tests and the diagnostics report).</summary>
        public static void SaveWindowPicture(Control window, string file)
        {
            WindowRect rc;
            if (!GetWindowRect(window.Handle, out rc)) return;
            int width = rc.Right - rc.Left, height = rc.Bottom - rc.Top;
            if (width <= 0 || height <= 0) return;
            using (Bitmap bitmap = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(bitmap))
                {
                    IntPtr hdc = g.GetHdc();
                    try { PrintWindow(window.Handle, hdc, 2 /* PW_RENDERFULLCONTENT */); }
                    finally { g.ReleaseHdc(hdc); }
                }
                bitmap.Save(file, System.Drawing.Imaging.ImageFormat.Png);
            }
        }

        /// <summary>Asks Windows to draw the window's title bar dark (Windows 10 2004 and later).</summary>
        public static void DarkTitleBar(IntPtr hwnd)
        {
            try
            {
                int on = 1;
                if (DwmSetWindowAttribute(hwnd, 20, ref on, 4) != 0)
                    DwmSetWindowAttribute(hwnd, 19, ref on, 4);
            }
            catch
            {
            }
        }
    }

    /// <summary>Dark colours for right-click / drop-down menus.</summary>
    internal sealed class DarkMenuColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground { get { return Theme.Panel; } }
        public override Color MenuBorder { get { return Theme.Line; } }
        public override Color MenuItemBorder { get { return Theme.RowSelected; } }
        public override Color MenuItemSelected { get { return Theme.RowSelected; } }
        public override Color MenuItemSelectedGradientBegin { get { return Theme.RowSelected; } }
        public override Color MenuItemSelectedGradientEnd { get { return Theme.RowSelected; } }
        public override Color ImageMarginGradientBegin { get { return Theme.Panel; } }
        public override Color ImageMarginGradientMiddle { get { return Theme.Panel; } }
        public override Color ImageMarginGradientEnd { get { return Theme.Panel; } }
        public override Color SeparatorDark { get { return Theme.Line; } }
        public override Color SeparatorLight { get { return Theme.Line; } }
        public override Color CheckBackground { get { return Theme.Panel; } }
        public override Color CheckSelectedBackground { get { return Theme.RowSelected; } }
        public override Color CheckPressedBackground { get { return Theme.RowSelected; } }
    }

    internal sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
    {
        public DarkMenuRenderer() : base(new DarkMenuColors())
        {
            RoundedEdges = false;
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Theme.Text : Theme.TextFaint;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            Rectangle r = e.ImageRectangle;
            Graphics g = e.Graphics;
            SmoothingMode old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (Pen p = new Pen(Theme.Good, Math.Max(1.8f, Theme.Scale * 1.8f)))
            {
                p.StartCap = LineCap.Round;
                p.EndCap = LineCap.Round;
                p.LineJoin = LineJoin.Round;
                float x = r.X + r.Width * 0.2f, y = r.Y + r.Height * 0.52f;
                g.DrawLines(p, new PointF[] {
                    new PointF(x, y),
                    new PointF(r.X + r.Width * 0.42f, r.Y + r.Height * 0.74f),
                    new PointF(r.X + r.Width * 0.82f, r.Y + r.Height * 0.28f) });
            }
            g.SmoothingMode = old;
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = Theme.TextDim;
            base.OnRenderArrow(e);
        }
    }
}
