// NetWatch - the "Donate" window and the project's public details.

using System;
using System.Drawing;
using System.Windows.Forms;

namespace NetWatch
{
    internal static class ProjectInfo
    {
        public const string Version = "1.0";
        public const string Website = "https://github.com/Mohenni/NetWatch";

        /// <summary>The author's Bitcoin address for voluntary donations (Bitcoin network only).</summary>
        public const string BitcoinAddress = "bc1qe53lfh6535w4h3py7pwfhcml32lpq9rty55l5p";
    }

    /// <summary>
    /// Shows the donation address with a button that copies it. NetWatch never contacts any payment
    /// service: the window only displays text.
    /// </summary>
    internal sealed class DonateDialog : Form
    {
        readonly FlatButton _copy = new FlatButton();
        readonly FlatButton _close = new FlatButton();
        readonly TextBox _address = new TextBox();
        readonly System.Windows.Forms.Timer _revert = new System.Windows.Forms.Timer();
        readonly Font _mono;
        Rectangle _headingRect, _bodyRect, _boxRect, _noteRect;

        const string Heading = "Support NetWatch";
        const string Body = "NetWatch is free and open source, with no ads and no tracking. " +
                            "If it is useful to you, you can say thank you with a Bitcoin donation to this address:";
        const string Note = "Bitcoin network only. Donating is entirely optional; NetWatch works the same either way.";

        public DonateDialog()
        {
            Theme.Init();
            Text = "NetWatch";
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
            int width = Theme.S(540);
            int textWidth = width - 2 * pad;
            _mono = new Font("Consolas", 11.5f);

            _headingRect = new Rectangle(pad, pad, textWidth, Theme.WrappedHeight(Heading, Theme.Title, textWidth));
            _bodyRect = new Rectangle(pad, _headingRect.Bottom + Theme.S(10), textWidth, Theme.WrappedHeight(Body, Theme.Body, textWidth));
            _boxRect = new Rectangle(pad, _bodyRect.Bottom + Theme.S(16), textWidth, Theme.S(46));
            _noteRect = new Rectangle(pad, _boxRect.Bottom + Theme.S(12), textWidth, Theme.WrappedHeight(Note, Theme.Small, textWidth));

            // A real (read-only) text box, so the address can also be selected and copied by hand.
            _address.ReadOnly = true;
            _address.BorderStyle = BorderStyle.None;
            _address.BackColor = Theme.Window;
            _address.ForeColor = Theme.Text;
            _address.Font = _mono;
            _address.TextAlign = HorizontalAlignment.Center;
            _address.Text = ProjectInfo.BitcoinAddress;
            _address.TabStop = false;
            int boxTextHeight = _address.PreferredHeight;
            _address.Bounds = new Rectangle(_boxRect.X + Theme.S(10), _boxRect.Y + (_boxRect.Height - boxTextHeight) / 2,
                                            _boxRect.Width - Theme.S(20), boxTextHeight);
            Controls.Add(_address);

            int buttonTop = _noteRect.Bottom + Theme.S(22);
            int buttonHeight = Theme.S(34);
            _copy.Kind = ButtonKind.Primary;
            _copy.Accent = Theme.Up;
            _copy.Text = "Copy address";
            _copy.BackColor = Theme.Panel;
            int copyWidth = Math.Max(Theme.S(140), _copy.PreferredWidth());
            _copy.Bounds = new Rectangle(width - pad - copyWidth, buttonTop, copyWidth, buttonHeight);
            _copy.Click += delegate { CopyAddress(); };
            Controls.Add(_copy);

            _close.Kind = ButtonKind.Normal;
            _close.Text = "Close";
            _close.BackColor = Theme.Panel;
            int closeWidth = Math.Max(Theme.S(100), _close.PreferredWidth());
            _close.Bounds = new Rectangle(_copy.Left - Theme.S(10) - closeWidth, buttonTop, closeWidth, buttonHeight);
            _close.Click += delegate { Close(); };
            Controls.Add(_close);

            _revert.Interval = 2000;
            _revert.Tick += delegate { _revert.Stop(); _copy.Text = "Copy address"; };

            ClientSize = new Size(width, buttonTop + buttonHeight + pad);
        }

        void CopyAddress()
        {
            try
            {
                Clipboard.SetText(ProjectInfo.BitcoinAddress);
                _copy.Text = "Copied";
            }
            catch
            {
                _copy.Text = "Could not copy";
            }
            _revert.Stop();
            _revert.Start();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.DarkTitleBar(Handle);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            _copy.Focus();
            _address.SelectionLength = 0;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape)
            {
                e.Handled = true;
                Close();
                return;
            }
            base.OnKeyDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            Theme.Wrapped(g, Heading, Theme.Title, _headingRect, Theme.Text);
            Theme.Wrapped(g, Body, Theme.Body, _bodyRect, Color.FromArgb(200, 205, 217));
            Theme.FillRound(g, _boxRect, Theme.S(8), Theme.Window);
            Theme.StrokeRound(g, _boxRect, Theme.S(8), Theme.Line, 1f);
            Theme.Wrapped(g, Note, Theme.Small, _noteRect, Theme.TextFaint);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _revert.Dispose();
                _mono.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
