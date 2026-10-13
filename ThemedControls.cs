using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace ProcessMuterByChannel
{
    // Собственные контролы в стиле OBS (Yami): стандартные ComboBox/Button/GroupBox из WinForms
    // нельзя перекрасить под тему, поэтому они нарисованы вручную по значениям из ObsTheme.

    /// <summary>Оттенок обычной кнопки: слегка красный для опасных действий, слегка зелёный для добавления</summary>
    internal enum ButtonTone { Normal, Danger, Success }

    /// <summary>Кнопка как QPushButton в OBS: скруглённая, меняет фон и рамку при наведении</summary>
    internal sealed class ThemedButton : Control
    {
        private bool _hover;
        private bool _down;
        private Font? _iconFont;

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public bool Primary { get; set; }
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public ButtonTone Tone { get; set; }
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public bool Round { get; set; }
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public string? Glyph { get; set; }

        public ThemedButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw | ControlStyles.Selectable | ControlStyles.StandardClick | ControlStyles.StandardDoubleClick, true);
            TabStop = true;
            Font = Ui.Font();
            Size = new Size(Ui.Px(120), Ui.Px(30));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent?.BackColor ?? ObsTheme.BaseBg);

            Color fill, border;
            if (Primary)
            {
                fill = !Enabled ? ObsTheme.ButtonBg : _hover || _down ? ObsTheme.PrimaryLight : ObsTheme.Primary;
                border = !Enabled ? ObsTheme.ButtonBg : ObsTheme.PrimaryLight;
                if (_hover || Focused) border = ObsTheme.ButtonBorderHover;
            }
            else if (Tone != ButtonTone.Normal && Enabled)
            {
                bool danger = Tone == ButtonTone.Danger;
                Color toneBg = danger ? ObsTheme.DangerBg : ObsTheme.SuccessBg;
                Color toneHover = danger ? ObsTheme.DangerBgHover : ObsTheme.SuccessBgHover;
                Color toneBorder = danger ? ObsTheme.DangerBorder : ObsTheme.SuccessBorder;
                fill = _down ? ObsTheme.ButtonBgDown : _hover ? toneHover : toneBg;
                border = !_down && (_hover || Focused) ? toneBorder : fill;
            }
            else
            {
                fill = !Enabled ? ObsTheme.ButtonBg : _down ? ObsTheme.ButtonBgDown : _hover ? ObsTheme.ButtonBgHover : ObsTheme.ButtonBg;
                border = Enabled && !_down && (_hover || Focused) ? ObsTheme.ButtonBorderHover : fill;
            }

            // с клавиатуры (Tab) фокус виден отчётливым кольцом; после щелчка мышью кольца нет
            bool keyboardFocus = Focused && ShowFocusCues && Enabled;
            if (keyboardFocus) border = ObsTheme.FocusRing;

            var rect = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
            using (var path = Ui.RoundedRect(rect, Round ? Math.Min(Width, Height) / 2f : Ui.Px(4)))
            using (var brush = new SolidBrush(fill))
            using (var pen = new Pen(border, 1f))
            {
                g.FillPath(brush, path);
                g.DrawPath(pen, path);
            }

            Color textColor = Enabled ? ObsTheme.Text : ObsTheme.TextMuted;
            Color glyphColor = textColor;
            if (Enabled && Tone == ButtonTone.Danger) glyphColor = ObsTheme.DangerAccent;
            else if (Enabled && Tone == ButtonTone.Success) glyphColor = ObsTheme.SuccessAccent;
            DrawContent(g, textColor, glyphColor);
        }

        private void DrawContent(Graphics g, Color color, Color glyphColor)
        {
            const TextFormatFlags flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine
                | TextFormatFlags.VerticalCenter | TextFormatFlags.Left;
            var unlimited = new Size(int.MaxValue, int.MaxValue);

            int glyphWidth = 0;
            if (!string.IsNullOrEmpty(Glyph))
            {
                _iconFont ??= Ui.IconFont(Text.Length == 0 ? 0.95f : 0.85f);
                glyphWidth = TextRenderer.MeasureText(g, Glyph, _iconFont, unlimited, flags).Width;
            }

            int textWidth = Text.Length > 0 ? TextRenderer.MeasureText(g, Text, Font, unlimited, flags).Width : 0;
            int gap = glyphWidth > 0 && textWidth > 0 ? Ui.Px(8) : 0;
            int x = Math.Max(Ui.Px(6), (Width - (glyphWidth + gap + textWidth)) / 2);

            if (glyphWidth > 0)
            {
                TextRenderer.DrawText(g, Glyph, _iconFont, new Rectangle(x, 0, glyphWidth, Height), glyphColor, flags);
                x += glyphWidth + gap;
            }

            if (textWidth > 0)
            {
                TextRenderer.DrawText(g, Text, Font, new Rectangle(x, 0, Math.Max(0, Width - x - Ui.Px(6)), Height), color,
                    flags | TextFormatFlags.EndEllipsis);
            }
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { _down = true; Invalidate(); } base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space) { _down = true; Invalidate(); e.Handled = true; }
            base.OnKeyDown(e);
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space && _down)
            {
                _down = false;
                Invalidate();
                OnClick(EventArgs.Empty);
                e.Handled = true;
            }
            base.OnKeyUp(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _iconFont?.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>Карточка как QGroupBox в OBS: фон bg_base, скругление 4 px, жирный заголовок</summary>
    internal class CardPanel : Control
    {
        public CardPanel()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw, true);
            BackColor = ObsTheme.BaseBg;
            Font = Ui.Font();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent?.BackColor ?? ObsTheme.WindowBg);

            using (var path = Ui.RoundedRect(new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), Ui.Px(4)))
            using (var brush = new SolidBrush(ObsTheme.BaseBg))
            using (var pen = new Pen(ObsTheme.CardBorder, 1f))
            {
                g.FillPath(brush, path);
                g.DrawPath(pen, path);
            }

            using var bold = Ui.Font(FontStyle.Bold);
            TextRenderer.DrawText(g, Text, bold, new Rectangle(Ui.Px(16), Ui.Px(10), Width - Ui.Px(200), Ui.Px(28)), ObsTheme.Text,
                TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
    }

    /// <summary>Общая рамка полей ввода: фон input_bg, при наведении рамка, при фокусе рамка цвета primary</summary>
    internal abstract class FramedControl : Control
    {
        private bool _hover;

        protected FramedControl()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw | ControlStyles.StandardClick, true);
            Font = Ui.Font();
            Size = new Size(Ui.Px(200), Ui.Px(30));
        }

        protected abstract bool FocusedInside { get; }

        protected bool Hovered => _hover;

        protected virtual Color FillColor => !Enabled ? ObsTheme.InputBg : FocusedInside ? ObsTheme.InputBgFocus : _hover ? ObsTheme.InputBgHover : ObsTheme.InputBg;
        protected virtual Color BorderColor => !Enabled ? ObsTheme.InputBg : FocusedInside ? ObsTheme.FocusRing : _hover ? ObsTheme.InputBorderHover : ObsTheme.InputBg;

        protected virtual void OnStateChanged() { }

        public void RefreshState()
        {
            if (!IsHandleCreated) return;

            _hover = ClientRectangle.Contains(PointToClient(Cursor.Position));
            OnStateChanged();
            Invalidate();
        }

        protected void HookChild(Control child)
        {
            child.MouseEnter += (s, e) => RefreshState();
            child.MouseLeave += (s, e) => RefreshState();
            child.GotFocus += (s, e) => RefreshState();
            child.LostFocus += (s, e) => RefreshState();
        }

        protected void PaintFrame(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent?.BackColor ?? ObsTheme.BaseBg);

            using var path = Ui.RoundedRect(new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), Ui.Px(4));
            using var brush = new SolidBrush(FillColor);
            using var pen = new Pen(BorderColor, 1f);
            g.FillPath(brush, path);
            g.DrawPath(pen, path);
        }

        protected override void OnMouseEnter(EventArgs e) { RefreshState(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { RefreshState(); base.OnMouseLeave(e); }
        protected override void OnEnabledChanged(EventArgs e) { RefreshState(); base.OnEnabledChanged(e); }
        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); OnStateChanged(); }
        protected override void OnPaint(PaintEventArgs e) => PaintFrame(e.Graphics);
    }

    /// <summary>Однострочное поле ввода как QLineEdit в OBS</summary>
    internal sealed class ThemedTextBox : FramedControl
    {
        private readonly TextBox _box;

        public ThemedTextBox()
        {
            _box = new TextBox { BorderStyle = BorderStyle.None, Font = Font };
            Controls.Add(_box);
            HookChild(_box);
            _box.TextChanged += (s, e) => OnTextChanged(e);
            OnStateChanged();
            LayoutBox();
        }

        [AllowNull]
        public override string Text
        {
            get => _box.Text;
            set => _box.Text = value;
        }

        /// <summary>Внутреннее поле ввода: нужно, чтобы поймать Enter и уход фокуса</summary>
        internal TextBox InnerTextBox => _box;

        protected override bool FocusedInside => _box.Focused;

        private bool _flat;
        private string _suffix = "";
        private int _rightReserve;

        /// <summary>Плоский вид для заголовка: без рамки и фона, пока на поле нет курсора и фокуса, справа значок карандаша</summary>
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public bool Flat
        {
            get => _flat;
            set { _flat = value; UpdateReserve(); OnStateChanged(); Invalidate(); }
        }

        /// <summary>Единица измерения внутри поля справа, например «мс»</summary>
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public string Suffix
        {
            get => _suffix;
            set { _suffix = value ?? ""; UpdateReserve(); Invalidate(); }
        }

        private bool FlatIdle => _flat && !FocusedInside && !Hovered;

        protected override Color FillColor => FlatIdle && Enabled ? ObsTheme.BaseBg : base.FillColor;
        protected override Color BorderColor => FlatIdle && Enabled ? ObsTheme.BaseBg : base.BorderColor;

        // Место справа под единицу измерения и карандаш; поле ввода его не занимает
        private void UpdateReserve()
        {
            int reserve = 0;
            if (_suffix.Length > 0) reserve += TextRenderer.MeasureText(_suffix, Font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding).Width + Ui.Px(8);
            if (_flat) reserve += Ui.Px(26);
            _rightReserve = reserve;
            LayoutBox();
        }

        protected override void OnStateChanged()
        {
            // вызывается и из конструктора базового класса, когда поле ещё не создано
            if (_box is null) return;

            _box.BackColor = FillColor;
            _box.ForeColor = Enabled ? ObsTheme.Text : ObsTheme.TextMuted;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);

            var g = e.Graphics;
            const TextFormatFlags flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine
                | TextFormatFlags.VerticalCenter | TextFormatFlags.Right;

            int right = Width - Ui.Px(8);
            if (_flat && !FocusedInside)
            {
                using var icon = Ui.IconFont(0.85f);
                var color = Hovered ? ObsTheme.Text : ObsTheme.TextMuted;
                TextRenderer.DrawText(g, Ui.GlyphEdit, icon, new Rectangle(right - Ui.Px(18), 0, Ui.Px(18), Height), color, flags);
                right -= Ui.Px(26);
            }

            if (_suffix.Length > 0)
            {
                using var small = Ui.Font(scale: 0.92f);
                TextRenderer.DrawText(g, _suffix, small, new Rectangle(0, 0, right, Height), ObsTheme.TextMuted, flags);
            }
        }

        private void LayoutBox()
        {
            // вызывается из конструктора базового класса, когда поле ещё не создано
            if (_box is null) return;

            _box.SetBounds(Ui.Px(8), Math.Max(0, (Height - _box.Height) / 2), Math.Max(10, Width - Ui.Px(16) - _rightReserve), _box.Height);
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); LayoutBox(); }

        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e);
            // вызывается и из конструктора базового класса, когда поле ещё не создано
            if (_box is null) return;
            _box.Font = Font;
            LayoutBox();
        }
        protected override void OnMouseDown(MouseEventArgs e) { _box.Focus(); base.OnMouseDown(e); }
    }

    /// <summary>Список выбора как QComboBox в OBS. С Editable можно печатать текст (поиск по списку)</summary>
    internal sealed class ThemedPicker : FramedControl
    {
        private const int MaxRows = 8;

        private readonly bool _editable;
        private readonly TextBox? _box;
        private readonly List<object> _items = new List<object>();
        private DropDownPopup? _popup;
        private PopupCloseFilter? _filter;
        private Form? _hostForm;
        private int _selectedIndex = -1;

        public event EventHandler? SelectionChanged;
        public event EventHandler? UserTextChanged;   // пользователь изменил текст (только Editable)
        public event EventHandler? DropDownOpening;   // пользователь открывает список
        public event EventHandler? FieldEntered;      // поле получило фокус (только Editable)

        public ThemedPicker(bool editable)
        {
            _editable = editable;

            if (editable)
            {
                _box = new TextBox { BorderStyle = BorderStyle.None, Font = Font };
                Controls.Add(_box);
                HookChild(_box);
                _box.GotFocus += (s, e) => FieldEntered?.Invoke(this, EventArgs.Empty);
                _box.LostFocus += (s, e) => CloseDropDown();
                _box.KeyDown += OnBoxKeyDown;
                _box.TextChanged += OnBoxTextChanged;
                OnStateChanged();
                LayoutBox();
            }
            else
            {
                SetStyle(ControlStyles.Selectable, true);
                TabStop = true;
            }
        }

        public IReadOnlyList<object> Items => _items;

        /// <summary>Заменяет список вариантов; выбранный элемент сбрасывается без события</summary>
        public void SetItems(IEnumerable<object> items)
        {
            _items.Clear();
            _items.AddRange(items);
            _selectedIndex = -1;
            Invalidate();

            if (IsDropDownOpen) OpenDropDown(userInitiated: false);
        }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public int SelectedIndex
        {
            get => _selectedIndex;
            set
            {
                if (value < -1 || value >= _items.Count) value = -1;
                if (value == _selectedIndex) return;

                _selectedIndex = value;
                if (_editable && value >= 0) SetBoxText(_items[value].ToString() ?? "");
                Invalidate();
                SelectionChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public object? SelectedItem
        {
            get => _selectedIndex >= 0 && _selectedIndex < _items.Count ? _items[_selectedIndex] : null;
            set => SelectedIndex = value == null ? -1 : _items.IndexOf(value);
        }

        [AllowNull]
        public override string Text
        {
            get => _editable ? _box!.Text : SelectedItem?.ToString() ?? "";
            set { if (_editable) SetBoxText(value ?? ""); }
        }

        protected override bool FocusedInside => _editable ? _box!.Focused : Focused;

        public bool IsDropDownOpen => _popup is { Visible: true };

        public void FocusField()
        {
            if (_editable) _box!.Focus();
            else Focus();
        }

        public void MoveCaretToEnd()
        {
            if (!_editable) return;
            _box!.SelectionStart = _box.Text.Length;
            _box.SelectionLength = 0;
        }

        // --- текстовое поле (Editable) ---

        private void SetBoxText(string text)
        {
            if (_box!.Text == text) return;

            _suppressTextEvent = true;
            try { _box.Text = text; }
            finally { _suppressTextEvent = false; }
        }

        private bool _suppressTextEvent;

        private void OnBoxTextChanged(object? sender, EventArgs e)
        {
            OnTextChanged(e);
            if (_suppressTextEvent) return;

            _selectedIndex = -1;
            UserTextChanged?.Invoke(this, EventArgs.Empty);
        }

        protected override void OnStateChanged()
        {
            if (_box == null) return;

            _box.BackColor = FillColor;
            _box.ForeColor = Enabled ? ObsTheme.Text : ObsTheme.TextMuted;
        }

        private void LayoutBox()
        {
            if (_box == null) return;
            _box.SetBounds(Ui.Px(8), Math.Max(0, (Height - _box.Height) / 2), Math.Max(10, Width - Height - Ui.Px(8)), _box.Height);
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); LayoutBox(); }

        // --- рисование ---

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            PaintFrame(g);

            if (!_editable)
            {
                TextRenderer.DrawText(g, Text, Font, new Rectangle(Ui.Px(8), 0, Math.Max(0, Width - Height - Ui.Px(8)), Height),
                    Enabled ? ObsTheme.Text : ObsTheme.TextMuted,
                    TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter
                    | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            }

            // шеврон справа
            float cx = Width - Height / 2f;
            float cy = Height / 2f;
            float w = Ui.Px(4.5f);
            float h = Ui.Px(2.5f);
            using var pen = new Pen(Enabled ? ObsTheme.Text : ObsTheme.TextMuted, Math.Max(1.5f, Ui.Px(1.6f)))
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round
            };
            g.DrawLines(pen, new[] { new PointF(cx - w, cy - h), new PointF(cx, cy + h), new PointF(cx + w, cy - h) });
        }

        // --- ввод ---

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left || !Enabled) return;

            bool onChevron = e.X >= Width - Height;
            if (_editable)
            {
                if (!onChevron) { _box!.Focus(); return; }
                _box!.Focus();
            }
            else
            {
                Focus();
            }

            if (IsDropDownOpen) CloseDropDown();
            else OpenDropDown(userInitiated: true);
        }

        protected override void OnGotFocus(EventArgs e) { RefreshState(); base.OnGotFocus(e); }

        protected override void OnLostFocus(EventArgs e)
        {
            if (!_editable) CloseDropDown();
            RefreshState();
            base.OnLostFocus(e);
        }

        protected override bool IsInputKey(Keys keyData)
        {
            switch (keyData & Keys.KeyCode)
            {
                case Keys.Up:
                case Keys.Down:
                case Keys.Enter:
                case Keys.Escape:
                    return !_editable || IsDropDownOpen;
            }
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (HandleKey(e.KeyCode)) e.Handled = true;
            base.OnKeyDown(e);
        }

        private void OnBoxKeyDown(object? sender, KeyEventArgs e)
        {
            if (HandleKey(e.KeyCode))
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
        }

        private bool HandleKey(Keys key)
        {
            bool open = IsDropDownOpen;
            switch (key)
            {
                case Keys.Down:
                    if (!open) OpenDropDown(userInitiated: true);
                    else MoveHot(1);
                    return true;
                case Keys.Up:
                    if (!open) OpenDropDown(userInitiated: true);
                    else MoveHot(-1);
                    return true;
                case Keys.PageDown: if (open) { MoveHot(MaxRows); return true; } return false;
                case Keys.PageUp: if (open) { MoveHot(-MaxRows); return true; } return false;
                case Keys.Space:
                    if (_editable) return false;
                    if (open) CloseDropDown(); else OpenDropDown(userInitiated: true);
                    return true;
                case Keys.Enter:
                    if (open && _popup!.List.Hot >= 0) { Choose(_popup.List.Hot); return true; }
                    return open;
                case Keys.Escape:
                    if (open) { CloseDropDown(); return true; }
                    return false;
            }
            return false;
        }

        private void MoveHot(int delta)
        {
            var list = _popup!.List;
            int next = list.Hot < 0 ? (delta > 0 ? 0 : _items.Count - 1) : list.Hot + delta;
            list.Hot = Math.Clamp(next, 0, _items.Count - 1);
            list.EnsureVisible(list.Hot);
            list.Invalidate();
        }

        private void Choose(int index)
        {
            if (index < 0 || index >= _items.Count) return;

            CloseDropDown();
            SelectedIndex = index;
            if (_editable)
            {
                _box!.Focus();
                MoveCaretToEnd();
            }
        }

        // --- выпадающий список ---

        public void OpenDropDown(bool userInitiated)
        {
            var form = FindForm();
            if (form == null || !IsHandleCreated) return;

            if (userInitiated) DropDownOpening?.Invoke(this, EventArgs.Empty);
            if (_items.Count == 0) { CloseDropDown(); return; }

            if (_popup == null)
            {
                _popup = new DropDownPopup();
                _popup.List.ItemChosen += Choose;
            }

            _popup.List.SetItems(_items, _selectedIndex);

            int height = Math.Min(_items.Count, MaxRows) * _popup.List.ItemHeight + 2;
            var location = PointToScreen(new Point(0, Height + 1));
            var area = Screen.FromControl(this).WorkingArea;
            if (location.Y + height > area.Bottom) location.Y = PointToScreen(Point.Empty).Y - height - 1;

            _popup.SetBounds(location.X, location.Y, Width, height);

            if (!_popup.Visible)
            {
                _hostForm = form;
                form.Deactivate += OnHostChanged;
                form.LocationChanged += OnHostChanged;
                form.SizeChanged += OnHostChanged;
                _filter = new PopupCloseFilter(this);
                Application.AddMessageFilter(_filter);
                _popup.Show(form);
            }
        }

        public void CloseDropDown()
        {
            if (_filter != null)
            {
                Application.RemoveMessageFilter(_filter);
                _filter = null;
            }

            if (_hostForm != null)
            {
                _hostForm.Deactivate -= OnHostChanged;
                _hostForm.LocationChanged -= OnHostChanged;
                _hostForm.SizeChanged -= OnHostChanged;
                _hostForm = null;
            }

            if (_popup is { Visible: true }) _popup.Hide();
        }

        private void OnHostChanged(object? sender, EventArgs e) => CloseDropDown();

        private bool OwnsWindow(Control? control)
        {
            return control != null && (control == this || control == _box || (_popup != null && (control == _popup || control == _popup.List)));
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                CloseDropDown();
                _popup?.Dispose();
            }
            base.Dispose(disposing);
        }

        // Закрывает список при клике или прокрутке вне поля и списка
        private sealed class PopupCloseFilter : IMessageFilter
        {
            private readonly ThemedPicker _owner;

            public PopupCloseFilter(ThemedPicker owner) { _owner = owner; }

            public bool PreFilterMessage(ref Message m)
            {
                switch (m.Msg)
                {
                    case 0x0201: // WM_LBUTTONDOWN
                    case 0x0204: // WM_RBUTTONDOWN
                    case 0x0207: // WM_MBUTTONDOWN
                    case 0x00A1: // WM_NCLBUTTONDOWN
                    case 0x020A: // WM_MOUSEWHEEL
                        if (!_owner.OwnsWindow(Control.FromHandle(m.HWnd))) _owner.CloseDropDown();
                        break;
                }
                return false;
            }
        }
    }

    /// <summary>Окно выпадающего списка: не забирает фокус у поля, чтобы можно было продолжать печатать</summary>
    internal sealed class DropDownPopup : Form
    {
        public PopupList List { get; } = new PopupList();

        public DropDownPopup()
        {
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = ObsTheme.InputBorderHover; // 1 px рамки вокруг списка
            Padding = new Padding(1);
            List.Dock = DockStyle.Fill;
            Controls.Add(List);
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x08000000 | 0x00000080; // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW
                return cp;
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0021) // WM_MOUSEACTIVATE
            {
                m.Result = (IntPtr)3; // MA_NOACTIVATE
                return;
            }
            base.WndProc(ref m);
        }
    }

    /// <summary>Список вариантов: строка под курсором закрашивается цветом primary, как в QComboBox OBS</summary>
    internal sealed class PopupList : Control
    {
        private readonly List<string> _items = new List<string>();
        private int _offset;

        public int Hot = -1;
        public int Current = -1;

        public event Action<int>? ItemChosen;

        public PopupList()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
            BackColor = ObsTheme.BaseBg;
            Font = Ui.Font();
        }

        public int ItemHeight => Ui.Px(30);

        private int TotalHeight => _items.Count * ItemHeight;
        private int MaxOffset => Math.Max(0, TotalHeight - Height);

        public void SetItems(IEnumerable<object> items, int current)
        {
            _items.Clear();
            foreach (var item in items) _items.Add(item.ToString() ?? "");

            Current = current;
            Hot = current;
            _offset = 0;
            if (Hot >= 0) EnsureVisible(Hot);
            Invalidate();
        }

        public void EnsureVisible(int index)
        {
            int top = index * ItemHeight;
            if (top < _offset) _offset = top;
            else if (top + ItemHeight > _offset + Height) _offset = top + ItemHeight - Height;
            _offset = Math.Clamp(_offset, 0, MaxOffset);
        }

        private int IndexAt(int y)
        {
            int index = (y + _offset) / ItemHeight;
            return index >= 0 && index < _items.Count ? index : -1;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(ObsTheme.BaseBg);

            bool scrollbar = TotalHeight > Height;
            int right = Width - Ui.Px(3) - (scrollbar ? Ui.Px(8) : 0);

            for (int i = Math.Max(0, _offset / ItemHeight); i < _items.Count; i++)
            {
                int y = i * ItemHeight - _offset;
                if (y >= Height) break;

                var rect = new Rectangle(Ui.Px(3), y + Ui.Px(1), right - Ui.Px(3), ItemHeight - Ui.Px(2));
                if (i == Hot || i == Current)
                {
                    using var path = Ui.RoundedRect(rect, Ui.Px(3));
                    using var brush = new SolidBrush(i == Hot ? ObsTheme.Primary : ObsTheme.ButtonBg);
                    g.FillPath(brush, path);
                }

                TextRenderer.DrawText(g, _items[i], Font, new Rectangle(rect.X + Ui.Px(8), rect.Y, rect.Width - Ui.Px(12), rect.Height),
                    ObsTheme.Text,
                    TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter
                    | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            }

            if (scrollbar)
            {
                int thumb = Math.Max(Ui.Px(24), (int)((long)Height * Height / TotalHeight));
                int top = MaxOffset == 0 ? 0 : (int)((long)(Height - thumb) * _offset / MaxOffset);
                var rect = new RectangleF(Width - Ui.Px(7), top + 1, Ui.Px(4), thumb - 2);
                using var path = Ui.RoundedRect(rect, Ui.Px(2));
                using var brush = new SolidBrush(Color.FromArgb(150, ObsTheme.TextMuted));
                g.FillPath(brush, path);
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int index = IndexAt(e.Y);
            if (index >= 0 && index != Hot)
            {
                Hot = index;
                Invalidate();
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            int index = IndexAt(e.Y);
            if (e.Button == MouseButtons.Left && index >= 0) ItemChosen?.Invoke(index);
            base.OnMouseDown(e);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            _offset = Math.Clamp(_offset - Math.Sign(e.Delta) * ItemHeight * 3, 0, MaxOffset);
            Invalidate();
            base.OnMouseWheel(e);
        }
    }

    /// <summary>Подсказка как QToolTip в OBS: фон bg_base, цвет текста темы</summary>
    internal static class ThemedToolTip
    {
        public static ToolTip Create()
        {
            var tip = new ToolTip { OwnerDraw = true, AutoPopDelay = 30000, InitialDelay = 300, ReshowDelay = 200, UseFading = false, UseAnimation = false };
            var font = Ui.Font(scale: 0.95f);
            int pad = Ui.Px(10);
            const TextFormatFlags flags = TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.WordBreak;

            tip.Popup += (s, e) =>
            {
                string text = tip.GetToolTip(e.AssociatedControl!) ?? "";
                var size = TextRenderer.MeasureText(text, font, new Size(Ui.Px(380), int.MaxValue), flags);
                e.ToolTipSize = new Size(size.Width + pad * 2, size.Height + pad * 2);
            };

            tip.Draw += (s, e) =>
            {
                using (var brush = new SolidBrush(ObsTheme.BaseBg)) e.Graphics.FillRectangle(brush, e.Bounds);
                using (var pen = new Pen(ObsTheme.InputBorderHover)) e.Graphics.DrawRectangle(pen, 0, 0, e.Bounds.Width - 1, e.Bounds.Height - 1);

                var rect = new Rectangle(pad, pad, e.Bounds.Width - pad * 2, e.Bounds.Height - pad * 2);
                TextRenderer.DrawText(e.Graphics, e.ToolTipText, font, rect, ObsTheme.Text, flags);
            };

            return tip;
        }
    }
}
