using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace ProcessMuterByChannel
{
    // Мелкие контролы карточки фильтра: переключатель, плашка состояния, фраза-сводка и список значений.
    // Все рисуются вручную по цветам ObsTheme, как остальные контролы окна.

    /// <summary>Переключатель включён/выключен (как в настройках OBS): дорожка и круглый бегунок</summary>
    internal sealed class ThemedSwitch : Control
    {
        private bool _checked;
        private bool _hover;

        public event EventHandler? CheckedChanged;

        public ThemedSwitch()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw | ControlStyles.Selectable | ControlStyles.StandardClick, true);
            TabStop = true;
            AccessibleRole = AccessibleRole.CheckButton;
            Size = new Size(Ui.Px(40), Ui.Px(22));
        }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public bool Checked
        {
            get => _checked;
            set
            {
                if (_checked == value) return;
                _checked = value;
                Invalidate();
                CheckedChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent?.BackColor ?? ObsTheme.BaseBg);

            var track = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
            Color fill = _checked ? (_hover ? ObsTheme.PrimaryLight : ObsTheme.Primary) : (_hover ? ObsTheme.ButtonBgHover : ObsTheme.ButtonBg);
            Color border = Focused && ShowFocusCues ? ObsTheme.FocusRing : _checked ? fill : ObsTheme.InputBorderHover;

            using (var path = Ui.RoundedRect(track, track.Height / 2f))
            using (var brush = new SolidBrush(fill))
            using (var pen = new Pen(border, Focused && ShowFocusCues ? 2f : 1f))
            {
                g.FillPath(brush, path);
                g.DrawPath(pen, path);
            }

            // бегунок справа, когда включено; на светлой теме светлая дорожка требует тёмного бегунка
            float d = Height - Ui.Px(8);
            float x = _checked ? Width - d - Ui.Px(4) : Ui.Px(4);
            Color knob = _checked ? (ObsTheme.IsDark ? Color.White : Color.FromArgb(0x1D, 0x1F, 0x26)) : ObsTheme.TextMuted;
            using var knobBrush = new SolidBrush(knob);
            g.FillEllipse(knobBrush, x, (Height - d) / 2f, d, d);
        }

        protected override void OnClick(EventArgs e)
        {
            if (Enabled) Checked = !Checked;
            base.OnClick(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space) { Checked = !Checked; e.Handled = true; }
            base.OnKeyDown(e);
        }

        protected override bool IsInputKey(Keys keyData) => keyData == Keys.Space || base.IsInputKey(keyData);

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    }

    /// <summary>Плашка-таблетка с коротким текстом: «Приглушает», «Ждёт звука», «Выключен»</summary>
    internal sealed class StatusChip : Control
    {
        private bool _active;

        public StatusChip()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw, true);
            Font = Ui.Font(scale: 0.9f);
            TabStop = false;
            Height = Ui.Px(24);
        }

        /// <summary>Подсвеченная плашка: фильтр сейчас что-то делает</summary>
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public bool Active
        {
            get => _active;
            set { if (_active == value) return; _active = value; Invalidate(); }
        }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        [AllowNull]
        public override string Text
        {
            get => base.Text;
            set
            {
                value ??= "";
                if (base.Text == value) return;
                base.Text = value;
                Invalidate();
            }
        }

        public int PreferredWidth => TextRenderer.MeasureText(Text, Font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding).Width + Ui.Px(24);

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent?.BackColor ?? ObsTheme.BaseBg);

            var rect = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
            using (var path = Ui.RoundedRect(rect, rect.Height / 2f))
            using (var brush = new SolidBrush(_active ? ObsTheme.ChipActiveBg : ObsTheme.ChipBg))
            {
                g.FillPath(brush, path);
            }

            TextRenderer.DrawText(g, Text, Font, new Rectangle(0, 0, Width, Height), _active ? ObsTheme.ChipActiveText : ObsTheme.ChipText,
                TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.HorizontalCenter
                | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
    }

    /// <summary>
    /// Фраза-сводка с переносом по словам. В тексте части, обрамлённые «**», рисуются жирным:
    /// «Когда **Микрофон** громче **-35 дБ**...»
    /// </summary>
    internal sealed class SummaryText : Control
    {
        private readonly List<(string Word, bool Bold)> _words = new List<(string, bool)>();
        private string _markup = "";
        private readonly Font _bold;

        public SummaryText()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw, true);
            Font = Ui.Font();
            _bold = Ui.Font(FontStyle.Bold);
            TabStop = false;
            AccessibleRole = AccessibleRole.StaticText;
        }

        /// <summary>Текст со вставками **жирного**; пустая строка очищает</summary>
        public void SetMarkup(string markup)
        {
            markup ??= "";
            if (_markup == markup) return;
            _markup = markup;
            AccessibleName = markup.Replace("**", "");

            _words.Clear();
            bool bold = false;
            foreach (var part in markup.Split("**"))
            {
                foreach (var word in part.Split(' ', StringSplitOptions.RemoveEmptyEntries)) _words.Add((word, bold));
                bold = !bold;
            }
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent?.BackColor ?? ObsTheme.BaseBg);

            const TextFormatFlags flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.Left;
            var unlimited = new Size(int.MaxValue, int.MaxValue);
            int space = TextRenderer.MeasureText(g, "a a", Font, unlimited, flags).Width - 2 * TextRenderer.MeasureText(g, "a", Font, unlimited, flags).Width;
            int lineH = (int)Math.Ceiling(Font.GetHeight(g) * 1.35f);

            int x = 0, y = 0;
            for (int i = 0; i < _words.Count; i++)
            {
                var (word, bold) = _words[i];
                var font = bold ? _bold : Font;
                int w = TextRenderer.MeasureText(g, word, font, unlimited, flags).Width;

                if (x > 0 && x + w > Width)
                {
                    x = 0;
                    y += lineH;
                }

                TextRenderer.DrawText(g, word, font, new Point(x, y), bold ? ObsTheme.Text : ObsTheme.TextMuted, flags);
                x += w;

                // пробел после слова, кроме случая, когда следующее слово начинается со знака препинания
                bool nextAttached = i + 1 < _words.Count && ",.;:!?)".Contains(_words[i + 1].Word[0]);
                if (!nextAttached) x += space;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _bold.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>Короткий список «подпись: значение» рядом с индикатором: текущие значения ползунков словами и цифрами</summary>
    internal sealed class ReadoutList : Control
    {
        private readonly List<(string Label, string Value)> _rows = new List<(string, string)>();

        public ReadoutList()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw, true);
            Font = Ui.Font();
            TabStop = false;
            AccessibleRole = AccessibleRole.Table;
        }

        public void SetRows(params (string Label, string Value)[] rows)
        {
            bool same = rows.Length == _rows.Count;
            for (int i = 0; same && i < rows.Length; i++) same = rows[i] == _rows[i];
            if (same) return;

            _rows.Clear();
            _rows.AddRange(rows);
            AccessibleName = string.Join(", ", rows.Select(r => r.Label + " " + r.Value));
            Invalidate();
        }

        public int RowHeight => Ui.Px(24);

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent?.BackColor ?? ObsTheme.BaseBg);

            const TextFormatFlags common = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis;
            using var bold = Ui.Font(FontStyle.Bold);
            int y = 0;
            foreach (var (label, value) in _rows)
            {
                var row = new Rectangle(0, y, Width, RowHeight);
                int valueW = Math.Min(Width / 2, TextRenderer.MeasureText(g, value, bold, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding).Width + Ui.Px(2));
                TextRenderer.DrawText(g, label, Font, new Rectangle(0, y, Width - valueW - Ui.Px(6), RowHeight), ObsTheme.TextMuted, common | TextFormatFlags.Left);
                TextRenderer.DrawText(g, value, bold, new Rectangle(Width - valueW, y, valueW, RowHeight), ObsTheme.Text, common | TextFormatFlags.Right);
                y += RowHeight;
            }
        }
    }
}
