using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Text.Json;
using System.Windows.Forms;

namespace ProcessMuterByChannel
{
    /// <summary>
    /// Окно настроек: список фильтров. Фильтр состоит из трёх блоков: что отслеживать (сцена, источник, порог),
    /// что приглушать (процесс Windows или источник OBS, уровень приглушения) и время (затухание, задержка возврата).
    /// Индикаторы показывают живой уровень звука. Внешний вид повторяет тему OBS (см. ObsTheme).
    /// </summary>
    internal sealed class SettingsForm : Form
    {
        // Размеры карточки для 96 dpi: три блока в ряд, если места хватает, иначе блоки переносятся ниже
        private const int BlockWidth = 392;
        private const int BlockGap = 24;
        private const int CardPad = 16;
        private const int MaxBlockGap = 60;
        private const int SaveDelayMs = 500;

        // Эти элементы пересоздаются при смене темы (см. BuildUi)
        private FlowLayoutPanel _list = null!;
        private Label _emptyLabel = null!;
        private ThemedButton _addBottom = null!;
        private Label _statusLabel = null!;
        private System.Windows.Forms.Timer _statusTimer = null!;
        private ToolTip _tip = null!;
        private readonly List<FilterCard> _cards = new List<FilterCard>();
        private readonly List<SceneItem> _scenes = new List<SceneItem>();
        private readonly ProbeHub _hub = new ProbeHub();
        private readonly System.Windows.Forms.Timer _liveTimer;
        // Автосохранение: правки копятся, и после короткой паузы без изменений фильтры записываются и применяются
        private readonly System.Windows.Forms.Timer _saveTimer;
        private string _savedJson = "";
        private long _lastLiveErrorMs;
        private string[] _processNames = Array.Empty<string>();

        // Элемент списка сцен. Пустое имя означает глобальные источники
        private sealed class SceneItem
        {
            public SceneItem(string name, bool isGlobal) { Name = name; IsGlobal = isGlobal; }

            public string Name { get; }
            public bool IsGlobal { get; }

            public override string ToString() => IsGlobal ? Loc.GlobalSources : Name;
        }

        public SettingsForm(IReadOnlyList<FilterConfig> filters)
        {
            Ui.K = DeviceDpi / 96f;

            AutoScaleMode = AutoScaleMode.None;
            Text = Loc.WindowTitle;
            ShowIcon = false;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;

            // ширина окна под три блока, но не шире экрана; на узком экране блоки перенесутся ниже
            var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1366, 768);
            int scrollbar = SystemInformation.VerticalScrollBarWidth;
            int wide = Ui.Px(CardPad * 2 + BlockWidth * 3 + BlockGap * 2 + 32) + scrollbar;
            ClientSize = new Size(Math.Min(wide, area.Width - Ui.Px(40)), Math.Min(Ui.Px(780), area.Height - Ui.Px(100)));
            MinimumSize = SizeFromClientSize(new Size(Ui.Px(CardPad * 2 + BlockWidth + 32) + scrollbar, Ui.Px(420)));

            LoadScenes();
            ReloadProcessNames();

            // живой уровень в индикаторах
            _liveTimer = new System.Windows.Forms.Timer { Interval = 40 };
            _liveTimer.Tick += (s, e) =>
            {
                // Сбой в одной карточке (например, OBS отдал неожиданный ответ) не должен останавливать остальные
                // и не должен повторяться окном ошибки 25 раз в секунду: пишем в журнал не чаще раза в 10 секунд
                foreach (var card in _cards.ToArray())
                {
                    try
                    {
                        card.UpdateLive();
                    }
                    catch (Exception ex)
                    {
                        long now = Environment.TickCount64;
                        if (now - _lastLiveErrorMs > 10000)
                        {
                            _lastLiveErrorMs = now;
                            Plugin.LogError($"Settings window: live update failed: {ex}");
                        }
                    }
                }
            };

            _saveTimer = new System.Windows.Forms.Timer { Interval = SaveDelayMs };
            _saveTimer.Tick += (s, e) =>
            {
                // пока ползунок зажат, ничего не пишем: громкость цели стоит на уровне ползунка (предпросмотр)
                if (_cards.Exists(c => c.IsGrabbed)) return;
                _saveTimer.Stop();
                SaveNow();
            };

            ObsTheme.Apply(Plugin.ThemeDark);
            BuildUi(filters);
            _savedJson = SerializeFilters(filters);   // что уже лежит в конфиге: по нему отличаем настоящие правки
            _liveTimer.Start();
        }

        // Строит весь интерфейс по текущей теме. Вызывается при открытии окна и при смене темы
        private void BuildUi(IReadOnlyList<FilterConfig> filters)
        {
            BackColor = ObsTheme.WindowBg;
            ForeColor = ObsTheme.Text;
            Font = Ui.Font();

            _tip = ThemedToolTip.Create();

            _list = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = true,
                BackColor = ObsTheme.WindowBg,
                Padding = new Padding(Ui.Px(16), Ui.Px(4), Ui.Px(16), Ui.Px(8))
            };
            var list = _list;
            list.HandleCreated += (s, e) => Ui.ApplyScrollbarTheme(list.Handle);
            list.SizeChanged += (s, e) => ResizeCards();

            _emptyLabel = new Label
            {
                Text = Loc.NoFilters,
                AutoSize = false,
                Width = Ui.Px(400),
                Height = Ui.Px(44),
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = ObsTheme.TextMuted,
                BackColor = ObsTheme.WindowBg
            };
            _list.Controls.Add(_emptyLabel);

            var topBar = new Panel { Dock = DockStyle.Top, Height = Ui.Px(58), BackColor = ObsTheme.WindowBg };
            var btnAdd = new ThemedButton
            {
                Text = Loc.AddFilter,
                Glyph = Ui.GlyphAdd,
                Tone = ButtonTone.Success,
                Bounds = new Rectangle(Ui.Px(16), Ui.Px(14), Ui.Px(210), Ui.Px(32))
            };
            btnAdd.Click += (s, e) => AddNewFilter();
            topBar.Controls.Add(btnAdd);

            // такая же кнопка в конце списка: после прокрутки к последнему фильтру добавлять следующий удобнее оттуда
            _addBottom = new ThemedButton
            {
                Text = Loc.AddFilter,
                Glyph = Ui.GlyphAdd,
                Tone = ButtonTone.Success,
                Size = new Size(Ui.Px(210), Ui.Px(32)),
                Margin = new Padding(0, Ui.Px(4), 0, Ui.Px(8))
            };
            _addBottom.Click += (s, e) => AddNewFilter();
            _list.Controls.Add(_addBottom);

            // выбор темы справа: подпись и список из двух вариантов
            var themeBox = new Panel { Dock = DockStyle.Right, Width = Ui.Px(250), BackColor = ObsTheme.WindowBg };
            themeBox.Controls.Add(new Label
            {
                Text = Loc.Theme,
                Bounds = new Rectangle(0, Ui.Px(14), Ui.Px(70), Ui.Px(32)),
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleRight,
                ForeColor = ObsTheme.TextMuted,
                BackColor = ObsTheme.WindowBg
            });
            var themePicker = new ThemedPicker(editable: false) { Bounds = new Rectangle(Ui.Px(78), Ui.Px(14), Ui.Px(156), Ui.Px(32)) };
            themePicker.SetItems(new object[] { Loc.ThemeDarkName, Loc.ThemeLightName });
            themePicker.SelectedIndex = ObsTheme.IsDark ? 0 : 1;
            themePicker.SelectionChanged += (s, e) =>
            {
                bool dark = themePicker.SelectedIndex == 0;
                // перестраиваем окно уже после того, как закончится обработка клика по списку
                BeginInvoke(new Action(() => SwitchTheme(dark)));
            };
            themeBox.Controls.Add(themePicker);
            topBar.Controls.Add(themeBox);

            var bottomBar = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = Ui.Px(40),
                BackColor = ObsTheme.BaseBg,
                Padding = new Padding(Ui.Px(16), 0, Ui.Px(16), 0)
            };
            // кнопки «Сохранить» нет: всё сохраняется само, а внизу показывается подсказка и короткое «Сохранено»
            _statusLabel = new Label
            {
                Dock = DockStyle.Fill,
                AutoSize = false,
                Text = Loc.AutoSaveHint,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = ObsTheme.TextMuted,
                BackColor = ObsTheme.BaseBg
            };
            bottomBar.Controls.Add(_statusLabel);

            var statusLabel = _statusLabel;
            _statusTimer = new System.Windows.Forms.Timer { Interval = 2500 };
            var statusTimer = _statusTimer;
            statusTimer.Tick += (s, e) =>
            {
                statusTimer.Stop();
                statusLabel.ForeColor = ObsTheme.TextMuted;
                statusLabel.Text = Loc.AutoSaveHint;
            };

            // то же правило docking: Fill первым
            Controls.Add(_list);
            Controls.Add(topBar);
            Controls.Add(bottomBar);

            foreach (var filter in filters) AddCard(filter);
            UpdateEmptyState();
            ResizeCards();
        }

        // Меняет тему: запоминает её в конфиге и пересобирает окно, сохраняя введённые, но ещё не сохранённые значения
        private void SwitchTheme(bool dark)
        {
            if (dark == ObsTheme.IsDark) return;

            FlushSave();   // Plugin.SetTheme пишет в конфиг то, что уже применено: отложенные правки должны попасть туда раньше

            var current = new List<FilterConfig>(_cards.Count);
            foreach (var card in _cards) current.Add(card.ToConfig());

            Plugin.SetTheme(dark);
            ObsTheme.Apply(dark);

            SuspendLayout();
            var old = new List<Control>();
            foreach (Control control in Controls) old.Add(control);
            Controls.Clear();
            foreach (var control in old) control.Dispose();
            _cards.Clear();
            _statusTimer.Dispose();
            _tip.Dispose();

            BuildUi(current);
            ResumeLayout(true);

            Ui.ApplyWindowChrome(Handle);
            Invalidate(true);
        }

        // Карточки занимают всю ширину списка, а число колонок внутри карточки зависит от этой ширины.
        // Место под полосу прокрутки резервируется всегда, чтобы раскладка не прыгала при её появлении
        private void ResizeCards()
        {
            if (_list == null) return;

            int width = CardWidth();

            _emptyLabel.Width = width;
            foreach (var card in _cards)
            {
                if (card.Width != width) card.Width = width;
            }
        }

        private int CardWidth()
        {
            // список растянут на всё окно (Dock.Fill), поэтому берём ширину окна: у только что созданного списка своя ширина ещё не посчитана
            return Math.Max(Ui.Px(CardPad * 2 + BlockWidth),
                ClientSize.Width - _list.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Ui.ApplyWindowChrome(Handle);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            FlushSave();   // правки, сделанные меньше чем за паузу до закрытия, не должны потеряться
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _liveTimer.Dispose();
                _saveTimer.Dispose();
                _statusTimer.Dispose();
                _tip.Dispose();
                foreach (var card in _cards) card.Dispose();
                _cards.Clear();
                _hub.Dispose();
            }
            base.Dispose(disposing);
        }

        private void LoadScenes()
        {
            _scenes.Clear();
            _scenes.Add(new SceneItem("", isGlobal: true));

            try
            {
                foreach (var name in ObsSceneApi.GetSceneNames())
                {
                    _scenes.Add(new SceneItem(name, isGlobal: false));
                }
            }
            catch (Exception ex)
            {
                Plugin.LogError($"Failed to read OBS scenes: {ex.Message}");
            }
        }

        private void ReloadProcessNames()
        {
            try
            {
                _processNames = ProcessAudioControl.GetRunningProcessNames();
            }
            catch (Exception ex)
            {
                Plugin.LogError($"Failed to read process list: {ex.Message}");
            }
        }

        // Порядок Tab: карточки сверху вниз, кнопка «Добавить фильтр» внизу списка последняя.
        // Control.Add выдаёт каждому новому контролу следующий номер, поэтому после вставки номера выставляются заново;
        // иначе при открытии окна фокус (и прокрутка вместе с ним) уходил бы на кнопку в конце списка
        private void FixTabOrder()
        {
            for (int i = 0; i < _cards.Count; i++) _cards[i].TabIndex = i + 1;
            _addBottom.TabIndex = _cards.Count + 1;
        }

        private void AddNewFilter()
        {
            var card = AddCard(new FilterConfig());
            _list.ScrollControlIntoView(card);
            QueueSave();
        }

        private FilterCard AddCard(FilterConfig config, FilterCard? after = null)
        {
            // у нового фильтра (и у фильтра из старого конфига) название по умолчанию: «Фильтр N» с первым свободным номером
            if (string.IsNullOrWhiteSpace(config.name)) config.name = NextDefaultName();

            var card = new FilterCard(this, config);
            card.RemoveRequested += (s, e) =>
            {
                // удаление сразу попадает в конфиг, поэтому спрашиваем подтверждение
                string name = card.FilterName.Length > 0 ? card.FilterName : config.name;
                if (!ThemedDialog.Confirm(this, Loc.ConfirmRemoveTitle, Loc.ConfirmRemoveBody(name), ButtonTone.Danger)) return;

                _cards.Remove(card);
                _list.Controls.Remove(card);
                card.Dispose();
                UpdateEmptyState();
                ResizeCards();
                SaveNow();
            };
            card.DuplicateRequested += (s, e) =>
            {
                var copy = card.ToConfig();
                copy.name = Loc.CopyName(card.FilterName);
                var clone = AddCard(copy, after: card);
                _list.ScrollControlIntoView(clone);
                QueueSave();
            };
            card.Changed += (s, e) => QueueSave();

            // ширина задаётся до добавления в список: иначе карточка с шириной по умолчанию на миг становится одноколоночной
            // (очень высокой), и область прокрутки запоминает эту высоту, оставляя пустое место под последним фильтром
            card.Width = CardWidth();

            // копия встаёт сразу после оригинала, новый фильтр в конец (перед кнопкой «Добавить фильтр» внизу списка)
            int index = after != null ? _cards.IndexOf(after) + 1 : _cards.Count;
            _cards.Insert(index, card);
            _list.Controls.Add(card);
            _list.Controls.SetChildIndex(card, _list.Controls.IndexOf(_emptyLabel) + 1 + index);
            FixTabOrder();
            ResizeCards();
            UpdateEmptyState();
            return card;
        }

        private string NextDefaultName()
        {
            for (int number = 1; ; number++)
            {
                string candidate = Loc.FilterTitle(number);
                if (!_cards.Exists(c => string.Equals(c.FilterName, candidate, StringComparison.OrdinalIgnoreCase))) return candidate;
            }
        }

        private void UpdateEmptyState()
        {
            _emptyLabel.Visible = _cards.Count == 0;
        }

        // Любая правка запускает отсчёт заново: запись идёт после паузы, а не на каждое движение ползунка или нажатие клавиши
        private void QueueSave()
        {
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        // Записывает отложенные правки сразу (перед сменой темы и при закрытии окна)
        private void FlushSave()
        {
            if (!_saveTimer.Enabled) return;
            _saveTimer.Stop();
            SaveNow();
        }

        private List<FilterConfig> CollectFilters()
        {
            var filters = new List<FilterConfig>(_cards.Count);
            foreach (var card in _cards) filters.Add(card.ToConfig());
            return filters;
        }

        private static string SerializeFilters(IReadOnlyList<FilterConfig> filters)
        {
            // Clone приводит значения к допустимым: NaN или бесконечность (их можно получить только из кода) не записываются в JSON
            // и иначе не давали бы окну даже открыться
            var config = new PluginConfig { filters = new List<FilterConfig>(filters.Count) };
            foreach (var filter in filters) config.filters.Add((filter ?? new FilterConfig()).Clone());
            return JsonSerializer.Serialize(config, PluginConfigJsonContext.Default.PluginConfig);
        }

        // Записывает фильтры в конфиг и применяет их. Если ничего не изменилось (например, просто открыли список), конфиг не трогаем
        private void SaveNow()
        {
            // исключение из таймера окна убило бы окно настроек, поэтому ошибку только пишем в журнал
            string json;
            bool written;
            try
            {
                var filters = CollectFilters();
                json = SerializeFilters(filters);
                if (string.Equals(json, _savedJson, StringComparison.Ordinal)) return;

                written = Plugin.SaveConfig(filters);
            }
            catch (Exception ex)
            {
                Plugin.LogError($"Failed to apply filters: {ex.Message}");
                return;
            }

            if (!written)
            {
                // фильтры применены, но на диск не записались (нет прав, диск переполнен): говорим об этом и пробуем снова при следующей правке
                _statusLabel.Text = Loc.SaveFailed;
                _statusLabel.ForeColor = ObsTheme.DangerAccent;
                _statusTimer.Stop();
                return;
            }
            _savedJson = json;

            // окно остаётся открытым, о сохранении сообщаем коротким текстом внизу
            _statusLabel.Text = Loc.Saved;
            _statusLabel.ForeColor = ObsTheme.Text;
            _statusTimer.Stop();
            _statusTimer.Start();
        }

        // Карточка одного фильтра
        private sealed class FilterCard : CardPanel
        {
            private const int RowPitch = 40;
            private const int LabelWidth = 132;
            private const int MeterAreaHeight = 268;   // индикатор, подписи под ползунками и две строки статуса («Сцена неактивна»)

            private readonly SettingsForm _owner;
            private readonly ObsLevelProbeSlot _monitor;
            private readonly ObsLevelProbeSlot _target;
            private readonly ProcessLevelProbe _processProbe = new ProcessLevelProbe();

            // заголовок: переключатель, название, плашка состояния, дублирование и удаление
            private readonly ThemedSwitch _enabled;
            private readonly ThemedTextBox _name;
            private readonly StatusChip _chip;
            private readonly ThemedButton _duplicate;
            private readonly ThemedButton _remove;

            // блок «Что отслеживать»
            private readonly Label _monitorTitle;
            private readonly Label _sceneLabel;
            private readonly ThemedPicker _scene;
            private readonly Label _sourceLabel;
            private readonly ThemedPicker _source;
            private readonly Label _thresholdTitle;
            private readonly Label _thresholdBody;
            private readonly ThemedButton _thresholdHelp;
            private readonly ReadoutList _thresholdReadout;
            private readonly LevelSlider _threshold;

            // блок «Что приглушать»
            private readonly Label _targetTitle;
            private readonly ThemedPicker _kind;
            private readonly Label _processLabel;
            private readonly ThemedPicker _process;
            private readonly ThemedButton _refresh;
            private readonly Label _targetSceneLabel;
            private readonly ThemedPicker _targetScene;
            private readonly Label _targetSourceLabel;
            private readonly ThemedPicker _targetSource;
            private readonly Label _duckTitle;
            private readonly Label _duckBody;
            private readonly ThemedButton _duckHelp;
            private readonly ReadoutList _duckReadout;
            private readonly LevelSlider _duck;

            // блок «Настройки» и под ним фраза-сводка
            private readonly Label _timeTitle;
            private readonly Label _fadeLabel;
            private readonly ThemedTextBox _fade;
            private readonly Label _holdLabel;
            private readonly ThemedTextBox _hold;
            private readonly ThemedButton _holdHelp;
            private readonly Label _summaryTitle;
            private readonly SummaryText _summary;

            // уровень приглушения хранится для обоих видов цели, чтобы при переключении ничего не терялось
            private float _duckDb;
            private float _duckPercent;

            // до какого уровня возвращать звук (верхний край шкалы значит «до исходной громкости»)
            private float _restoreDb;
            private float _restorePercent;
            // false: возвращать до исходной громкости (ползунок возврата сам стоит на ней), true: до уровня, выставленного пользователем
            private bool _restoreCustom;

            // предпросмотр: пока двигают ползунок, громкость цели следует за ним, потом возвращается
            private readonly VolumePreview _preview = new VolumePreview();

            // исходная громкость цели (множитель: у источника OBS, 0..1 у процесса), запомненная, когда цель появилась
            private string _originalKey = "";
            private float? _originalVolume;

            public event EventHandler? RemoveRequested;
            public event EventHandler? DuplicateRequested;

            /// <summary>Пользователь изменил любое значение фильтра: окно сохранит его после короткой паузы</summary>
            public event EventHandler? Changed;

            // зажат ли ползунок мышью: пока да, окно откладывает сохранение (громкость цели стоит на уровне ползунка)
            private bool _thresholdGrabbed;
            private bool _duckGrabbed;

            public bool IsGrabbed => _thresholdGrabbed || _duckGrabbed;

            public string FilterName => _name.Text.Trim();

            private bool TargetsObs => _kind.SelectedIndex == 1;

            public FilterCard(SettingsForm owner, FilterConfig config)
            {
                _owner = owner;
                _monitor = new ObsLevelProbeSlot(owner._hub);
                _target = new ObsLevelProbeSlot(owner._hub);
                Margin = new Padding(0, 0, 0, Ui.Px(12));

                int inputH = Ui.Px(30);

                // заголовок: переключатель, название (плоское поле с карандашом), плашка состояния, дублирование и удаление
                _enabled = new ThemedSwitch { AccessibleName = Loc.SwitchTip };
                _name = new ThemedTextBox { Font = Ui.Font(FontStyle.Bold, 1.1f), Height = inputH, Flat = true, AccessibleName = Loc.EditNameTip };
                _chip = new StatusChip();
                _duplicate = new ThemedButton { Glyph = Ui.GlyphCopy, AccessibleName = Loc.DuplicateFilter };
                _duplicate.Click += (s, e) => DuplicateRequested?.Invoke(this, EventArgs.Empty);
                _remove = new ThemedButton { Glyph = Ui.GlyphDelete, Tone = ButtonTone.Danger, AccessibleName = Loc.RemoveFilter };
                _remove.Click += (s, e) => RemoveRequested?.Invoke(this, EventArgs.Empty);
                owner._tip.SetToolTip(_enabled, Loc.SwitchTip);
                owner._tip.SetToolTip(_name, Loc.EditNameTip);
                owner._tip.SetToolTip(_duplicate, Loc.DuplicateFilter);
                owner._tip.SetToolTip(_remove, Loc.RemoveFilter);

                _monitorTitle = MakeLabel(Loc.MonitorTitle, bold: true);
                _sceneLabel = MakeLabel(Loc.Scene);
                _scene = new ThemedPicker(editable: false) { Height = inputH };
                _sourceLabel = MakeLabel(Loc.AudioSource);
                _source = new ThemedPicker(editable: false) { Height = inputH };
                _thresholdTitle = MakeLabel(Loc.ThresholdTitle, bold: true, color: ObsTheme.Text);
                _thresholdBody = MakeLabel(Loc.ThresholdShort, color: ObsTheme.TextMuted);
                _thresholdHelp = MakeHelpButton(Loc.ThresholdBody);
                _thresholdReadout = new ReadoutList();
                _threshold = new LevelSlider
                {
                    Mode = LevelScale.Db, ShowValueBox = true, ShowLine = true, DuckLabel = Loc.SliderThreshold, AccessibleName = Loc.ThresholdTitle
                };

                _targetTitle = MakeLabel(Loc.TargetTitle, bold: true);
                _kind = new ThemedPicker(editable: false) { Height = inputH };
                _processLabel = MakeLabel(Loc.Process);
                _process = new ThemedPicker(editable: true) { Height = inputH };
                _refresh = new ThemedButton { Glyph = Ui.GlyphRefresh, TabStop = false };
                owner._tip.SetToolTip(_refresh, Loc.RefreshProcesses);
                _targetSceneLabel = MakeLabel(Loc.TargetScene);
                _targetScene = new ThemedPicker(editable: false) { Height = inputH };
                _targetSourceLabel = MakeLabel(Loc.TargetSource);
                _targetSource = new ThemedPicker(editable: false) { Height = inputH };
                _duckTitle = MakeLabel(Loc.DuckTitle, bold: true, color: ObsTheme.Text);
                _duckBody = MakeLabel(Loc.DuckShort, color: ObsTheme.TextMuted);
                _duckHelp = MakeHelpButton("");   // текст подсказки зависит от вида цели, его ставит ApplyDuckMode
                _duckReadout = new ReadoutList();
                _duck = new LevelSlider { DuckLabel = Loc.SliderDuck, ReturnLabel = Loc.SliderReturn, AccessibleName = Loc.DuckTitle };

                _timeTitle = MakeLabel(Loc.TimeTitle, bold: true);
                _fadeLabel = MakeLabel(Loc.Fade);
                _fade = new ThemedTextBox { Height = inputH, Suffix = Loc.MsUnit, AccessibleName = Loc.Fade };
                _holdLabel = MakeLabel(Loc.Hold);
                _hold = new ThemedTextBox { Height = inputH, Suffix = Loc.MsUnit, AccessibleName = Loc.Hold };
                _holdHelp = MakeHelpButton(Loc.HoldHint);
                _summaryTitle = MakeLabel(Loc.SummaryTitle, bold: true);
                _summary = new SummaryText();

                _scene.AccessibleName = Loc.Scene;
                _source.AccessibleName = Loc.AudioSource;
                _kind.AccessibleName = Loc.TargetTitle;
                _process.AccessibleName = Loc.Process;
                _refresh.AccessibleName = Loc.RefreshProcesses;
                _targetScene.AccessibleName = Loc.TargetScene;
                _targetSource.AccessibleName = Loc.TargetSource;

                // порядок добавления задаёт порядок Tab
                foreach (Control control in new Control[]
                {
                    _enabled, _name, _chip, _duplicate, _remove,
                    _monitorTitle, _sceneLabel, _scene, _sourceLabel, _source, _thresholdTitle, _thresholdHelp, _thresholdBody, _thresholdReadout, _threshold,
                    _targetTitle, _kind, _processLabel, _process, _refresh, _targetSceneLabel, _targetScene,
                    _targetSourceLabel, _targetSource, _duckTitle, _duckHelp, _duckBody, _duckReadout, _duck,
                    _timeTitle, _fadeLabel, _fade, _holdLabel, _hold, _holdHelp, _summaryTitle, _summary
                })
                {
                    Controls.Add(control);
                }

                owner._tip.SetToolTip(_holdLabel, Loc.HoldHint);
                owner._tip.SetToolTip(_hold, Loc.HoldHint);

                Fill(config);
                WireEvents();
                WireChanged();
                RefreshInfo();
                Relayout();
            }

            private Label MakeLabel(string text, bool bold = false, Color? color = null)
            {
                return new Label
                {
                    Text = text,
                    AutoSize = false,
                    Font = Ui.Font(bold ? FontStyle.Bold : FontStyle.Regular),
                    TextAlign = ContentAlignment.MiddleLeft,
                    ForeColor = color ?? ObsTheme.Text,
                    BackColor = ObsTheme.BaseBg
                };
            }

            // Круглая кнопка «?» с подсказкой
            private ThemedButton MakeHelpButton(string tip)
            {
                var button = new ThemedButton
                {
                    Text = "?",
                    Round = true,
                    TabStop = false,
                    Font = Ui.Font(FontStyle.Bold, 0.9f),
                    Size = new Size(Ui.Px(24), Ui.Px(24)),
                    AccessibleName = tip.Length > 0 ? tip : Loc.ThresholdBody
                };
                if (tip.Length > 0) _owner._tip.SetToolTip(button, tip);
                return button;
            }

            protected override void OnResize(EventArgs e)
            {
                base.OnResize(e);
                if (_name != null) Relayout();
            }

            // --- раскладка: три блока в ряд, а на узком окне блоки переносятся ниже ---

            private const int ReadoutRows = 3;

            private void Relayout()
            {
                int pad = Ui.Px(CardPad), blockW = Ui.Px(BlockWidth), gap = Ui.Px(BlockGap);
                int rowH = Ui.Px(RowPitch), headerH = Ui.Px(54);
                // заголовок блока и две строки полей, под ними индикатор: у всех трёх блоков одинаковая высота строк
                int blockH = 3 * rowH + Ui.Px(8) + Ui.Px(MeterAreaHeight);
                int timeBlock = 3 * rowH;   // «Настройки»: заголовок и две строки
                int summaryH = Ui.Px(112);

                int cols = Width >= pad * 2 + blockW * 3 + gap * 2 ? 3 : Width >= pad * 2 + blockW * 2 + gap ? 2 : 1;

                // свободная ширина распределяется между колонками, но не бесконечно
                int extra = Math.Max(0, Width - (pad * 2 + blockW * cols + gap * (cols - 1)));
                int spread = cols > 1 ? Math.Min(Ui.Px(MaxBlockGap - BlockGap), extra / (cols - 1)) : 0;
                int step = blockW + gap + spread;

                // сводка занимает свободное место: в три колонки под «Настройками», в две рядом с ними, в одну под ними
                Point a = new Point(pad, headerH), b, c, s;
                int summaryHeight;
                int height;
                if (cols == 3)
                {
                    b = new Point(pad + step, headerH);
                    c = new Point(pad + step * 2, headerH);
                    s = new Point(c.X, c.Y + 3 * rowH + Ui.Px(8));
                    summaryHeight = Ui.Px(MeterAreaHeight);
                    height = headerH + blockH + pad;
                }
                else if (cols == 2)
                {
                    b = new Point(pad + step, headerH);
                    c = new Point(pad, headerH + blockH + Ui.Px(8));
                    s = new Point(b.X, c.Y);
                    summaryHeight = timeBlock;
                    height = c.Y + timeBlock + pad;
                }
                else
                {
                    b = new Point(pad, headerH + blockH + Ui.Px(12));
                    c = new Point(pad, b.Y + blockH + Ui.Px(12));
                    s = new Point(pad, c.Y + timeBlock + Ui.Px(8));
                    summaryHeight = summaryH;
                    height = s.Y + summaryH + pad;
                }

                LayoutHeader();

                PlaceMonitorBlock(a, blockW);
                PlaceTargetBlock(b, blockW);
                PlaceTimeBlock(c, blockW);
                PlaceSummary(s, blockW, summaryHeight);

                if (Height != height) Height = height;
            }

            // Заголовок: переключатель, название (по ширине текста), плашка состояния; справа дублирование и удаление
            private void LayoutHeader()
            {
                int pad = Ui.Px(CardPad);
                int switchW = Ui.Px(40), iconW = Ui.Px(36), gap = Ui.Px(10);

                _duplicate.SetBounds(Width - pad - iconW * 2 - Ui.Px(8), Ui.Px(9), iconW, Ui.Px(30));
                _remove.SetBounds(Width - pad - iconW, Ui.Px(9), iconW, Ui.Px(30));
                _enabled.SetBounds(pad, Ui.Px(13), switchW, Ui.Px(22));

                int chipW = _chip.PreferredWidth;
                int nameX = pad + switchW + gap;
                int room = _duplicate.Left - gap - nameX - gap - chipW;
                int wanted = TextRenderer.MeasureText(_name.Text, _name.Font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding).Width + Ui.Px(52);
                int nameW = Math.Max(Ui.Px(100), Math.Min(Math.Max(wanted, Ui.Px(110)), room));

                _name.SetBounds(nameX, Ui.Px(9), nameW, Ui.Px(30));
                _chip.SetBounds(nameX + nameW + gap, Ui.Px(12), chipW, Ui.Px(24));
            }

            private void PlaceRow(Label label, Control control, Point origin, int row, int blockW, int controlWidth = -1)
            {
                int y = origin.Y + row * Ui.Px(RowPitch);
                int controlX = origin.X + Ui.Px(LabelWidth + 6);
                label.SetBounds(origin.X, y, Ui.Px(LabelWidth), Ui.Px(30));
                control.SetBounds(controlX, y, controlWidth > 0 ? controlWidth : blockW - Ui.Px(LabelWidth + 6), control.Height);
            }

            // Индикатор справа. Слева заголовок со значком «?», одна строка пояснения и список текущих значений
            private void PlaceMeter(Label title, ThemedButton help, Label body, ReadoutList readout, LevelSlider meter, Point origin, int blockW)
            {
                int y = origin.Y + 3 * Ui.Px(RowPitch) + Ui.Px(8);
                int meterW = meter.PreferredWidth;
                int captionW = Math.Max(Ui.Px(120), blockW - meterW - Ui.Px(8));

                int titleW = Math.Min(captionW - Ui.Px(30),
                    TextRenderer.MeasureText(title.Text, title.Font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding).Width + Ui.Px(6));
                title.SetBounds(origin.X, y, titleW, Ui.Px(26));
                help.SetBounds(origin.X + titleW + Ui.Px(4), y + Ui.Px(1), Ui.Px(24), Ui.Px(24));

                body.SetBounds(origin.X, y + Ui.Px(32), captionW, Ui.Px(58));
                body.TextAlign = ContentAlignment.TopLeft;
                readout.SetBounds(origin.X, y + Ui.Px(100), captionW, readout.RowHeight * ReadoutRows);
                meter.SetBounds(origin.X + blockW - meterW, y, meterW, Ui.Px(MeterAreaHeight));
            }

            private void PlaceMonitorBlock(Point o, int blockW)
            {
                _monitorTitle.SetBounds(o.X, o.Y, blockW, Ui.Px(30));
                PlaceRow(_sceneLabel, _scene, o, 1, blockW);
                PlaceRow(_sourceLabel, _source, o, 2, blockW);
                PlaceMeter(_thresholdTitle, _thresholdHelp, _thresholdBody, _thresholdReadout, _threshold, o, blockW);
            }

            private void PlaceTargetBlock(Point o, int blockW)
            {
                bool obs = TargetsObs;
                int fullW = blockW - Ui.Px(LabelWidth + 6);

                // заголовок блока и вид цели на одной строке, поля ниже на тех же строках, что у соседних блоков
                _targetTitle.SetBounds(o.X, o.Y, Ui.Px(LabelWidth), Ui.Px(30));
                _kind.SetBounds(o.X + Ui.Px(LabelWidth + 6), o.Y, fullW, _kind.Height);

                // процесс или пара «сцена и источник» занимают те же строки
                int refreshW = Ui.Px(36);
                PlaceRow(_processLabel, _process, o, 1, blockW, fullW - refreshW - Ui.Px(6));
                _refresh.SetBounds(o.X + blockW - refreshW, o.Y + Ui.Px(RowPitch), refreshW, Ui.Px(30));
                PlaceRow(_targetSceneLabel, _targetScene, o, 1, blockW);
                PlaceRow(_targetSourceLabel, _targetSource, o, 2, blockW);

                _processLabel.Visible = _process.Visible = _refresh.Visible = !obs;
                _targetSceneLabel.Visible = _targetScene.Visible = obs;
                _targetSourceLabel.Visible = _targetSource.Visible = obs;

                PlaceMeter(_duckTitle, _duckHelp, _duckBody, _duckReadout, _duck, o, blockW);
            }

            private void PlaceTimeBlock(Point o, int blockW)
            {
                int numberW = Ui.Px(124);

                // заголовок блока и под ним две строки, на одном уровне с полями соседних блоков; единица «мс» внутри поля
                _timeTitle.SetBounds(o.X, o.Y, blockW, Ui.Px(30));
                PlaceRow(_fadeLabel, _fade, o, 1, blockW, numberW);
                PlaceRow(_holdLabel, _hold, o, 2, blockW, numberW);
                _holdHelp.SetBounds(o.X + Ui.Px(LabelWidth + 6) + numberW + Ui.Px(8), o.Y + 2 * Ui.Px(RowPitch) + Ui.Px(3), Ui.Px(24), Ui.Px(24));
            }

            private void PlaceSummary(Point o, int blockW, int height)
            {
                _summaryTitle.SetBounds(o.X, o.Y, blockW, Ui.Px(26));
                _summary.SetBounds(o.X, o.Y + Ui.Px(30), blockW, Math.Max(Ui.Px(40), height - Ui.Px(30)));
            }

            // --- значения ---

            private void Fill(FilterConfig config)
            {
                _name.Text = config.name;
                _enabled.Checked = config.enabled;

                // сцены для отслеживания и для цели в OBS берутся из одного списка;
                // сцену могли удалить или переименовать, тогда показываем сохранённое значение
                var scenes = new List<SceneItem>(_owner._scenes);
                SceneItem? Pick(string sceneName, string sourceName)
                {
                    // новый фильтр: сцена не выбрана, источник недоступен, пока не выбрана сцена
                    if (sceneName.Length == 0 && sourceName.Length == 0) return null;

                    var item = scenes.Find(s => s.IsGlobal ? sceneName.Length == 0 : string.Equals(s.Name, sceneName, StringComparison.Ordinal));
                    if (item == null)
                    {
                        item = new SceneItem(sceneName, isGlobal: false);
                        scenes.Add(item);
                    }
                    return item;
                }

                var monitoredScene = Pick(config.scene_name, config.source_name);
                var targetScene = Pick(config.target_scene, config.target_source);

                _scene.SetItems(scenes);
                _targetScene.SetItems(scenes);

                if (monitoredScene != null)
                {
                    _scene.SelectedItem = monitoredScene;
                    RefreshSources(config.source_name);
                }
                _source.Enabled = monitoredScene != null;

                _kind.SetItems(new object[] { Loc.TargetKindProcess, Loc.TargetKindObs });
                _kind.SelectedIndex = config.TargetsObsSource ? 1 : 0;

                if (targetScene != null)
                {
                    _targetScene.SelectedItem = targetScene;
                    RefreshTargetSources(config.target_source);
                }
                _targetSource.Enabled = targetScene != null;

                _process.Text = config.process_name;
                ApplyProcessFilter(openDropDown: false);

                _threshold.Value = FilterConfig.ClampDb(config.threshold_db);
                _duckDb = FilterConfig.ClampDb(config.duck_db);
                _duckPercent = float.IsFinite(config.duck_percent) ? Math.Clamp(config.duck_percent, 0.0f, 100.0f) : 0.0f;
                _restoreDb = float.IsFinite(config.restore_db) ? Math.Clamp(config.restore_db, FilterConfig.MinDb, 0.0f) : 0.0f;
                _restorePercent = float.IsFinite(config.restore_percent) ? Math.Clamp(config.restore_percent, 0.0f, 100.0f) : 100.0f;
                _restoreCustom = config.restore_custom;
                ApplyDuckMode();

                _fade.Text = config.fade_ms.ToString(CultureInfo.InvariantCulture);
                _hold.Text = config.hold_ms.ToString(CultureInfo.InvariantCulture);

                RebindProbes();
            }

            // Индикатор уровня приглушения: шкала в дБ для источника OBS и проценты для процесса Windows
            private void ApplyDuckMode()
            {
                bool obs = TargetsObs;
                _preview.Cancel();
                _duck.ShowReturn = true;
                _duck.Mode = obs ? LevelScale.Db : LevelScale.Percent;
                _duck.ShowValueBox = obs;
                _duck.Value = obs ? _duckDb : _duckPercent;
                _duck.ReturnValue = _restoreCustom ? (obs ? _restoreDb : _restorePercent) : OriginalOnScale();
                _owner._tip.SetToolTip(_duckHelp, obs ? Loc.DuckBodyObs : Loc.DuckBodyProcess);
            }

            // --- предпросмотр и исходная громкость цели ---

            // Ключ цели так же, как у фильтра: «obs:Имя» для источника OBS, иначе имя процесса без .exe
            private string CurrentTargetKey()
            {
                if (TargetsObs)
                {
                    return _targetSource.SelectedItem is string name && name.Length > 0 ? ObsSourceBackend.Prefix + name : "";
                }

                string process = _process.Text.Trim();
                if (process.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) process = process.Substring(0, process.Length - 4).Trim();
                return process;
            }

            // Громкость в единицах шкалы -> абсолютная громкость цели (множитель OBS или доля 0..1)
            private float ScaleToVolume(float value) => TargetsObs ? FilterConfig.DbToVolume(value) : value / 100.0f;

            private void StartPreview(float scaleValue, bool isReturn = false)
            {
                string key = CurrentTargetKey();
                if (key.Length == 0) return;
                _preview.SetLevel(key, ScaleToVolume(scaleValue), isReturn);
            }

            private void WireEvents()
            {
                // при смене сцены выбранный источник сбрасывается
                _scene.SelectionChanged += (s, e) =>
                {
                    _source.Enabled = _scene.SelectedItem != null;
                    RefreshSources(null);
                    RebindProbes();
                };
                _source.SelectionChanged += (s, e) => RebindProbes();

                // список источников обновляется при открытии, вдруг в сцену добавили новый
                _source.DropDownOpening += (s, e) => RefreshSources(_source.Text);

                // вид цели: процесс или звуковой источник OBS
                _kind.SelectionChanged += (s, e) =>
                {
                    // для цели в OBS по умолчанию берём ту же сцену, что выбрана для отслеживания
                    if (TargetsObs && _targetScene.SelectedItem == null && _scene.SelectedItem != null)
                    {
                        _targetScene.SelectedItem = _scene.SelectedItem;
                    }
                    ApplyDuckMode();
                    RebindProbes();
                    Relayout();
                };
                _targetScene.SelectionChanged += (s, e) =>
                {
                    _targetSource.Enabled = _targetScene.SelectedItem != null;
                    RefreshTargetSources(null);
                    RebindProbes();
                };
                _targetSource.SelectionChanged += (s, e) =>
                {
                    _preview.Cancel();
                    RebindProbes();
                };
                _targetSource.DropDownOpening += (s, e) => RefreshTargetSources(_targetSource.Text);

                // левый ползунок: до какого уровня приглушать; правый: до какого возвращать. Оба сразу слышны (предпросмотр)
                _duck.ValueChanged += (s, e) =>
                {
                    if (TargetsObs) _duckDb = _duck.Value;
                    else _duckPercent = _duck.Value;
                    StartPreview(_duck.Value);
                };
                _duck.ReturnChanged += (s, e) =>
                {
                    if (TargetsObs) _restoreDb = _duck.ReturnValue;
                    else _restorePercent = _duck.ReturnValue;
                    _restoreCustom = true;   // ползунок тронут: возврат теперь до заданного уровня, а не до исходной громкости
                    StartPreview(_duck.ReturnEffective, isReturn: true);
                };
                // Пока ползунок зажат мышью (даже без движения), громкость цели стоит на его уровне, можно включить музыку
                // и послушать. Отпустили: громкость сразу возвращается, и дальше работает обычное приглушение
                _duck.GrabStarted += isReturn =>
                {
                    StartPreview(isReturn ? _duck.ReturnEffective : _duck.Value, isReturn);
                    _preview.Hold();
                };
                _duck.GrabEnded += (s, e) => _preview.Release();

                // двойной щелчок по ползунку возврата: снова «до исходной громкости»
                _duck.ReturnReset += (s, e) =>
                {
                    _restoreCustom = false;
                    _duck.ReturnValue = OriginalOnScale();
                };

                // список процессов обновляется, когда пользователь переходит к полю, и по кнопке обновления
                _process.FieldEntered += (s, e) =>
                {
                    _owner.ReloadProcessNames();
                    ApplyProcessFilter(openDropDown: false);
                };
                _process.UserTextChanged += (s, e) =>
                {
                    _preview.Cancel();
                    ApplyProcessFilter(openDropDown: _process.Text.Length > 0);
                };
                _process.SelectionChanged += (s, e) => _preview.Cancel();
                _process.DropDownOpening += (s, e) => ApplyProcessFilter(openDropDown: false);
                _refresh.Click += (s, e) =>
                {
                    _owner.ReloadProcessNames();
                    _process.FocusField();
                    _process.MoveCaretToEnd();
                    ApplyProcessFilter(openDropDown: true);
                };
            }

            // Все поля, которые пользователь может менять. Подключается после WireEvents, чтобы к моменту сигнала
            // внутренние поля карточки (уровни, режим) уже были обновлены, и после Fill, чтобы заполнение не считалось правкой
            private void WireChanged()
            {
                void Raise(object? s, EventArgs e)
                {
                    RefreshInfo();
                    Changed?.Invoke(this, EventArgs.Empty);
                }

                // Длина ввода ограничена: бесконечное название ломало бы заголовок и окно подтверждения, а число из
                // десятков цифр всё равно подтянется к границе
                _name.InnerTextBox.MaxLength = 80;
                _fade.InnerTextBox.MaxLength = 6;
                _hold.InnerTextBox.MaxLength = 8;

                // Когда поле покинули, в нём остаётся то число, которое будет сохранено (а не буквы или пустая строка)
                _fade.InnerTextBox.Leave += (s, e) => _fade.Text = FilterConfig.ParseFade(_fade.Text).ToString(CultureInfo.InvariantCulture);
                _hold.InnerTextBox.Leave += (s, e) => _hold.Text = FilterConfig.ParseHold(_hold.Text).ToString(CultureInfo.InvariantCulture);

                _name.TextChanged += (s, e) => LayoutHeader();   // поле названия растёт и сжимается вместе с текстом
                _name.TextChanged += Raise;
                _enabled.CheckedChanged += Raise;
                _scene.SelectionChanged += Raise;
                _source.SelectionChanged += Raise;
                _kind.SelectionChanged += Raise;
                _process.SelectionChanged += Raise;
                _process.UserTextChanged += Raise;
                _targetScene.SelectionChanged += Raise;
                _targetSource.SelectionChanged += Raise;
                _threshold.ValueChanged += Raise;
                _duck.ValueChanged += Raise;
                _duck.ReturnChanged += Raise;
                _duck.ReturnReset += Raise;
                _fade.TextChanged += Raise;
                _hold.TextChanged += Raise;

                _threshold.GrabStarted += isReturn => _thresholdGrabbed = true;
                _threshold.GrabEnded += (s, e) => { _thresholdGrabbed = false; Raise(s, e); };
                _duck.GrabStarted += isReturn => _duckGrabbed = true;
                _duck.GrabEnded += (s, e) => { _duckGrabbed = false; Raise(s, e); };
            }

            private static List<string> ReadSources(SceneItem scene)
            {
                try
                {
                    return ObsSceneApi.GetAudioSourceNames(scene.IsGlobal ? "" : scene.Name);
                }
                catch (Exception ex)
                {
                    Plugin.LogError($"Failed to read audio sources of scene '{scene}': {ex.Message}");
                    return new List<string>();
                }
            }

            private void RefreshSources(string? keep)
            {
                var names = new List<string>();

                if (_scene.SelectedItem is SceneItem scene)
                {
                    names.AddRange(ReadSources(scene));

                    // сохранённого источника может не быть в списке (удалён, переименован, старый конфиг)
                    if (!string.IsNullOrEmpty(keep) && !names.Contains(keep)) names.Add(keep);
                }

                _source.SetItems(names);
                if (!string.IsNullOrEmpty(keep)) _source.SelectedItem = keep;
            }

            private void RefreshTargetSources(string? keep)
            {
                var names = new List<string>();

                if (_targetScene.SelectedItem is SceneItem scene)
                {
                    // источник, за которым следим, глушить нельзя: его громкость влияла бы на измеряемый уровень
                    string monitored = _source.SelectedItem as string ?? "";
                    foreach (var name in ReadSources(scene))
                    {
                        if (!string.Equals(name, monitored, StringComparison.OrdinalIgnoreCase)) names.Add(name);
                    }

                    if (!string.IsNullOrEmpty(keep) && !names.Contains(keep)) names.Add(keep);
                }

                _targetSource.SetItems(names);
                if (!string.IsNullOrEmpty(keep)) _targetSource.SelectedItem = keep;
            }

            // Поиск процесса по мере ввода: показываем только имена, содержащие введённый текст
            private void ApplyProcessFilter(bool openDropDown)
            {
                string text = _process.Text;
                var all = _owner._processNames;

                var matches = new List<object>();
                foreach (var name in all)
                {
                    if (name.Contains(text, StringComparison.OrdinalIgnoreCase)) matches.Add(name);
                }

                // после выбора из списка в поле остаётся ровно одно совпадение: список закрываем,
                // а при следующем открытии показываем все процессы
                bool exactOnly = matches.Count == 1 && string.Equals((string)matches[0], text, StringComparison.OrdinalIgnoreCase);

                _process.SetItems(exactOnly ? new List<object>(all) : matches);

                if (openDropDown && !exactOnly && matches.Count > 0) _process.OpenDropDown(userInitiated: false);
                else if (exactOnly || matches.Count == 0) _process.CloseDropDown();
            }

            // --- живой уровень ---

            private bool IsGlobal(ThemedPicker scenePicker) => scenePicker.SelectedItem is SceneItem { IsGlobal: true };

            // Измерители подключаются к тому, что выбрано в окне (даже если фильтр ещё не сохранён)
            private void RebindProbes()
            {
                _monitor.Bind(_source.SelectedItem as string);
                _target.Bind(TargetsObs ? _targetSource.SelectedItem as string : null);
            }

            /// <summary>Вызывается таймером окна: обновляет индикаторы живым уровнем</summary>
            public void UpdateLive()
            {
                _preview.Tick();
                UpdateLevels();
                UpdateOriginal();
                RefreshInfo();
            }

            // живые значения для списков рядом с индикаторами; null - неизвестно (источник не выбран, не найден или не в эфире)
            private float? _levelDb;
            private float? _obsVolumeDb;
            private float? _targetVolumeDb;

            private void UpdateLevels()
            {
                // источник, за которым следим: уровень, текущая громкость в OBS и признак неактивной сцены
                var monitor = _monitor.Probe;
                float peak = monitor?.TakePeakDb() ?? -96.0f;
                _levelDb = null;
                _obsVolumeDb = null;
                _targetVolumeDb = null;
                if (monitor == null)
                {
                    ShowState(_threshold, Loc.PickSource, ghost: null);
                }
                else if (!monitor.Attached)
                {
                    ShowState(_threshold, Loc.SourceNotFound, ghost: null);
                }
                else if (!IsGlobal(_scene) && !monitor.IsActive)
                {
                    _obsVolumeDb = monitor.VolumeDb;
                    ShowState(_threshold, Loc.SceneInactive, ghost: monitor.VolumeDb);
                }
                else
                {
                    _levelDb = peak;
                    _obsVolumeDb = monitor.VolumeDb;
                    _threshold.Dimmed = false;
                    _threshold.StatusText = "";
                    _threshold.GhostValue = monitor.VolumeDb;
                    _threshold.SetLevel(peak);
                }

                // цель: источник OBS или процесс Windows
                if (TargetsObs)
                {
                    var target = _target.Probe;
                    if (target != null && target.Attached) _targetVolumeDb = target.VolumeDb;
                    // текущая громкость источника (его фейдер в OBS) - черта на шкале; усиление выше 0 дБ упирается в верх шкалы
                    _duck.CurrentValue = _targetVolumeDb.HasValue ? Math.Max(_targetVolumeDb.Value, FilterConfig.MinDb) : null;
                    float targetPeak = target?.TakePeakDb() ?? -96.0f;
                    if (target == null) ShowState(_duck, Loc.PickSource, ghost: null);
                    else if (!target.Attached) ShowState(_duck, Loc.SourceNotFound, ghost: null);
                    else if (!IsGlobal(_targetScene) && !target.IsActive) ShowState(_duck, Loc.SceneInactive, ghost: null);
                    else
                    {
                        _duck.Dimmed = false;
                        _duck.StatusText = "";
                        _duck.SetLevel(targetPeak);
                    }
                }
                else
                {
                    _processProbe.ProcessName = _process.Text;
                    float level = _processProbe.ReadPeak() * 100.0f;
                    // третий ползунок следит за текущей громкостью процесса в микшере Windows, в том числе пока она меняется при прослушивании
                    float volume = _processProbe.ReadVolume();
                    _duck.CurrentValue = volume >= 0.0f ? volume * 100.0f : null;
                    if (_process.Text.Trim().Length == 0) ShowState(_duck, Loc.ProcessNotSelected, ghost: null);
                    else if (!_processProbe.HasSessions) ShowState(_duck, Loc.ProcessNotRunning, ghost: null);
                    else
                    {
                        _duck.Dimmed = false;
                        _duck.StatusText = "";
                        _duck.SetLevel(level);
                    }
                }
            }

            // --- текстовые подсказки карточки: значения рядом с индикаторами, фраза-сводка, плашка состояния ---

            private static string Num(float value) => value.ToString("0.#", CultureInfo.InvariantCulture);

            // Значение шкалы словами: для источника OBS «-24 дБ», для процесса «20%»; нижний край шкалы - тишина
            private string ScaleText(float value)
            {
                if (TargetsObs) return value <= FilterConfig.MinDb + 0.01f ? Loc.ReadoutSilence : Loc.Db(Num(value));
                return value <= 0.01f ? Loc.ReadoutSilence : Num(value) + "%";
            }

            private string ReturnText() => _restoreCustom ? ScaleText(_duck.ReturnEffective) : Loc.ReadoutOriginal;

            private void RefreshInfo()
            {
                UpdateReadouts();
                UpdateSummary();
                UpdateChip();
            }

            private void UpdateReadouts()
            {
                string level = _levelDb.HasValue ? (_levelDb.Value <= -90.0f ? Loc.ReadoutSilence : Loc.Db(Num(MathF.Round(_levelDb.Value)))) : Loc.ReadoutUnknown;
                string obsVolume = _obsVolumeDb.HasValue ? Loc.Db(Num(MathF.Round(_obsVolumeDb.Value * 10.0f) / 10.0f)) : Loc.ReadoutUnknown;
                _thresholdReadout.SetRows(
                    (Loc.ReadoutThreshold, Loc.Db(Num(_threshold.Value))),
                    (Loc.ReadoutLevel, level),
                    (Loc.ReadoutObsVolume, obsVolume));

                string now = Loc.ReadoutUnknown;
                if (TargetsObs)
                {
                    if (_targetVolumeDb.HasValue) now = _targetVolumeDb.Value <= -95.0f ? Loc.ReadoutSilence : Loc.Db(Num(MathF.Round(_targetVolumeDb.Value * 10.0f) / 10.0f));
                }
                else if (_duck.CurrentValue.HasValue)
                {
                    now = Num(MathF.Round(_duck.CurrentValue.Value)) + "%";
                }

                _duckReadout.SetRows(
                    (Loc.ReadoutDuck, ScaleText(_duck.Value)),
                    (Loc.ReadoutReturn, ReturnText()),
                    (Loc.ReadoutNow, now));
            }

            // Одна фраза о том, что сделает фильтр с текущими настройками; обновляется вместе с правками
            private void UpdateSummary()
            {
                string source = _source.SelectedItem as string ?? "";
                string target = TargetsObs ? (_targetSource.SelectedItem as string ?? "") : CurrentProcessName();
                if (source.Length == 0 || target.Length == 0)
                {
                    _summary.SetMarkup(Loc.SummaryPick);
                    return;
                }

                string duck = _duck.Value <= (TargetsObs ? FilterConfig.MinDb + 0.01f : 0.01f) ? Loc.SummaryToSilence : ScaleText(_duck.Value);
                string back = _restoreCustom ? ScaleText(_duck.ReturnEffective) : Loc.SummaryBackOriginal;
                int fade = FilterConfig.ParseFade(_fade.Text);
                int hold = FilterConfig.ParseHold(_hold.Text);
                _summary.SetMarkup(Loc.Summary(source, Loc.Db(Num(_threshold.Value)), target, duck,
                    $"{fade} {Loc.MsUnit}", $"{hold} {Loc.MsUnit}", back));
            }

            private void UpdateChip()
            {
                string text;
                bool active = false;
                if (!_enabled.Checked)
                {
                    text = Loc.ChipOff;
                }
                else
                {
                    string key = CurrentTargetKey();
                    if (_source.SelectedItem is not string { Length: > 0 } || key.Length == 0) text = Loc.ChipNotReady;
                    else if (AudioMuterManager.TryGetDuckedOriginal(key, out _)) { text = Loc.ChipDucking; active = true; }
                    else text = Loc.ChipWaiting;
                }

                _chip.Active = active;
                if (_chip.Text != text)
                {
                    _chip.Text = text;
                    LayoutHeader();   // плашка меняет ширину вместе с текстом
                }
            }

            private string CurrentProcessName()
            {
                string process = _process.Text.Trim();
                if (process.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) process = process.Substring(0, process.Length - 4).Trim();
                return process;
            }

            // Запоминает исходную громкость цели, как только она появилась, и передаёт её правому ползунку как верхний предел.
            // Пока цель приглушена движком или идёт предпросмотр, текущая громкость не исходная, поэтому её не запоминаем
            private void UpdateOriginal()
            {
                string key = CurrentTargetKey();
                if (!string.Equals(key, _originalKey, StringComparison.Ordinal))
                {
                    _originalKey = key;
                    _originalVolume = null;
                }

                if (key.Length > 0)
                {
                    if (AudioMuterManager.TryGetDuckedOriginal(key, out float ducked))
                    {
                        if (ducked > 0.0f) _originalVolume = ducked;
                    }
                    else if (!_preview.IsActive)
                    {
                        float? current = CurrentVolume();
                        if (current.HasValue) _originalVolume = current;
                    }
                }

                // пока возврат «до исходной», ползунок возврата сам стоит на исходной громкости цели (и следует за ней)
                if (!_restoreCustom) _duck.ReturnValue = OriginalOnScale();
            }

            // Исходная громкость цели в единицах шкалы; если она ещё неизвестна (цель не запущена), верх шкалы
            private float OriginalOnScale()
            {
                if (_originalVolume.HasValue) return VolumeToScale(_originalVolume.Value);
                return TargetsObs ? 0.0f : 100.0f;
            }

            private float? CurrentVolume()
            {
                if (TargetsObs)
                {
                    var target = _target.Probe;
                    if (target == null || !target.Attached) return null;

                    float db = target.VolumeDb;
                    return db <= -95.0f ? 0.0f : MathF.Pow(10.0f, db / 20.0f);
                }

                // сессии процесса уже найдены при чтении уровня в UpdateLevels
                float volume = _processProbe.ReadVolume();
                return volume >= 0.0f ? volume : null;
            }

            private float VolumeToScale(float volume)
            {
                if (TargetsObs) return volume <= 0.0f ? FilterConfig.MinDb : Math.Min(0.0f, 20.0f * MathF.Log10(volume));
                return Math.Clamp(volume * 100.0f, 0.0f, 100.0f);
            }

            private static void ShowState(LevelSlider meter, string text, float? ghost)
            {
                meter.Dimmed = true;
                meter.StatusText = text;
                meter.GhostValue = ghost;
                meter.SetLevel(-96.0f);
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    _preview.Cancel();   // громкость цели возвращается, даже если окно закрыли во время предпросмотра
                    _monitor.Bind(null);
                    _target.Bind(null);
                    _processProbe.Dispose();
                }
                base.Dispose(disposing);
            }

            public FilterConfig ToConfig()
            {
                string processName = _process.Text.Trim();
                if (processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    processName = processName.Substring(0, processName.Length - 4).Trim();
                }

                return new FilterConfig
                {
                    name = FilterName,
                    enabled = _enabled.Checked,
                    scene_name = _scene.SelectedItem is SceneItem { IsGlobal: false } scene ? scene.Name : "",
                    source_name = _source.SelectedItem as string ?? "",
                    target_type = TargetsObs ? FilterConfig.TargetObsSource : FilterConfig.TargetProcess,
                    process_name = processName,
                    target_scene = _targetScene.SelectedItem is SceneItem { IsGlobal: false } targetScene ? targetScene.Name : "",
                    target_source = _targetSource.SelectedItem as string ?? "",
                    threshold_db = _threshold.Value,
                    duck_db = _duckDb,
                    duck_percent = _duckPercent,
                    restore_db = _restoreDb,
                    restore_percent = _restorePercent,
                    restore_custom = _restoreCustom,
                    fade_ms = FilterConfig.ParseFade(_fade.Text),
                    hold_ms = FilterConfig.ParseHold(_hold.Text)
                };
            }
        }

        // Подключение карточки к измерителю источника через общий ProbeHub: при смене источника старый отпускается
        private sealed class ObsLevelProbeSlot
        {
            private readonly ProbeHub _hub;

            public ObsLevelProbeSlot(ProbeHub hub) { _hub = hub; }

            public ObsLevelProbe? Probe { get; private set; }

            public void Bind(string? sourceName)
            {
                if (string.IsNullOrEmpty(sourceName))
                {
                    _hub.Release(Probe);
                    Probe = null;
                    return;
                }

                if (Probe != null && string.Equals(Probe.SourceName, sourceName, StringComparison.Ordinal)) return;

                var next = _hub.Acquire(sourceName);
                _hub.Release(Probe);
                Probe = next;
            }
        }
    }
}
