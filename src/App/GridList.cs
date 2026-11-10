// NetWatch - a hand-drawn list with columns, sorting, selection, a slim scroll bar and one optional
// button per row. Used for the program list, the server list and the blocked list.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace NetWatch
{
    internal sealed class GridColumn
    {
        public string Title;
        public int Width;            // unscaled pixels; ignored when Fill > 0
        public float Fill;           // share of the left-over width
        public int MinWidth = 80;
        public bool RightAlign;
        public bool Sortable = true;
        public bool IsButton;
        internal int X, W;           // position on screen (device pixels)

        public GridColumn(string title, int width)
        {
            Title = title;
            Width = width;
        }
    }

    internal struct GridCellState
    {
        public bool Selected;
        public bool RowHover;
        public bool ButtonHover;
        public bool ButtonDown;
    }

    internal delegate void GridPaintCell(Graphics g, int row, int column, Rectangle cell, GridCellState state);

    internal sealed class GridList : Control
    {
        public readonly List<GridColumn> Columns = new List<GridColumn>();

        public GridPaintCell PaintCell;
        public Func<int, bool> RowHasButton;            // which rows show the button
        public Func<int, object> RowKey;                // identity of a row, to make clicks safe while the list re-sorts
        public Func<int, int, string> CellToolTip;
        public Func<int, bool> RowAlert;                // rows that get a red glow

        public event EventHandler SelectionChanged;     // the user picked another row
        public event Action<int> HeaderClicked;         // column index
        public event Action<int> ButtonClicked;         // row index
        public event Action<int, Point> RowRightClicked;

        public string EmptyText = "";
        public int SortColumn = -1;
        public bool SortDescending = true;
        public Color Accent = Theme.Up;
        public bool Selectable = true;

        int _rowCount;
        int _selected = -1;
        int _hoverRow = -1, _hoverCol = -1, _hoverHeader = -1;
        bool _hoverButton, _buttonDown, _hoverThumb, _dragThumb;
        int _downRow = -1;
        object _downKey;
        int _scroll, _dragStartY, _dragStartScroll;
        int _rowHeight, _headerHeight;
        readonly ToolTip _tip = new ToolTip();
        string _tipText = "";

        public GridList()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            BackColor = Theme.Panel;
            TabStop = true;
            _rowHeight = Theme.S(40);
            _headerHeight = Theme.S(32);

            _tip.OwnerDraw = true;
            _tip.InitialDelay = 550;
            _tip.ReshowDelay = 120;
            _tip.AutoPopDelay = 12000;
            _tip.Popup += OnTipPopup;
            _tip.Draw += OnTipDraw;
        }

        public int RowHeight
        {
            get { return _rowHeight; }
            set { _rowHeight = Math.Max(8, value); Invalidate(); }
        }

        public int RowCount { get { return _rowCount; } }
        public int SelectedIndex { get { return _selected; } }

        public void SetRowCount(int count)
        {
            if (count < 0) count = 0;
            _rowCount = count;
            if (_selected >= count) _selected = -1;
            if (_hoverRow >= count) { _hoverRow = -1; _hoverButton = false; }
            ClampScroll();
            Invalidate();
        }

        /// <summary>Sets the highlighted row from code (no SelectionChanged event).</summary>
        public void SetSelected(int index, bool ensureVisible)
        {
            if (index >= _rowCount) index = -1;
            _selected = index;
            if (ensureVisible && index >= 0) EnsureVisible(index);
            Invalidate();
        }

        public void ScrollToTop()
        {
            _scroll = 0;
            Invalidate();
        }

        // ------------------------------------------------------------------ geometry

        int ScrollBarWidth { get { return Theme.S(14); } }
        int ViewHeight { get { return Math.Max(0, ClientSize.Height - _headerHeight); } }
        int MaxScroll { get { return Math.Max(0, _rowCount * _rowHeight - ViewHeight); } }

        void ClampScroll()
        {
            int max = MaxScroll;
            if (_scroll > max) _scroll = max;
            if (_scroll < 0) _scroll = 0;
        }

        void LayoutColumns()
        {
            int padLeft = Theme.S(18);
            int available = ClientSize.Width - padLeft - ScrollBarWidth - Theme.S(6);
            int fixedSum = 0, fillMinimum = 0;
            float fillSum = 0;
            foreach (GridColumn c in Columns)
            {
                if (c.Fill > 0)
                {
                    fillSum += c.Fill;
                    fillMinimum += Theme.S(c.MinWidth);
                }
                else
                {
                    fixedSum += Theme.S(c.Width);
                }
            }
            // In a narrow window the fixed columns give up a little width so nothing is cut off.
            float shrink = 1f;
            if (fixedSum > 0 && fixedSum + fillMinimum > available)
                shrink = Math.Max(0.6f, (available - fillMinimum) / (float)fixedSum);
            int left = Math.Max(0, available - (int)(fixedSum * shrink));
            int x = padLeft;
            foreach (GridColumn c in Columns)
            {
                int w = c.Fill > 0 ? Math.Max(Theme.S(c.MinWidth), (int)(left * c.Fill / fillSum)) : (int)(Theme.S(c.Width) * shrink);
                c.X = x;
                c.W = w;
                x += w;
            }
        }

        Rectangle CellRect(int row, int column)
        {
            GridColumn c = Columns[column];
            return new Rectangle(c.X, _headerHeight + row * _rowHeight - _scroll, c.W, _rowHeight);
        }

        /// <summary>Self-test hook: shows the list as if the mouse pointer were over a cell.</summary>
        internal void TestSetHover(int row, int column, bool overButton)
        {
            _hoverRow = row;
            _hoverCol = column;
            _hoverButton = overButton;
            Invalidate();
        }

        /// <summary>Self-test hook: shows a column title as if the mouse pointer were over it.</summary>
        internal void TestSetHeaderHover(int column)
        {
            _hoverHeader = column;
            Invalidate();
        }

        /// <summary>Screen rectangle of one cell, in this control's coordinates.</summary>
        public Rectangle GetCellBounds(int row, int column)
        {
            LayoutColumns();
            return CellRect(row, column);
        }

        /// <summary>Where the button sits inside a button-column cell.</summary>
        public Rectangle ButtonRect(Rectangle cell)
        {
            int h = Math.Min(Theme.S(30), cell.Height - Theme.S(8));
            int w = Math.Min(cell.Width - Theme.S(10), Theme.S(96));
            return new Rectangle(cell.Right - w - Theme.S(2), cell.Top + (cell.Height - h) / 2, w, h);
        }

        Rectangle TrackRect()
        {
            return new Rectangle(ClientSize.Width - Theme.S(11), _headerHeight + Theme.S(3), Theme.S(7), ViewHeight - Theme.S(6));
        }

        Rectangle ThumbRect()
        {
            int max = MaxScroll;
            if (max <= 0) return Rectangle.Empty;
            Rectangle track = TrackRect();
            if (track.Height < Theme.S(20)) return Rectangle.Empty;
            long content = (long)_rowCount * _rowHeight;
            int h = (int)Math.Max(Theme.S(30), (long)track.Height * ViewHeight / content);
            if (h > track.Height) h = track.Height;
            int y = track.Y + (int)((long)(track.Height - h) * _scroll / max);
            return new Rectangle(track.X, y, track.Width, h);
        }

        public int RowAt(Point p)
        {
            if (p.Y < _headerHeight || p.X >= ClientSize.Width - ScrollBarWidth) return -1;
            int row = (p.Y - _headerHeight + _scroll) / _rowHeight;
            return row >= 0 && row < _rowCount ? row : -1;
        }

        int ColumnAt(int x)
        {
            for (int i = 0; i < Columns.Count; i++)
                if (x >= Columns[i].X && x < Columns[i].X + Columns[i].W) return i;
            return -1;
        }

        bool OverButton(int row, int column, Point p)
        {
            if (row < 0 || column < 0 || !Columns[column].IsButton) return false;
            if (RowHasButton == null || !RowHasButton(row)) return false;
            return ButtonRect(CellRect(row, column)).Contains(p);
        }

        void EnsureVisible(int row)
        {
            int top = row * _rowHeight, bottom = top + _rowHeight;
            if (top < _scroll) _scroll = top;
            else if (bottom > _scroll + ViewHeight) _scroll = bottom - ViewHeight;
            ClampScroll();
        }

        // --------------------------------------------------------------------- mouse

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_dragThumb)
            {
                Rectangle track = TrackRect();
                Rectangle thumb = ThumbRect();
                int range = track.Height - thumb.Height;
                if (range > 0)
                {
                    _scroll = _dragStartScroll + (int)((long)(e.Y - _dragStartY) * MaxScroll / range);
                    ClampScroll();
                    Invalidate();
                }
                return;
            }
            UpdateHover(e.Location);
        }

        void UpdateHover(Point p)
        {
            LayoutColumns();
            int row = RowAt(p);
            int col = ColumnAt(p.X);
            bool button = OverButton(row, col, p);
            bool thumb = ThumbRect().Contains(p) || (MaxScroll > 0 && p.X >= ClientSize.Width - ScrollBarWidth && p.Y >= _headerHeight);
            bool headerHot = p.Y >= 0 && p.Y < _headerHeight && col >= 0 && Columns[col].Sortable && HeaderClicked != null;
            int header = headerHot ? col : -1;

            if (row != _hoverRow || col != _hoverCol || button != _hoverButton || thumb != _hoverThumb || header != _hoverHeader)
            {
                _hoverRow = row;
                _hoverCol = col;
                _hoverButton = button;
                _hoverThumb = thumb;
                _hoverHeader = header;
                Invalidate();
                UpdateToolTip(row, col);
            }
            Cursor = button || headerHot ? Cursors.Hand : Cursors.Default;
        }

        void UpdateToolTip(int row, int col)
        {
            string text = "";
            if (_hoverHeader >= 0) text = "Click to sort the list by this column. Click again to reverse the order.";
            else if (row >= 0 && col >= 0 && CellToolTip != null) text = CellToolTip(row, col) ?? "";
            if (text == _tipText) return;
            _tipText = text;
            _tip.SetToolTip(this, text);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hoverRow != -1 || _hoverButton || _hoverThumb || _hoverHeader != -1)
            {
                _hoverRow = -1;
                _hoverCol = -1;
                _hoverHeader = -1;
                _hoverButton = false;
                _hoverThumb = false;
                Invalidate();
            }
            if (_tipText.Length > 0)
            {
                _tipText = "";
                _tip.SetToolTip(this, "");
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            LayoutColumns();
            Point p = e.Location;

            if (e.Button == MouseButtons.Left && MaxScroll > 0 && p.X >= ClientSize.Width - ScrollBarWidth && p.Y >= _headerHeight)
            {
                Rectangle thumb = ThumbRect();
                if (!thumb.Contains(p))
                {
                    // click on the track: jump there
                    Rectangle track = TrackRect();
                    int range = Math.Max(1, track.Height - thumb.Height);
                    _scroll = (int)((long)(p.Y - track.Y - thumb.Height / 2) * MaxScroll / range);
                    ClampScroll();
                }
                _dragThumb = true;
                _dragStartY = p.Y;
                _dragStartScroll = _scroll;
                Invalidate();
                return;
            }

            if (p.Y < _headerHeight)
            {
                int col = ColumnAt(p.X);
                if (e.Button == MouseButtons.Left && col >= 0 && Columns[col].Sortable && HeaderClicked != null)
                    HeaderClicked(col);
                return;
            }

            int row = RowAt(p);
            if (row < 0) return;
            int column = ColumnAt(p.X);

            if (e.Button == MouseButtons.Left && OverButton(row, column, p))
            {
                _buttonDown = true;
                _downRow = row;
                _downKey = RowKey != null ? RowKey(row) : null;
                Invalidate();
                return;
            }

            if (Selectable && row != _selected)
            {
                _selected = row;
                Invalidate();
                if (SelectionChanged != null) SelectionChanged(this, EventArgs.Empty);
            }
            if (e.Button == MouseButtons.Right && RowRightClicked != null)
                RowRightClicked(row, p);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (_dragThumb)
            {
                _dragThumb = false;
                Invalidate();
                return;
            }
            if (_buttonDown)
            {
                _buttonDown = false;
                LayoutColumns();
                int row = RowAt(e.Location);
                int column = ColumnAt(e.X);
                bool same = row == _downRow && OverButton(row, column, e.Location);
                // The list may have been re-sorted between press and release: only accept the click
                // when the very same item is still under the pointer.
                if (same && RowKey != null && !object.Equals(RowKey(row), _downKey)) same = false;
                _downRow = -1;
                _downKey = null;
                Invalidate();
                if (same && ButtonClicked != null) ButtonClicked(row);
            }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (MaxScroll <= 0) return;
            _scroll -= e.Delta / 120 * _rowHeight * (_rowHeight > Theme.S(40) ? 1 : 3);
            ClampScroll();
            UpdateHover(PointToClient(MousePosition));
            Invalidate();
        }

        // ------------------------------------------------------------------ keyboard

        protected override bool IsInputKey(Keys keyData)
        {
            switch (keyData & Keys.KeyCode)
            {
                case Keys.Up:
                case Keys.Down:
                case Keys.PageUp:
                case Keys.PageDown:
                case Keys.Home:
                case Keys.End:
                    return true;
            }
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (_rowCount == 0) return;
            int page = Math.Max(1, ViewHeight / _rowHeight - 1);
            if (!Selectable)
            {
                // a list without a selection just scrolls
                switch (e.KeyCode)
                {
                    case Keys.Up: _scroll -= _rowHeight; break;
                    case Keys.Down: _scroll += _rowHeight; break;
                    case Keys.PageUp: _scroll -= page * _rowHeight; break;
                    case Keys.PageDown: _scroll += page * _rowHeight; break;
                    case Keys.Home: _scroll = 0; break;
                    case Keys.End: _scroll = MaxScroll; break;
                    default: return;
                }
                e.Handled = true;
                ClampScroll();
                Invalidate();
                return;
            }
            int target = _selected;
            switch (e.KeyCode)
            {
                case Keys.Up: target = _selected <= 0 ? 0 : _selected - 1; break;
                case Keys.Down: target = _selected < 0 ? 0 : Math.Min(_rowCount - 1, _selected + 1); break;
                case Keys.PageUp: target = Math.Max(0, (_selected < 0 ? 0 : _selected) - page); break;
                case Keys.PageDown: target = Math.Min(_rowCount - 1, (_selected < 0 ? 0 : _selected) + page); break;
                case Keys.Home: target = 0; break;
                case Keys.End: target = _rowCount - 1; break;
                default: return;
            }
            e.Handled = true;
            if (target != _selected)
            {
                _selected = target;
                EnsureVisible(target);
                Invalidate();
                if (SelectionChanged != null) SelectionChanged(this, EventArgs.Empty);
            }
        }

        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

        // -------------------------------------------------------------------- drawing

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            if (Columns.Count == 0) return;
            LayoutColumns();
            ClampScroll();

            int width = ClientSize.Width, height = ClientSize.Height;

            if (_rowCount == 0)
            {
                if (!string.IsNullOrEmpty(EmptyText))
                {
                    int textWidth = Math.Min(width - Theme.S(60), Theme.S(620));
                    int textHeight = Theme.WrappedHeight(EmptyText, Theme.Body, textWidth);
                    Rectangle r = new Rectangle((width - textWidth) / 2, _headerHeight + Math.Max(Theme.S(16), (ViewHeight - textHeight) / 2 - Theme.S(8)), textWidth, textHeight + 4);
                    TextRenderer.DrawText(g, EmptyText, Theme.Body, r, Theme.TextFaint,
                        TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.WordBreak | TextFormatFlags.HorizontalCenter | TextFormatFlags.Top);
                }
            }
            else
            {
                int first = _scroll / _rowHeight;
                int y = _headerHeight - (_scroll % _rowHeight);
                using (SolidBrush selected = new SolidBrush(Theme.RowSelected))
                using (SolidBrush hover = new SolidBrush(Theme.RowHover))
                using (SolidBrush accent = new SolidBrush(Accent))
                using (Pen line = new Pen(Theme.Line))
                {
                    for (int row = first; row < _rowCount && y < height; row++, y += _rowHeight)
                    {
                        bool isSelected = Selectable && row == _selected;
                        bool isHover = row == _hoverRow;
                        Rectangle rowRect = new Rectangle(0, y, width - ScrollBarWidth, _rowHeight);
                        bool alert = RowAlert != null && RowAlert(row);
                        if (isSelected) g.FillRectangle(selected, rowRect);
                        else if (isHover) g.FillRectangle(hover, rowRect);
                        if (alert)
                        {
                            // red glow that fades out towards the right, plus a red edge
                            Rectangle glow = new Rectangle(0, y, Math.Max(Theme.S(200), rowRect.Width * 6 / 10), _rowHeight - 1);
                            using (LinearGradientBrush fade = new LinearGradientBrush(new Rectangle(glow.X - 1, glow.Y, glow.Width + 2, glow.Height),
                                       Color.FromArgb(88, Theme.Danger), Color.FromArgb(0, Theme.Danger), 0f))
                                g.FillRectangle(fade, glow);
                            using (SolidBrush edge = new SolidBrush(Theme.Danger))
                                g.FillRectangle(edge, new Rectangle(0, y, Theme.S(4), _rowHeight - 1));
                        }
                        else if (isSelected)
                        {
                            g.FillRectangle(accent, new Rectangle(0, y, Theme.S(3), _rowHeight));
                        }
                        if (PaintCell != null)
                        {
                            for (int col = 0; col < Columns.Count; col++)
                            {
                                GridColumn c = Columns[col];
                                GridCellState state = new GridCellState();
                                state.Selected = isSelected;
                                state.RowHover = isHover;
                                state.ButtonHover = isHover && _hoverButton && col == _hoverCol && (!_buttonDown || row == _downRow);
                                state.ButtonDown = _buttonDown && row == _downRow && state.ButtonHover;
                                PaintCell(g, row, col, new Rectangle(c.X, y, c.W, _rowHeight), state);
                            }
                        }
                        g.DrawLine(line, Theme.S(18), y + _rowHeight - 1, width - ScrollBarWidth - Theme.S(6), y + _rowHeight - 1);
                    }
                }
            }

            PaintHeader(g, width);
            PaintScrollBar(g);
        }

        void PaintHeader(Graphics g, int width)
        {
            using (SolidBrush back = new SolidBrush(BackColor))
                g.FillRectangle(back, 0, 0, width, _headerHeight);
            for (int i = 0; i < Columns.Count; i++)
            {
                GridColumn c = Columns[i];
                if (string.IsNullOrEmpty(c.Title)) continue;
                bool sorted = i == SortColumn;
                bool hot = i == _hoverHeader;
                Color color = hot ? Theme.Text : (sorted ? Theme.TextDim : Theme.TextFaint);
                int arrow = sorted ? Theme.S(12) : 0;
                int textWidth = Theme.Measure(c.Title, Theme.Caps);
                Rectangle cell = new Rectangle(c.X, 0, c.W - Theme.S(10), _headerHeight);
                int arrowX, textX;
                if (c.RightAlign)
                {
                    Theme.Right(g, c.Title, Theme.Caps, cell, color);
                    arrowX = cell.Right - textWidth - arrow;
                    textX = cell.Right - textWidth;
                }
                else
                {
                    Theme.Left(g, c.Title, Theme.Caps, cell, color);
                    arrowX = cell.X + Math.Min(textWidth, cell.Width) + Theme.S(5);
                    textX = cell.X;
                }
                if (sorted) PaintSortArrow(g, arrowX, _headerHeight / 2, SortDescending, color);
                if (hot)
                {
                    // underline the title the mouse is on: it can be clicked to sort
                    using (SolidBrush b = new SolidBrush(Accent))
                        g.FillRectangle(b, textX, _headerHeight - Theme.S(4), Math.Min(textWidth, cell.Width), Math.Max(2, Theme.S(2)));
                }
            }
            using (Pen line = new Pen(Theme.Line))
                g.DrawLine(line, 0, _headerHeight - 1, width, _headerHeight - 1);
        }

        static void PaintSortArrow(Graphics g, int x, int centerY, bool down, Color color)
        {
            float w = Theme.S(7), h = Theme.S(4);
            PointF[] triangle = down
                ? new PointF[] { new PointF(x, centerY - h / 2), new PointF(x + w, centerY - h / 2), new PointF(x + w / 2, centerY + h / 2) }
                : new PointF[] { new PointF(x, centerY + h / 2), new PointF(x + w, centerY + h / 2), new PointF(x + w / 2, centerY - h / 2) };
            SmoothingMode old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (SolidBrush b = new SolidBrush(color))
                g.FillPolygon(b, triangle);
            g.SmoothingMode = old;
        }

        void PaintScrollBar(Graphics g)
        {
            Rectangle thumb = ThumbRect();
            if (thumb.IsEmpty) return;
            Color color = _dragThumb ? Theme.TextDim : (_hoverThumb ? Theme.TextFaint : Color.FromArgb(62, 68, 86));
            Theme.FillRound(g, thumb, thumb.Width / 2, color);
        }

        // ------------------------------------------------------------------- tooltip

        const TextFormatFlags TipFlags = TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.WordBreak |
                                         TextFormatFlags.Left | TextFormatFlags.Top;

        void OnTipPopup(object sender, PopupEventArgs e)
        {
            Size size = TextRenderer.MeasureText(_tipText, Theme.Small, new Size(Theme.S(560), 10000), TipFlags);
            e.ToolTipSize = new Size(size.Width + Theme.S(18), size.Height + Theme.S(12));
        }

        void OnTipDraw(object sender, DrawToolTipEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Color.FromArgb(34, 38, 49));
            using (Pen border = new Pen(Theme.Line))
                g.DrawRectangle(border, 0, 0, e.Bounds.Width - 1, e.Bounds.Height - 1);
            Rectangle r = new Rectangle(Theme.S(9), Theme.S(6), e.Bounds.Width - Theme.S(18), e.Bounds.Height - Theme.S(12));
            TextRenderer.DrawText(g, e.ToolTipText, Theme.Small, r, Theme.Text, TipFlags);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _tip.Dispose();
            base.Dispose(disposing);
        }
    }
}
