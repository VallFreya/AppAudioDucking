using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;

namespace ProcessMuterByChannel
{
    internal enum LevelScale
    {
        /// <summary>Шкала в дБ от -60 до 0, с подписями и числом, как в микшере OBS</summary>
        Db,
        /// <summary>Шкала в процентах от 0 до 100, без цифр</summary>
        Percent
    }

    /// <summary>
    /// Вертикальный индикатор уровня с ползунками, как в микшере OBS. Слева ползунок (активный задаёт значение настройки,
    /// второй, полупрозрачный и недоступный, показывает текущую громкость источника в OBS), справа цветной индикатор
    /// живого уровня и шкала. Над индикатором (для шкалы в дБ) поле, в которое значение можно ввести числом.
    ///
    /// В режиме ShowReturn ползунков два: правый задаёт, до какого уровня приглушать (Value), левый - до какого уровня
    /// возвращать звук (ReturnValue). Оба двигаются по всей шкале, но возврат не бывает ниже приглушения: при подъёме
    /// приглушения возврат подтягивается за ним. Двойной щелчок по ползунку возврата сбрасывает его на «до исходной».
    /// </summary>
    internal sealed class LevelSlider : Control
    {
        // Цвета индикатора и пороги зон как в OBS (одинаковые во всех темах)
        private static readonly Color NominalBg = Color.FromArgb(0x1E, 0x85, 0x28);
        private static readonly Color WarningBg = Color.FromArgb(0x92, 0x6E, 0x11);
        private static readonly Color ErrorBg = Color.FromArgb(0xA1, 0x17, 0x2E);
        private static readonly Color NominalFg = Color.FromArgb(0x59, 0xD9, 0x66);
        private static readonly Color WarningFg = Color.FromArgb(0xEA, 0xBC, 0x48);
        private static readonly Color ErrorFg = Color.FromArgb(0xE8, 0x5E, 0x75);
        private const float WarningFraction = 40.0f / 60.0f; // -20 дБ на шкале -60..0
        private const float ErrorFraction = 51.0f / 60.0f;   // -9 дБ

        private const float DbMin = -60.0f;
        private const float DecayPerSecondDb = 20.0f;
        private const float DecayPerSecondPercent = 60.0f;
        private const long PeakHoldMs = 1200;

        private enum Fader { Duck, Return }

        private readonly ThemedTextBox _box;
        private readonly ThemedTextBox _boxReturn;
        private readonly Font _tickFont;
        private readonly Font _statusFont;
        private readonly Stopwatch _clock = Stopwatch.StartNew();

        private LevelScale _scale = LevelScale.Db;
        private float _value;                 // левый ползунок (порог или уровень приглушения)
        private float _returnValue;           // правый ползунок; верхний край шкалы значит «до исходной»
        private float? _ghost;
        private bool _showReturn;
        private float? _current;
        private bool _dimmed;
        private bool _showLine;
        private string _statusText = "";
        private Fader? _dragging;
        private Fader _active = Fader.Duck;
        private bool _updatingBox;

        private float _level;
        private float _peak;
        private long _lastLevelMs;
        private long _peakSetMs;

        /// <summary>Пользователь изменил левый ползунок (мышью, клавишами, колесом или числом)</summary>
        public event EventHandler? ValueChanged;

        /// <summary>Пользователь изменил правый ползунок</summary>
        public event EventHandler? ReturnChanged;

        /// <summary>Двойной щелчок по ползунку возврата: вернуть режим «до исходной громкости»</summary>
        public event EventHandler? ReturnReset;

        /// <summary>Ползунок взяли мышью (даже без движения): true - ползунок возврата, false - приглушения</summary>
        public event Action<bool>? GrabStarted;

        /// <summary>Ползунок отпустили</summary>
        public event EventHandler? GrabEnded;

        /// <summary>Подписи под ползунками: короткое слово, что делает ползунок</summary>
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public string DuckLabel { get; set; } = "";

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public string ReturnLabel { get; set; } = "";

        /// <summary>
        /// Текущая громкость цели в единицах шкалы. Рисуется чертой поперёк цветной шкалы (там же, где бежит живой уровень звука),
        /// мышью не двигается. null - неизвестна, черты нет
        /// </summary>
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public float? CurrentValue
        {
            get => _current;
            set
            {
                float? clamped = value.HasValue ? Clamp(value.Value) : null;
                if (_current == clamped || (_current.HasValue && clamped.HasValue && Math.Abs(_current.Value - clamped.Value) < 0.05f)) return;
                _current = clamped;
                Invalidate();
            }
        }

        public LevelSlider()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw | ControlStyles.Selectable | ControlStyles.StandardDoubleClick, true);
            TabStop = true;
            Font = Ui.Font();
            AccessibleRole = AccessibleRole.Slider;
            _tickFont = Ui.Font(scale: 0.82f);
            _statusFont = Ui.Font(scale: 0.92f);
            Size = new Size(Ui.Px(100), Ui.Px(286)); // снизу подписи ползунков и три строки пояснения

            _box = MakeBox(Fader.Duck);
            _boxReturn = MakeBox(Fader.Return);
            _boxReturn.Visible = false;

            _returnValue = Maximum;
            _level = _peak = Minimum;
            LayoutBoxes();
            UpdateBoxes();
        }

        private ThemedTextBox MakeBox(Fader fader)
        {
            var box = new ThemedTextBox { Height = Ui.Px(28) };
            box.InnerTextBox.TextAlign = HorizontalAlignment.Center;
            box.InnerTextBox.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter)
                {
                    CommitBox(fader);
                    e.Handled = e.SuppressKeyPress = true;
                }
            };
            box.InnerTextBox.Leave += (s, e) => CommitBox(fader);
            Controls.Add(box);
            return box;
        }

        // --- настройки ---

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public LevelScale Mode
        {
            get => _scale;
            set
            {
                if (_scale == value) return;
                _scale = value;
                _level = _peak = Minimum;
                UpdateBoxes();
                Invalidate();
            }
        }

        /// <summary>Положение левого ползунка: дБ (-60..0) или проценты (0..100)</summary>
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public float Value
        {
            get => _value;
            set
            {
                value = Clamp(value);
                if (Math.Abs(value - _value) < 0.0001f) return;
                _value = value;
                UpdateBoxes();
                Invalidate();
            }
        }

        /// <summary>Правый ползунок: до какого уровня возвращать. Верхний край шкалы значит «до исходной громкости»</summary>
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public float ReturnValue
        {
            get => _returnValue;
            set
            {
                value = Clamp(value);
                if (Math.Abs(value - _returnValue) < 0.0001f) return;
                _returnValue = value;
                UpdateBoxes();
                Invalidate();
            }
        }

        /// <summary>Показывать второй (правый) ползунок</summary>
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public bool ShowReturn
        {
            get => _showReturn;
            set
            {
                if (_showReturn == value) return;
                _showReturn = value;
                LayoutBoxes();
                UpdateBoxes();
                Invalidate();
            }
        }

        /// <summary>Ширина контрола, которая нужна для выбранного набора ползунков</summary>
        public int PreferredWidth => Ui.Px(_showReturn ? 160 : 100);

        /// <summary>Положение второго, полупрозрачного ползунка (текущая громкость источника в OBS). null - не показывать</summary>
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public float? GhostValue
        {
            get => _ghost;
            set
            {
                float? clamped = value.HasValue ? Clamp(value.Value) : null;
                if (_ghost == clamped || (_ghost.HasValue && clamped.HasValue && Math.Abs(_ghost.Value - clamped.Value) < 0.05f)) return;
                _ghost = clamped;
                Invalidate();
            }
        }

        /// <summary>Поле для ввода значения числом над индикатором (только для шкалы в дБ)</summary>
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public bool ShowValueBox
        {
            get => _box.Visible;
            set
            {
                _box.Visible = value;
                _boxReturn.Visible = value && _showReturn;
                Invalidate();
            }
        }

        /// <summary>Линия поперёк индикатора на уровне левого ползунка (для порога)</summary>
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public bool ShowLine
        {
            get => _showLine;
            set { _showLine = value; Invalidate(); }
        }

        /// <summary>Серый индикатор: источник не выбран или не активен. Живой уровень при этом не рисуется</summary>
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public bool Dimmed
        {
            get => _dimmed;
            set
            {
                if (_dimmed == value) return;
                _dimmed = value;
                if (value) _level = _peak = Minimum;
                Invalidate();
            }
        }

        /// <summary>Пояснение под индикатором, например «Сцена неактивна»</summary>
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public string StatusText
        {
            get => _statusText;
            set
            {
                value ??= "";
                if (_statusText == value) return;
                _statusText = value;
                Invalidate();
            }
        }

        private float Minimum => _scale == LevelScale.Db ? DbMin : 0.0f;
        private float Maximum => _scale == LevelScale.Db ? 0.0f : 100.0f;
        private float Step => _scale == LevelScale.Db ? 0.5f : 1.0f;
        private float Clamp(float v) => float.IsFinite(v) ? Math.Clamp(v, Minimum, Maximum) : Minimum;

        /// <summary>Где ползунок возврата на самом деле: свободно по всей шкале, но не ниже ползунка приглушения (он подтягивается за ним)</summary>
        public float ReturnEffective => Math.Max(_value, _returnValue);

        // --- живой уровень ---

        /// <summary>Передаёт текущий уровень (дБ или проценты). Растёт сразу, падает плавно, пик удерживается около секунды</summary>
        public void SetLevel(float level)
        {
            long now = _clock.ElapsedMilliseconds;
            float dt = Math.Clamp((now - _lastLevelMs) / 1000.0f, 0.0f, 0.5f);
            _lastLevelMs = now;
            float decay = (_scale == LevelScale.Db ? DecayPerSecondDb : DecayPerSecondPercent) * dt;

            level = _dimmed ? Minimum : Clamp(level);
            float oldLevel = _level, oldPeak = _peak;

            _level = level >= _level ? level : Math.Max(level, _level - decay);

            if (level >= _peak)
            {
                _peak = level;
                _peakSetMs = now;
            }
            else if (now - _peakSetMs > PeakHoldMs)
            {
                _peak = Math.Max(_level, _peak - decay);
            }

            if (Math.Abs(_level - oldLevel) > 0.05f || Math.Abs(_peak - oldPeak) > 0.05f) Invalidate();
        }

        // --- числовые поля ---

        private void LayoutBoxes()
        {
            if (_showReturn)
            {
                // слева поле возврата, справа поле приглушения: так же, как стоят ползунки
                int w = (Width - Ui.Px(6)) / 2;
                _boxReturn.SetBounds(0, 0, w, Ui.Px(28));
                _box.SetBounds(Width - w, 0, w, Ui.Px(28));
            }
            else
            {
                _box.SetBounds(Ui.Px(6), 0, Width - Ui.Px(12), Ui.Px(28));
            }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (_boxReturn != null) LayoutBoxes();
        }

        private string Format(float db) => db.ToString("0.0", CultureInfo.InvariantCulture);

        private void UpdateBoxes()
        {
            _updatingBox = true;
            try
            {
                if (_scale != LevelScale.Db)
                {
                    _box.Text = "";
                    _boxReturn.Text = "";
                    return;
                }

                // с двумя полями единицу измерения не пишем: места мало
                _box.Text = _showReturn ? Format(_value) : Format(_value) + " dB";
                _boxReturn.Text = Format(ReturnEffective);
            }
            finally
            {
                _updatingBox = false;
            }
        }

        private void CommitBox(Fader fader)
        {
            if (_updatingBox || _scale != LevelScale.Db) return;

            string text = (fader == Fader.Duck ? _box : _boxReturn).Text.Trim();
            foreach (var unit in new[] { "dB", "дБ", "db" })
            {
                if (text.EndsWith(unit, StringComparison.OrdinalIgnoreCase)) text = text.Substring(0, text.Length - unit.Length).Trim();
            }

            if (float.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed) && float.IsFinite(parsed))
            {
                SetByUser(fader, parsed, snap: false);
            }
            else
            {
                UpdateBoxes(); // непонятный текст: возвращаем прежнее значение
            }
        }

        // Пользователь двигает ползунок: значение ограничивается соседом и исходной громкостью
        private void SetByUser(Fader fader, float value, bool snap)
        {
            value = Clamp(value);
            value = snap ? MathF.Round(value / Step) * Step : MathF.Round(value * 10.0f) / 10.0f;
            _active = fader;

            if (fader == Fader.Duck || !_showReturn)
            {
                value = Math.Clamp(value, Minimum, Maximum);   // уровень приглушения свободен: в любое значение шкалы
                bool changed = Math.Abs(value - _value) > 0.0001f;
                _value = value;
                // подъём толкает возврат вверх насовсем: при опускании он остаётся там, где его оставили, а не ползёт вниз следом
                if (_showReturn && _returnValue < value) _returnValue = value;
                UpdateBoxes();
                Invalidate();
                if (changed) ValueChanged?.Invoke(this, EventArgs.Empty);
                return;
            }

            // возврат можно поднять в любое значение, но ниже приглушения он опуститься не может
            value = Math.Clamp(value, _value, Maximum);
            float before = ReturnEffective;
            _returnValue = value;
            UpdateBoxes();
            Invalidate();
            if (Math.Abs(ReturnEffective - before) > 0.0001f) ReturnChanged?.Invoke(this, EventArgs.Empty);
        }

        // --- геометрия ---

        private int BarTop => Ui.Px(36);
        private int BarHeight => Ui.Px(170);
        // С двумя ползунками слева возврат, справа приглушение (ближе к индикатору), между ними место под подписи
        private int DuckX => Ui.Px(_showReturn ? 112 : 24);
        private int ReturnX => Ui.Px(34);
        private int BarX => Ui.Px(_showReturn ? 140 : 40);
        private int BarWidth => Ui.Px(12);

        private int FaderX(Fader fader) => fader == Fader.Duck ? DuckX : ReturnX;

        private float ValueToY(float value)
        {
            float t = (Clamp(value) - Minimum) / (Maximum - Minimum);
            return BarTop + (1.0f - t) * BarHeight;
        }

        private float YToValue(int y)
        {
            float t = 1.0f - (y - BarTop) / (float)BarHeight;
            return Minimum + Math.Clamp(t, 0.0f, 1.0f) * (Maximum - Minimum);
        }

        // --- рисование ---

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent?.BackColor ?? ObsTheme.BaseBg);

            bool gray = _dimmed || !Enabled;
            DrawBar(g, gray);
            if (_scale == LevelScale.Db) DrawScale(g);
            DrawFader(g, Fader.Duck, gray);
            if (_showReturn) DrawFader(g, Fader.Return, gray);
            DrawCurrent(g, gray);
            DrawFaderLabels(g, gray);
            DrawStatus(g);
        }

        private void DrawBar(Graphics g, bool gray)
        {
            int x = BarX, w = BarWidth, top = BarTop, bottom = BarTop + BarHeight;

            // зоны индикатора: обычная, предупреждение, перегрузка (границы как в OBS)
            float[] edges = { 0.0f, WarningFraction, ErrorFraction, 1.0f };
            Color[] bgs = { NominalBg, WarningBg, ErrorBg };
            Color[] fgs = { NominalFg, WarningFg, ErrorFg };
            float levelT = (_level - Minimum) / (Maximum - Minimum);

            for (int zone = 0; zone < 3; zone++)
            {
                float yTop = bottom - edges[zone + 1] * BarHeight;
                float yBottom = bottom - edges[zone] * BarHeight;

                using (var brush = new SolidBrush(gray ? ObsTheme.InputBg : bgs[zone]))
                {
                    g.FillRectangle(brush, x, yTop, w, yBottom - yTop);
                }

                if (gray || levelT <= edges[zone]) continue;

                float fillTop = bottom - Math.Min(levelT, edges[zone + 1]) * BarHeight;
                using var fg = new SolidBrush(fgs[zone]);
                g.FillRectangle(fg, x, fillTop, w, yBottom - fillTop);
            }

            // удерживаемый пик
            if (!gray && _peak > Minimum + 0.5f)
            {
                float peakT = (_peak - Minimum) / (Maximum - Minimum);
                int zone = peakT >= ErrorFraction ? 2 : peakT >= WarningFraction ? 1 : 0;
                float y = bottom - peakT * BarHeight;
                using var pen = new Pen(fgs[zone], Math.Max(1.0f, Ui.Px(2)));
                g.DrawLine(pen, x, y, x + w, y);
            }

            // линия порога поперёк индикатора
            if (_showLine)
            {
                float y = ValueToY(_value);
                using var pen = new Pen(Color.FromArgb(gray ? 120 : 230, ObsTheme.Text), Math.Max(1.0f, Ui.Px(1.5f)));
                g.DrawLine(pen, x - Ui.Px(3), y, x + w + Ui.Px(3), y);
            }
        }

        private void DrawScale(Graphics g)
        {
            var color = ObsTheme.TextMuted;
            for (int db = 0; db >= (int)DbMin; db -= 6)
            {
                float y = ValueToY(db);
                using (var pen = new Pen(Color.FromArgb(160, color), 1.0f))
                {
                    g.DrawLine(pen, BarX + BarWidth + Ui.Px(2), y, BarX + BarWidth + Ui.Px(6), y);
                }

                var size = TextRenderer.MeasureText(g, db.ToString(CultureInfo.InvariantCulture), _tickFont);
                // верхняя и нижняя подписи сдвинуты внутрь, чтобы не вылезать за индикатор
                float textY = Math.Clamp(y - size.Height / 2.0f, BarTop - 1, BarTop + BarHeight - size.Height + 1);
                TextRenderer.DrawText(g, db.ToString(CultureInfo.InvariantCulture), _tickFont,
                    new Point(BarX + BarWidth + Ui.Px(8), (int)textY), color,
                    TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.Left);
            }
        }

        private void DrawFader(Graphics g, Fader fader, bool gray)
        {
            int cx = FaderX(fader);
            float trackW = Math.Max(2.0f, Ui.Px(3));
            bool isReturn = fader == Fader.Return;
            float valueY = ValueToY(isReturn ? ReturnEffective : _value);

            // дорожка
            using (var track = new SolidBrush(Color.FromArgb(gray ? 90 : 150, ObsTheme.TextMuted)))
            {
                g.FillRectangle(track, cx - trackW / 2, BarTop, trackW, BarHeight);
            }

            // закрашенная часть под ползунком: у возврата светлее, у приглушения основной цвет
            if (!gray)
            {
                using var fill = new SolidBrush(isReturn ? ObsTheme.PrimaryLight : ObsTheme.Primary);
                g.FillRectangle(fill, cx - trackW / 2, valueY, trackW, BarTop + BarHeight - valueY);
            }

            // полупрозрачный ползунок: только у ползунка порога, текущая громкость источника в OBS
            float? ghost = isReturn ? null : _ghost;
            if (ghost.HasValue)
            {
                DrawHandle(g, cx, ValueToY(ghost.Value), alpha: gray ? 50 : 95, focused: false, active: false);
            }

            DrawHandle(g, cx, valueY, alpha: gray ? 130 : 255, focused: (Focused && _active == fader) || _dragging == fader, active: true);
        }

        private void DrawHandle(Graphics g, int cx, float y, int alpha, bool focused, bool active)
        {
            int w = Ui.Px(22), h = Ui.Px(11);
            var rect = new RectangleF(cx - w / 2.0f, y - h / 2.0f, w, h);
            Color fill = ObsTheme.IsDark ? Color.FromArgb(0xE1, 0xE1, 0xE1) : Color.FromArgb(0xFE, 0xFE, 0xFE);
            Color border = focused ? ObsTheme.FocusRing : (ObsTheme.IsDark ? Color.FromArgb(0x8A, 0x90, 0xA0) : Color.FromArgb(0x82, 0x82, 0x82));

            using var path = Ui.RoundedRect(rect, Ui.Px(2.5f));
            using var brush = new SolidBrush(Color.FromArgb(alpha, fill));
            using var pen = new Pen(Color.FromArgb(active ? alpha : Math.Min(255, alpha + 40), border), focused ? 2.0f : 1.0f);
            g.FillPath(brush, path);
            g.DrawPath(pen, path);
        }

        // Текущая громкость цели: чёрта поперёк цветной шкалы, на которой бежит живой уровень звука, и маленький указатель слева.
        // Мышью её не двигают (это показание, а не ручка). Тёмная подложка под чертой нужна, чтобы она читалась на зелёном, жёлтом и красном
        private void DrawCurrent(Graphics g, bool gray)
        {
            if (!_current.HasValue) return;

            float y = ValueToY(_current.Value);
            float x0 = BarX - Ui.Px(4), x1 = BarX + BarWidth + Ui.Px(4);
            int alpha = gray ? 120 : 255;

            using (var shade = new Pen(Color.FromArgb(alpha * 2 / 3, ObsTheme.IsDark ? Color.Black : Color.White), Math.Max(4.0f, Ui.Px(4))))
            using (var line = new Pen(Color.FromArgb(alpha, ObsTheme.IsDark ? Color.White : Color.Black), Math.Max(2.0f, Ui.Px(2))))
            {
                g.DrawLine(shade, x0, y, x1, y);
                g.DrawLine(line, x0, y, x1, y);
            }

            // указатель слева от шкалы, смотрит на черту
            float tip = BarX - Ui.Px(5), h = Ui.Px(4.5f), w = Ui.Px(7);
            using var arrow = new SolidBrush(Color.FromArgb(alpha, ObsTheme.Text));
            g.FillPolygon(arrow, new[] { new PointF(tip - w, y - h), new PointF(tip, y), new PointF(tip - w, y + h) });
        }

        // Подпись под каждым ползунком, по центру его дорожки
        private void DrawFaderLabels(Graphics g, bool gray)
        {
            int y = BarTop + BarHeight + Ui.Px(6);
            var color = gray ? Color.FromArgb(150, ObsTheme.TextMuted) : ObsTheme.Text;
            const TextFormatFlags flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine
                | TextFormatFlags.HorizontalCenter | TextFormatFlags.Top;

            void Draw(string text, int centerX)
            {
                if (text.Length == 0) return;
                int w = TextRenderer.MeasureText(g, text, _statusFont, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding).Width + Ui.Px(2);
                // подпись по центру своей дорожки, но не за краем контрола: у крайнего ползунка её сдвигаем внутрь, чтобы не обрезалась
                int x = Math.Clamp(centerX - w / 2, 0, Math.Max(0, Width - w));
                TextRenderer.DrawText(g, text, _statusFont, new Rectangle(x, y, w, Ui.Px(20)), color, flags);
            }

            Draw(DuckLabel, DuckX);
            if (_showReturn) Draw(ReturnLabel, ReturnX);
        }

        private void DrawStatus(Graphics g)
        {
            if (_statusText.Length == 0) return;

            var rect = new Rectangle(0, BarTop + BarHeight + Ui.Px(24), Width, Height - (BarTop + BarHeight + Ui.Px(24)));
            TextRenderer.DrawText(g, _statusText, _statusFont, rect, ObsTheme.TextMuted,
                TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak | TextFormatFlags.HorizontalCenter | TextFormatFlags.Top);
        }

        // --- ввод ---

        // Какой ползунок под курсором. С одним ползунком - всегда левый
        private Fader? FaderAt(Point p)
        {
            if (p.Y < BarTop - Ui.Px(8) || p.Y > BarTop + BarHeight + Ui.Px(8)) return null;

            if (!_showReturn) return p.X <= BarX + BarWidth + Ui.Px(6) ? Fader.Duck : null;

            if (p.X > BarX - Ui.Px(4)) return null;
            return Math.Abs(p.X - DuckX) <= Math.Abs(p.X - ReturnX) ? Fader.Duck : Fader.Return;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left || !Enabled) return;

            var fader = FaderAt(e.Location);
            if (fader == null) return;

            Focus();
            _dragging = fader;
            SetByUser(fader.Value, YToValue(e.Y), snap: true);
            GrabStarted?.Invoke(fader.Value == Fader.Return);
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (_showReturn && Enabled && FaderAt(e.Location) == Fader.Return) ReturnReset?.Invoke(this, EventArgs.Empty);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_dragging.HasValue) SetByUser(_dragging.Value, YToValue(e.Y), snap: true);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            EndGrab();
        }

        // Мышь перехватили, не отпустив кнопку (например, переключили окно): считаем, что ползунок отпущен
        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            if (_dragging.HasValue && !Capture) EndGrab();
        }

        private void EndGrab()
        {
            if (!_dragging.HasValue) return;

            _dragging = null;
            Invalidate();
            GrabEnded?.Invoke(this, EventArgs.Empty);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (!Enabled) return;

            var fader = _showReturn ? (FaderAt(e.Location) ?? _active) : Fader.Duck;
            float current = fader == Fader.Duck ? _value : ReturnEffective;
            SetByUser(fader, current + Math.Sign(e.Delta) * Step * 2, snap: true);
        }

        protected override bool IsInputKey(Keys keyData)
        {
            switch (keyData & Keys.KeyCode)
            {
                case Keys.Up:
                case Keys.Down:
                case Keys.Left:
                case Keys.Right:
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
            // стрелки влево и вправо переключают ползунок, остальные клавиши двигают выбранный
            if (_showReturn && (e.KeyCode == Keys.Left || e.KeyCode == Keys.Right))
            {
                _active = e.KeyCode == Keys.Left ? Fader.Return : Fader.Duck;
                Invalidate();
                e.Handled = true;
                base.OnKeyDown(e);
                return;
            }

            float current = _active == Fader.Duck || !_showReturn ? _value : ReturnEffective;
            float? next = e.KeyCode switch
            {
                Keys.Up => current + Step,
                Keys.Down => current - Step,
                Keys.PageUp => current + Step * 10,
                Keys.PageDown => current - Step * 10,
                Keys.Home => Maximum,
                Keys.End => Minimum,
                _ => null
            };

            if (next.HasValue)
            {
                SetByUser(_showReturn ? _active : Fader.Duck, next.Value, snap: true);
                e.Handled = true;
            }
            base.OnKeyDown(e);
        }

        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _tickFont.Dispose();
                _statusFont.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
