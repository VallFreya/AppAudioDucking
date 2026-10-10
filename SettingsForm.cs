using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace ProcessMuterByChannel
{
    /// <summary>
    /// Окно настроек: список фильтров, каждый фильтр - сцена, звуковой источник, процесс, порог и время затухания.
    /// Внешний вид повторяет тему OBS (см. ObsTheme).
    /// </summary>
    internal sealed class SettingsForm : Form
    {
        private const int CardWidth = 500;

        // Эти элементы пересоздаются при смене темы (см. BuildUi)
        private FlowLayoutPanel _list = null!;
        private Label _emptyLabel = null!;
        private Label _statusLabel = null!;
        private System.Windows.Forms.Timer _statusTimer = null!;
        private ToolTip _tip = null!;
        private readonly List<FilterCard> _cards = new List<FilterCard>();
        private readonly List<SceneItem> _scenes = new List<SceneItem>();
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
            // 16 px полей слева и справа плюс место под полосу прокрутки, чтобы карточки не прыгали при её появлении
            ClientSize = new Size(Ui.Px(CardWidth + 32) + SystemInformation.VerticalScrollBarWidth, Ui.Px(700));
            MinimumSize = new Size(Size.Width, Ui.Px(420));

            LoadScenes();
            ReloadProcessNames();

            ObsTheme.Apply(Plugin.ThemeDark);
            BuildUi(filters);
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

            _emptyLabel = new Label
            {
                Text = Loc.NoFilters,
                AutoSize = false,
                Width = Ui.Px(CardWidth),
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
                Bounds = new Rectangle(Ui.Px(16), Ui.Px(14), Ui.Px(210), Ui.Px(32))
            };
            btnAdd.Click += (s, e) =>
            {
                var card = AddCard(new FilterConfig());
                _list.ScrollControlIntoView(card);
            };
            topBar.Controls.Add(btnAdd);

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
                Height = Ui.Px(64),
                BackColor = ObsTheme.BaseBg,
                Padding = new Padding(Ui.Px(16), Ui.Px(14), Ui.Px(16), Ui.Px(14))
            };
            _statusLabel = new Label
            {
                Dock = DockStyle.Fill,
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = ObsTheme.TextMuted,
                BackColor = ObsTheme.BaseBg
            };
            var btnSave = new ThemedButton { Text = Loc.Save, Primary = true, Dock = DockStyle.Right, Width = Ui.Px(150) };
            btnSave.Click += (s, e) => Save();
            // Fill-контрол добавляется первым, чтобы docking отдал ему оставшееся место
            bottomBar.Controls.Add(_statusLabel);
            bottomBar.Controls.Add(btnSave);

            var statusLabel = _statusLabel;
            _statusTimer = new System.Windows.Forms.Timer { Interval = 2500 };
            var statusTimer = _statusTimer;
            statusTimer.Tick += (s, e) =>
            {
                statusTimer.Stop();
                statusLabel.Text = "";
            };

            // то же правило docking: Fill первым
            Controls.Add(_list);
            Controls.Add(topBar);
            Controls.Add(bottomBar);

            foreach (var filter in filters) AddCard(filter);
            UpdateEmptyState();
        }

        // Меняет тему: запоминает её в конфиге и пересобирает окно, сохраняя введённые, но ещё не сохранённые значения
        private void SwitchTheme(bool dark)
        {
            if (dark == ObsTheme.IsDark) return;

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

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Ui.ApplyWindowChrome(Handle);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _statusTimer.Dispose();
                _tip.Dispose();
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

        private FilterCard AddCard(FilterConfig config)
        {
            // у нового фильтра (и у фильтра из старого конфига) название по умолчанию: «Фильтр N» с первым свободным номером
            if (string.IsNullOrWhiteSpace(config.name)) config.name = NextDefaultName();

            var card = new FilterCard(this, config);
            card.RemoveRequested += (s, e) =>
            {
                _cards.Remove(card);
                _list.Controls.Remove(card);
                card.Dispose();
                UpdateEmptyState();
            };

            _cards.Add(card);
            _list.Controls.Add(card);
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

        private void Save()
        {
            var filters = new List<FilterConfig>(_cards.Count);
            foreach (var card in _cards) filters.Add(card.ToConfig());

            Plugin.SaveConfig(filters);

            // окно остаётся открытым, о сохранении сообщаем коротким текстом рядом с кнопкой
            _statusLabel.Text = Loc.Saved;
            _statusTimer.Stop();
            _statusTimer.Start();
        }

        // Карточка одного фильтра
        private sealed class FilterCard : CardPanel
        {
            // Строка карточки: подпись и элементы со смещением по вертикали относительно строки.
            // Строки можно скрывать (например, выбор цели зависит от вида цели), остальные сдвигаются вверх
            private sealed class Row
            {
                public Row(Label label, Func<bool> visible, (Control Control, int Dy)[] items)
                {
                    Label = label;
                    Visible = visible;
                    Items = items;
                }

                public Label Label { get; }
                public Func<bool> Visible { get; }
                public (Control Control, int Dy)[] Items { get; }
            }

            private readonly SettingsForm _owner;
            private readonly List<Row> _rows = new List<Row>();
            private readonly ThemedTextBox _name;
            private readonly ThemedPicker _scene;
            private readonly ThemedPicker _source;
            private readonly ThemedPicker _kind;
            private readonly ThemedPicker _process;
            private readonly ThemedPicker _targetScene;
            private readonly ThemedPicker _targetSource;
            private readonly ThemedTextBox _threshold;
            private readonly ThemedTextBox _fade;
            private readonly ThemedTextBox _hold;

            public event EventHandler? RemoveRequested;

            public string FilterName => _name.Text.Trim();

            private bool TargetsObs => _kind.SelectedIndex == 1;

            public FilterCard(SettingsForm owner, FilterConfig config)
            {
                _owner = owner;
                Width = Ui.Px(CardWidth);
                Margin = new Padding(0, 0, 0, Ui.Px(12));

                int ctrlX = Ui.Px(190);
                int ctrlW = Ui.Px(CardWidth - 190 - 16);
                int inputH = Ui.Px(30);
                int numberW = Ui.Px(110);
                int refreshW = Ui.Px(36);

                // заголовок: название фильтра и кнопка удаления
                int removeW = Ui.Px(176);
                _name = new ThemedTextBox
                {
                    Bounds = new Rectangle(Ui.Px(16), Ui.Px(9), Ui.Px(CardWidth - 16 - 16 - 176 - 10), inputH),
                    Font = Ui.Font(FontStyle.Bold)
                };
                var btnRemove = new ThemedButton
                {
                    Text = Loc.RemoveFilter,
                    Glyph = Ui.GlyphDelete,
                    Bounds = new Rectangle(Ui.Px(CardWidth - 16) - removeW, Ui.Px(9), removeW, inputH)
                };
                btnRemove.Click += (s, e) => RemoveRequested?.Invoke(this, EventArgs.Empty);
                Controls.Add(_name);
                Controls.Add(btnRemove);

                _scene = new ThemedPicker(editable: false) { Bounds = new Rectangle(ctrlX, 0, ctrlW, inputH) };
                _source = new ThemedPicker(editable: false) { Bounds = new Rectangle(ctrlX, 0, ctrlW, inputH) };
                _kind = new ThemedPicker(editable: false) { Bounds = new Rectangle(ctrlX, 0, ctrlW, inputH) };
                _process = new ThemedPicker(editable: true) { Bounds = new Rectangle(ctrlX, 0, ctrlW - refreshW - Ui.Px(6), inputH) };
                var btnRefresh = new ThemedButton
                {
                    Glyph = Ui.GlyphRefresh,
                    TabStop = false,
                    Bounds = new Rectangle(ctrlX + ctrlW - refreshW, 0, refreshW, inputH)
                };
                _owner._tip.SetToolTip(btnRefresh, Loc.RefreshProcesses);
                _targetScene = new ThemedPicker(editable: false) { Bounds = new Rectangle(ctrlX, 0, ctrlW, inputH) };
                _targetSource = new ThemedPicker(editable: false) { Bounds = new Rectangle(ctrlX, 0, ctrlW, inputH) };

                _threshold = new ThemedTextBox { Bounds = new Rectangle(ctrlX, 0, numberW, inputH) };
                _fade = new ThemedTextBox { Bounds = new Rectangle(ctrlX, 0, numberW, inputH) };
                _hold = new ThemedTextBox { Bounds = new Rectangle(ctrlX, 0, numberW, inputH) };

                var holdHelp = new ThemedButton
                {
                    Text = "?",
                    Round = true,
                    TabStop = false,
                    Font = Ui.Font(FontStyle.Bold, 0.9f),
                    Bounds = new Rectangle(ctrlX + numberW + Ui.Px(44), 0, Ui.Px(24), Ui.Px(24))
                };

                // порядок добавления строк задаёт порядок Tab
                AddRow(Loc.Scene, () => true, (_scene, 0));
                AddRow(Loc.AudioSource, () => true, (_source, 0));
                AddRow(Loc.TargetKind, () => true, (_kind, 0));
                AddRow(Loc.Process, () => !TargetsObs, (_process, 0), (btnRefresh, 0));
                AddRow(Loc.TargetScene, () => TargetsObs, (_targetScene, 0));
                AddRow(Loc.TargetSource, () => TargetsObs, (_targetSource, 0));
                AddRow(Loc.Threshold, () => true, (_threshold, 0), (Suffix(Loc.Decibels, ctrlX + numberW), 0));
                AddRow(Loc.Fade, () => true, (_fade, 0), (Suffix(Loc.Milliseconds, ctrlX + numberW), 0));
                var holdLabel = AddRow(Loc.Hold, () => true, (_hold, 0), (Suffix(Loc.Milliseconds, ctrlX + numberW), 0), (holdHelp, Ui.Px(3)));
                holdHelp.BringToFront(); // подпись «мс» не должна перекрывать значок

                _owner._tip.SetToolTip(holdLabel, Loc.HoldHint);
                _owner._tip.SetToolTip(_hold, Loc.HoldHint);
                _owner._tip.SetToolTip(holdHelp, Loc.HoldHint);

                Fill(config);

                // при смене сцены выбранный источник сбрасывается
                _scene.SelectionChanged += (s, e) =>
                {
                    _source.Enabled = _scene.SelectedItem != null;
                    RefreshSources(null);
                };

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
                    Relayout();
                };
                _targetScene.SelectionChanged += (s, e) =>
                {
                    _targetSource.Enabled = _targetScene.SelectedItem != null;
                    RefreshTargetSources(null);
                };
                _targetSource.DropDownOpening += (s, e) => RefreshTargetSources(_targetSource.Text);

                // список процессов обновляется, когда пользователь переходит к полю, и по кнопке обновления
                _process.FieldEntered += (s, e) =>
                {
                    _owner.ReloadProcessNames();
                    ApplyProcessFilter(openDropDown: false);
                };
                _process.UserTextChanged += (s, e) => ApplyProcessFilter(openDropDown: _process.Text.Length > 0);
                _process.DropDownOpening += (s, e) => ApplyProcessFilter(openDropDown: false);
                btnRefresh.Click += (s, e) =>
                {
                    _owner.ReloadProcessNames();
                    _process.FocusField();
                    _process.MoveCaretToEnd();
                    ApplyProcessFilter(openDropDown: true);
                };
            }

            private Label AddRow(string text, Func<bool> visible, params (Control Control, int Dy)[] items)
            {
                var label = new Label
                {
                    Text = text,
                    Bounds = new Rectangle(Ui.Px(16), 0, Ui.Px(166), Ui.Px(30)),
                    AutoSize = false,
                    TextAlign = ContentAlignment.MiddleLeft,
                    ForeColor = ObsTheme.Text,
                    BackColor = ObsTheme.BaseBg
                };
                Controls.Add(label);
                foreach (var item in items) Controls.Add(item.Control);

                _rows.Add(new Row(label, visible, items));
                return label;
            }

            private Label Suffix(string text, int left)
            {
                return new Label
                {
                    Text = text,
                    Bounds = new Rectangle(left + Ui.Px(10), 0, Ui.Px(30), Ui.Px(30)),
                    AutoSize = false,
                    TextAlign = ContentAlignment.MiddleLeft,
                    ForeColor = ObsTheme.TextMuted,
                    BackColor = ObsTheme.BaseBg
                };
            }

            // Расставляет видимые строки друг под другом и подгоняет высоту карточки
            private void Relayout()
            {
                int y = Ui.Px(54);
                foreach (var row in _rows)
                {
                    bool visible = row.Visible();
                    row.Label.Visible = visible;
                    foreach (var item in row.Items) item.Control.Visible = visible;
                    if (!visible) continue;

                    row.Label.Top = y;
                    foreach (var item in row.Items) item.Control.Top = y + item.Dy;
                    y += Ui.Px(40);
                }

                Height = y + Ui.Px(6);
            }

            private void Fill(FilterConfig config)
            {
                _name.Text = config.name;

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

                _threshold.Text = config.threshold_db.ToString(CultureInfo.InvariantCulture);
                _fade.Text = config.fade_ms.ToString(CultureInfo.InvariantCulture);
                _hold.Text = config.hold_ms.ToString(CultureInfo.InvariantCulture);

                Relayout();
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
                    scene_name = _scene.SelectedItem is SceneItem { IsGlobal: false } scene ? scene.Name : "",
                    source_name = _source.SelectedItem as string ?? "",
                    target_type = TargetsObs ? FilterConfig.TargetObsSource : FilterConfig.TargetProcess,
                    process_name = processName,
                    target_scene = _targetScene.SelectedItem is SceneItem { IsGlobal: false } targetScene ? targetScene.Name : "",
                    target_source = _targetSource.SelectedItem as string ?? "",
                    threshold_db = FilterConfig.ParseThreshold(_threshold.Text),
                    fade_ms = FilterConfig.ParseFade(_fade.Text),
                    hold_ms = FilterConfig.ParseHold(_hold.Text)
                };
            }
        }
    }
}
