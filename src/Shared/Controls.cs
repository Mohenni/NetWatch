// NetWatch - small hand-drawn controls (buttons, tick boxes, message windows) shared by the app and its setup.

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace NetWatch
{
    internal enum ButtonKind { Normal, Primary, Danger, Tab, Ghost }

    internal enum Glyph { None, Up, Down, Block, Chevron }

    internal sealed class FlatButton : Control
    {
        ButtonKind _kind = ButtonKind.Normal;
        Glyph _glyph = Glyph.None;
        Color _accent = Theme.Up;
        string _subText = "";
        int _subWidth;
        bool _selected, _hover, _down;

        public FlatButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.Selectable | ControlStyles.StandardClick, true);
            SetStyle(ControlStyles.StandardDoubleClick, false);
            BackColor = Theme.Window;
            Cursor = Cursors.Hand;
            TabStop = true;
        }

        public ButtonKind Kind { get { return _kind; } set { _kind = value; Invalidate(); } }
        public Glyph Glyph { get { return _glyph; } set { _glyph = value; Invalidate(); } }
        public Color Accent { get { return _accent; } set { _accent = value; Invalidate(); } }

        /// <summary>Second, smaller text shown after the title of a tab (for example the current speed).</summary>
        public string SubText
        {
            get { return _subText; }
            set
            {
                string v = value ?? "";
                if (v == _subText) return;
                _subText = v;
                Invalidate();
            }
        }

        /// <summary>Width reserved for SubText (unscaled pixels), so tabs do not move when the number changes.</summary>
        public int SubWidth { get { return _subWidth; } set { _subWidth = value; } }

        public bool Selected
        {
            get { return _selected; }
            set { if (_selected != value) { _selected = value; Invalidate(); } }
        }

        /// <summary>Self-test hook: behaves exactly like a mouse click on the button.</summary>
        internal void PerformClickForTests()
        {
            OnClick(EventArgs.Empty);
        }

        public int PreferredWidth()
        {
            if (_kind == ButtonKind.Tab)
            {
                int w = Theme.S(14);
                if (_glyph != Glyph.None) w += Theme.S(26);
                w += Theme.Measure(Text, Theme.TabFont);
                if (_subWidth > 0) w += Theme.S(10) + Theme.S(_subWidth);
                return w + Theme.S(14);
            }
            int width = Theme.S(18) * 2 + Theme.Measure(Text, Theme.BodyBold);
            if (_glyph == Glyph.Chevron) width += Theme.S(16);
            return width;
        }

        protected override void OnTextChanged(EventArgs e)
        {
            base.OnTextChanged(e);
            Invalidate();
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) { _down = true; Invalidate(); }
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            _down = false;
            Invalidate();
            base.OnMouseUp(e);
        }

        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter)
            {
                e.Handled = true;
                OnClick(EventArgs.Empty);
                return;
            }
            base.OnKeyDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Rectangle r = ClientRectangle;
            g.Clear(BackColor);
            if (_kind == ButtonKind.Tab) PaintTab(g, r);
            else PaintButton(g, r);
        }

        void DrawGlyph(Graphics g, Rectangle box, Color c)
        {
            switch (_glyph)
            {
                case Glyph.Up: Theme.Arrow(g, box, true, c); break;
                case Glyph.Down: Theme.Arrow(g, box, false, c); break;
                case Glyph.Block: Theme.BlockSign(g, box, c); break;
                case Glyph.Chevron: Theme.Chevron(g, box, c); break;
            }
        }

        void PaintTab(Graphics g, Rectangle r)
        {
            int underline = Theme.S(3);
            int bodyHeight = r.Height - underline - Theme.S(3);
            if (_hover && !_selected)
                Theme.FillRound(g, new Rectangle(r.X, r.Y + Theme.S(6), r.Width, bodyHeight - Theme.S(8)), Theme.S(6), Theme.RowHover);

            int x = Theme.S(14);
            if (_glyph != Glyph.None)
            {
                int size = Theme.S(18);
                DrawGlyph(g, new Rectangle(x, (bodyHeight - size) / 2 + Theme.S(1), size, size), _selected ? _accent : Theme.TextDim);
                x += Theme.S(26);
            }
            int textWidth = Theme.Measure(Text, Theme.TabFont);
            Theme.Left(g, Text, Theme.TabFont, new Rectangle(x, 0, textWidth + 4, bodyHeight), _selected ? Theme.Text : Theme.TextDim);
            x += textWidth + Theme.S(10);
            if (_subWidth > 0 && _subText.Length > 0)
                Theme.Left(g, _subText, Theme.Small, new Rectangle(x, Theme.S(1), r.Width - x - Theme.S(6), bodyHeight), _selected ? _accent : Theme.TextFaint);

            if (_selected)
                Theme.FillRound(g, new Rectangle(Theme.S(10), r.Height - underline, r.Width - Theme.S(20), underline), underline / 2, _accent);
            else if (Focused && ShowFocusCues)
                Theme.FillRound(g, new Rectangle(Theme.S(10), r.Height - underline, r.Width - Theme.S(20), underline), underline / 2, Theme.TextFaint);
        }

        void PaintButton(Graphics g, Rectangle r)
        {
            Color fill, text;
            Font font = Theme.Body;
            bool border = false;
            switch (_kind)
            {
                case ButtonKind.Primary:
                    fill = _hover ? Mix(_accent, Color.White, 0.14f) : _accent;
                    text = Theme.OnAccent;
                    font = Theme.BodyBold;
                    break;
                case ButtonKind.Danger:
                    fill = _hover ? Mix(Theme.Danger, Color.White, 0.14f) : Theme.Danger;
                    text = Theme.OnAccent;
                    font = Theme.BodyBold;
                    break;
                case ButtonKind.Ghost:
                    fill = _hover ? Theme.RowHover : BackColor;
                    text = _hover ? Theme.Text : Theme.TextDim;
                    border = true;
                    break;
                default:
                    fill = _hover ? Theme.ButtonHover : Theme.Button;
                    text = Theme.Text;
                    break;
            }
            if (_down) fill = Mix(fill, Color.Black, 0.18f);
            if (!Enabled)
            {
                fill = Theme.Button;
                text = Theme.TextFaint;
            }
            int radius = Theme.S(6);
            Theme.FillRound(g, r, radius, fill);
            if (border) Theme.StrokeRound(g, r, radius, Theme.Line, 1f);
            if (Focused && ShowFocusCues) Theme.StrokeRound(g, r, radius, Theme.Text, 1f);

            Rectangle textRect = r;
            if (_glyph == Glyph.Chevron)
            {
                int size = Theme.S(14);
                DrawGlyph(g, new Rectangle(r.Right - size - Theme.S(12), r.Y + (r.Height - size) / 2 + 1, size, size), text);
                textRect = new Rectangle(r.X + Theme.S(4), r.Y, r.Width - size - Theme.S(18), r.Height);
            }
            Theme.Center(g, Text, font, textRect, text);
        }

        internal static Color Mix(Color a, Color b, float t)
        {
            return Color.FromArgb(
                (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t),
                (int)(a.B + (b.B - a.B) * t));
        }
    }

    /// <summary>A panel that paints without flicker.</summary>
    internal sealed class BufferedPanel : Panel
    {
        public BufferedPanel()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Window;
        }
    }

    /// <summary>A tick box with a label.</summary>
    internal sealed class CheckRow : Control
    {
        bool _checked, _hover;

        public event EventHandler CheckedChanged;

        public CheckRow()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.Selectable | ControlStyles.StandardClick, true);
            SetStyle(ControlStyles.StandardDoubleClick, false);
            BackColor = Theme.Panel;
            Cursor = Cursors.Hand;
            TabStop = true;
        }

        public bool Checked
        {
            get { return _checked; }
            set
            {
                if (_checked == value) return;
                _checked = value;
                Invalidate();
                if (CheckedChanged != null) CheckedChanged(this, EventArgs.Empty);
            }
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

        protected override void OnClick(EventArgs e)
        {
            Checked = !Checked;
            base.OnClick(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space)
            {
                e.Handled = true;
                Checked = !Checked;
                return;
            }
            base.OnKeyDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            int size = Theme.S(18);
            Rectangle box = new Rectangle(1, (Height - size) / 2, size, size);
            if (_checked)
            {
                Theme.FillRound(g, box, Theme.S(4), Theme.Down);
                SmoothingMode old = g.SmoothingMode;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (Pen p = new Pen(Theme.OnAccent, Math.Max(1.8f, Theme.Scale * 1.8f)))
                {
                    p.StartCap = LineCap.Round;
                    p.EndCap = LineCap.Round;
                    p.LineJoin = LineJoin.Round;
                    g.DrawLines(p, new PointF[] {
                        new PointF(box.X + size * 0.24f, box.Y + size * 0.52f),
                        new PointF(box.X + size * 0.43f, box.Y + size * 0.70f),
                        new PointF(box.X + size * 0.77f, box.Y + size * 0.32f) });
                }
                g.SmoothingMode = old;
            }
            else
            {
                Theme.StrokeRound(g, box, Theme.S(4), _hover || Focused ? Theme.TextDim : Theme.TextFaint, 1.4f);
            }
            Theme.Left(g, Text, Theme.Body, new Rectangle(size + Theme.S(10), 0, Width - size - Theme.S(10), Height), Theme.Text);
        }
    }

    /// <summary>A dark message window with one or two buttons.</summary>
    internal sealed class MessageDialog : Form
    {
        readonly string _heading, _body;
        readonly FlatButton _ok, _cancel;
        readonly bool _focusCancel;
        Rectangle _headingRect, _bodyRect;

        MessageDialog(string caption, string heading, string body, string okText, string cancelText, ButtonKind okKind, Color accent)
        {
            Theme.Init();
            _heading = heading ?? "";
            _body = body ?? "";
            Text = caption;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowIcon = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Theme.Panel;
            KeyPreview = true;
            DoubleBuffered = true;

            int pad = Theme.S(24);
            int width = Theme.S(480);
            int textWidth = width - 2 * pad;
            int headingHeight = Theme.WrappedHeight(_heading, Theme.Title, textWidth);
            int bodyHeight = Theme.WrappedHeight(_body, Theme.Body, textWidth);
            _headingRect = new Rectangle(pad, pad, textWidth, headingHeight);
            _bodyRect = new Rectangle(pad, _headingRect.Bottom + Theme.S(10), textWidth, bodyHeight);
            int buttonTop = _bodyRect.Bottom + Theme.S(26);
            int buttonHeight = Theme.S(34);

            _ok = new FlatButton();
            _ok.Kind = okKind;
            _ok.Accent = accent;
            _ok.Text = okText;
            _ok.BackColor = Theme.Panel;
            int okWidth = Math.Max(Theme.S(100), _ok.PreferredWidth());
            _ok.Bounds = new Rectangle(width - pad - okWidth, buttonTop, okWidth, buttonHeight);
            _ok.Click += delegate { DialogResult = DialogResult.OK; Close(); };
            Controls.Add(_ok);

            if (cancelText != null)
            {
                _cancel = new FlatButton();
                _cancel.Kind = ButtonKind.Normal;
                _cancel.Text = cancelText;
                _cancel.BackColor = Theme.Panel;
                int cancelWidth = Math.Max(Theme.S(100), _cancel.PreferredWidth());
                _cancel.Bounds = new Rectangle(_ok.Left - Theme.S(10) - cancelWidth, buttonTop, cancelWidth, buttonHeight);
                _cancel.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
                Controls.Add(_cancel);
                _cancel.TabIndex = 0;
                _ok.TabIndex = 1;
            }
            _focusCancel = _cancel != null && okKind == ButtonKind.Danger;
            ClientSize = new Size(width, buttonTop + buttonHeight + pad);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.DarkTitleBar(Handle);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (_focusCancel) _cancel.Focus(); else _ok.Focus();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape)
            {
                e.Handled = true;
                DialogResult = DialogResult.Cancel;
                Close();
                return;
            }
            base.OnKeyDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            Theme.Wrapped(g, _heading, Theme.Title, _headingRect, Theme.Text);
            Theme.Wrapped(g, _body, Theme.Body, _bodyRect, Color.FromArgb(200, 205, 217));
        }

        /// <summary>Asks a yes/no question. Returns true when the first (ok) button was chosen.</summary>
        public static bool Confirm(IWin32Window owner, string caption, string heading, string body,
                                   string okText, string cancelText, ButtonKind okKind, Color accent)
        {
            using (MessageDialog d = new MessageDialog(caption, heading, body, okText, cancelText, okKind, accent))
            {
                if (owner == null)
                {
                    d.StartPosition = FormStartPosition.CenterScreen;
                    d.ShowInTaskbar = true;
                    d.TopMost = true;
                }
                return d.ShowDialog(owner) == DialogResult.OK;
            }
        }

        public static void Info(IWin32Window owner, string caption, string heading, string body)
        {
            Confirm(owner, caption, heading, body, "OK", null, ButtonKind.Normal, Theme.Down);
        }

        /// <summary>Creates the window without showing it (used by the visual self-test only).</summary>
        internal static Form CreateForTest(string caption, string heading, string body, string okText, string cancelText, ButtonKind okKind)
        {
            return new MessageDialog(caption, heading, body, okText, cancelText, okKind, Theme.Danger);
        }
    }
}
