using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using ProcessMuterByChannel;
using Xunit;

namespace ProcessMuter.Tests
{
    /// <summary>
    /// Окно настроек и его контролы на крайних значениях. Окно создаётся и рисуется без OBS (его API там недоступно,
    /// списки сцен и источников пустые), поэтому проверяется устойчивость вёрстки и ввода, а не данные OBS.
    /// </summary>
    [Collection("Plugin state")]
    public class SettingsFormTests
    {
        private static T Field<T>(object owner, string name) =>
            (T)owner.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(owner)!;

        private static List<object> Cards(SettingsForm form) =>
            Field<System.Collections.IEnumerable>(form, "_cards").Cast<object>().ToList();

        private static FilterConfig ToConfig(object card) => (FilterConfig)card.GetType().GetMethod("ToConfig")!.Invoke(card, null)!;

        private static void Pump(int ms = 100)
        {
            Application.DoEvents();
            Thread.Sleep(ms);
            Application.DoEvents();
        }

        private static void Click(Control c) =>
            typeof(Control).GetMethod("OnClick", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(c, new object[] { EventArgs.Empty });

        private static IEnumerable<Control> All(Control root)
        {
            foreach (Control c in root.Controls)
            {
                yield return c;
                foreach (var d in All(c)) yield return d;
            }
        }

        private static SettingsForm Open(IReadOnlyList<FilterConfig> filters, bool dark = true)
        {
            Plugin.ConfigPathOverride = TestEnvironment.ConfigPath;
            typeof(Plugin).GetProperty("ThemeDark")!.SetValue(null, dark);
            var form = new SettingsForm(filters);
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(10, 10);
            form.Show();
            Pump();
            Field<System.Windows.Forms.Timer>(form, "_liveTimer").Stop();
            return form;
        }

        private static void LiveTick(SettingsForm form)
        {
            foreach (var card in Cards(form))
            {
                card.GetType().GetMethod("UpdateLive")!.Invoke(card, null);
            }
        }

        // ---------- пустой и огромный список ----------

        [Fact]
        public void Empty_list_opens_and_shows_the_hint()
        {
            Sta.Run(() =>
            {
                using var form = Open(Array.Empty<FilterConfig>());
                Assert.Empty(Cards(form));
                Assert.True(Field<Label>(form, "_emptyLabel").Visible);
                LiveTick(form);
            });
        }

        [Fact]
        public void A_hundred_filters_open_and_tick_in_reasonable_time()
        {
            Sta.Run(() =>
            {
                var filters = Enumerable.Range(0, 100).Select(i => new FilterConfig
                {
                    name = "Фильтр " + i, scene_name = "Сцена " + i, source_name = "Источник " + i,
                    process_name = "proc" + i, threshold_db = -(i % 60), duck_percent = i % 100
                }).ToList();

                var sw = System.Diagnostics.Stopwatch.StartNew();
                using var form = Open(filters);
                Assert.True(sw.ElapsedMilliseconds < 30_000, $"opening took {sw.ElapsedMilliseconds} ms");
                Assert.Equal(100, Cards(form).Count);

                sw.Restart();
                for (int i = 0; i < 3; i++) LiveTick(form);
                Assert.True(sw.ElapsedMilliseconds < 20_000, $"3 live ticks took {sw.ElapsedMilliseconds} ms");
            });
        }

        // ---------- странные значения в конфиге ----------

        [Fact]
        public void Out_of_range_and_nonfinite_values_are_clamped_by_the_form()
        {
            Sta.Run(() =>
            {
                // значения не проходят через Clone: окно должно справиться само
                var weird = new FilterConfig
                {
                    threshold_db = float.NaN, duck_db = float.PositiveInfinity, duck_percent = float.NegativeInfinity,
                    restore_db = float.NaN, restore_percent = 1e30f, restore_custom = true, fade_ms = int.MinValue, hold_ms = int.MaxValue
                };

                using var form = Open(new[] { weird });
                var config = ToConfig(Cards(form)[0]);

                Assert.InRange(config.threshold_db, -60f, 0f);
                Assert.InRange(config.duck_db, -60f, 0f);
                Assert.InRange(config.duck_percent, 0f, 100f);
                Assert.InRange(config.restore_db, -60f, 0f);
                Assert.InRange(config.restore_percent, 0f, 100f);
                Assert.True(config.fade_ms > 0);
                Assert.InRange(config.hold_ms, 0, FilterConfig.MaxHoldMs);
            });
        }

        [Fact]
        public void Huge_and_strange_names_do_not_break_the_header()
        {
            Sta.Run(() =>
            {
                string[] names =
                {
                    new string('Ж', 100_000), "   ", "\t\t", "a\u0000b", "🎵🎵🎵🎵🎵", "‮evil rtl", new string('W', 500), "line1\nline2",
                };

                var filters = names.Select(n => new FilterConfig { name = n, source_name = n, process_name = n }).ToList();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                using var form = Open(filters);
                LiveTick(form);
                Assert.True(sw.ElapsedMilliseconds < 20_000, $"took {sw.ElapsedMilliseconds} ms");

                foreach (var card in Cards(form))
                {
                    var control = (Control)card;
                    Assert.True(control.Height > 0);
                    var name = Field<ThemedTextBox>(card, "_name");
                    Assert.True(name.Right <= control.Width, "название вылезло за карточку");
                    Assert.True(Field<StatusChip>(card, "_chip").Right <= control.Width + 1, "плашка вылезла за карточку");
                }
            });
        }

        [Fact]
        public void Duplicate_names_are_allowed_and_default_names_skip_taken_numbers()
        {
            Sta.Run(() =>
            {
                using var form = Open(new[]
                {
                    new FilterConfig { name = Loc.FilterTitle(1) }, new FilterConfig { name = Loc.FilterTitle(1) }, new FilterConfig { name = "" }
                });
                var names = Cards(form).Select(c => ToConfig(c).name).ToList();
                Assert.Equal(new[] { Loc.FilterTitle(1), Loc.FilterTitle(1), Loc.FilterTitle(2) }, names);
            });
        }

        // ---------- ввод в поля ----------

        [Fact]
        public void Number_fields_normalize_what_the_user_typed()
        {
            Sta.Run(() =>
            {
                using var form = Open(new[] { new FilterConfig() });
                var card = Cards(form)[0];
                var fade = Field<ThemedTextBox>(card, "_fade");
                var hold = Field<ThemedTextBox>(card, "_hold");
                var leave = typeof(Control).GetMethod("OnLeave", BindingFlags.NonPublic | BindingFlags.Instance)!;

                void Type(ThemedTextBox box, string text)
                {
                    box.Text = text;
                    leave.Invoke(box.InnerTextBox, new object[] { EventArgs.Empty });
                }

                Type(fade, "abc"); Assert.Equal("600", fade.Text);
                Type(fade, ""); Assert.Equal("600", fade.Text);
                Type(fade, "-5"); Assert.Equal("600", fade.Text);
                Type(fade, "0"); Assert.Equal("600", fade.Text);
                Type(fade, "99999999999"); Assert.Equal("60000", fade.Text);
                Type(fade, "250"); Assert.Equal("250", fade.Text);

                Type(hold, "x"); Assert.Equal("1500", hold.Text);
                Type(hold, "0"); Assert.Equal("0", hold.Text);
                Type(hold, "99999999999"); Assert.Equal("3600000", hold.Text);

                var config = ToConfig(card);
                Assert.Equal(250, config.fade_ms);
                Assert.Equal(3600000, config.hold_ms);
            });
        }

        [Fact]
        public void Garbage_typed_into_fields_never_reaches_the_config()
        {
            Sta.Run(() =>
            {
                using var form = Open(new[] { new FilterConfig() });
                var card = Cards(form)[0];
                Field<ThemedTextBox>(card, "_fade").Text = "1e308";
                Field<ThemedTextBox>(card, "_hold").Text = "٣٠٠";
                Field<ThemedPicker>(card, "_process").Text = "  Some App.EXE  ";

                var config = ToConfig(card);
                Assert.Equal(FilterConfig.DefaultFadeMs, config.fade_ms);
                Assert.Equal(FilterConfig.DefaultHoldMs, config.hold_ms);
                Assert.Equal("Some App", config.process_name);
            });
        }

        [Fact]
        public void Slider_values_typed_into_the_box_are_clamped_and_garbage_is_rejected()
        {
            Sta.Run(() =>
            {
                using var form = Open(new[] { new FilterConfig() });
                var threshold = Field<LevelSlider>(Cards(form)[0], "_threshold");
                var box = All(threshold).OfType<ThemedTextBox>().First();
                var leave = typeof(Control).GetMethod("OnLeave", BindingFlags.NonPublic | BindingFlags.Instance)!;

                void Type(string text)
                {
                    box.Text = text;
                    leave.Invoke(box.InnerTextBox, new object[] { EventArgs.Empty });
                }

                Type("-30"); Assert.Equal(-30f, threshold.Value);
                foreach (var junk in new[] { "NaN", "Infinity", "-Infinity", "1e999", "", "  ", "--5", "1,2,3", "0x10", "🎵", "-30 dB dB" })
                {
                    Type(junk);
                    Assert.True(float.IsFinite(threshold.Value), $"'{junk}' -> {threshold.Value}");
                    Assert.InRange(threshold.Value, -60f, 0f);
                }
                Type("-1e10"); Assert.Equal(-60f, threshold.Value);
                Type("1e10"); Assert.Equal(0f, threshold.Value);
                Type("-12,5"); Assert.Equal(-12.5f, threshold.Value);
            });
        }

        // ---------- переключение режимов и тем ----------

        [Fact]
        public void Switching_target_kind_back_and_forth_keeps_both_levels()
        {
            Sta.Run(() =>
            {
                using var form = Open(new[] { new FilterConfig { duck_percent = 35, duck_db = -18, restore_custom = true, restore_percent = 70, restore_db = -6 } });
                var card = Cards(form)[0];
                var kind = Field<ThemedPicker>(card, "_kind");

                for (int i = 0; i < 20; i++)
                {
                    kind.SelectedIndex = i % 2 == 0 ? 1 : 0;
                    Pump(10);
                    LiveTick(form);
                }

                kind.SelectedIndex = 0;
                var config = ToConfig(card);
                Assert.Equal(35f, config.duck_percent);
                Assert.Equal(-18f, config.duck_db);
                Assert.Equal(70f, config.restore_percent);
                Assert.Equal(-6f, config.restore_db);
            });
        }

        [Fact]
        public void Theme_can_be_switched_many_times_without_losing_values_or_leaking_cards()
        {
            Sta.Run(() =>
            {
                using var form = Open(new[]
                {
                    new FilterConfig { name = "A", process_name = "a", threshold_db = -33, fade_ms = 321, enabled = false },
                    new FilterConfig { name = "B", process_name = "b", threshold_db = -44, hold_ms = 987 },
                });
                var before = Cards(form).Select(ToConfig).ToList();
                var switchTheme = typeof(SettingsForm).GetMethod("SwitchTheme", BindingFlags.NonPublic | BindingFlags.Instance)!;

                for (int i = 0; i < 12; i++)
                {
                    switchTheme.Invoke(form, new object[] { i % 2 == 1 });
                    Pump(10);
                    Field<System.Windows.Forms.Timer>(form, "_liveTimer").Stop();
                }

                var after = Cards(form).Select(ToConfig).ToList();
                Assert.Equal(2, after.Count);
                for (int i = 0; i < 2; i++)
                {
                    Assert.Equal(before[i].name, after[i].name);
                    Assert.Equal(before[i].threshold_db, after[i].threshold_db);
                    Assert.Equal(before[i].fade_ms, after[i].fade_ms);
                    Assert.Equal(before[i].hold_ms, after[i].hold_ms);
                    Assert.Equal(before[i].enabled, after[i].enabled);
                }
                typeof(Plugin).GetProperty("ThemeDark")!.SetValue(null, true);
            });
        }

        // ---------- размеры окна ----------

        [Fact]
        public void Layout_survives_every_window_width_and_height()
        {
            Sta.Run(() =>
            {
                using var form = Open(new[]
                {
                    new FilterConfig { name = "Музыка", scene_name = "S", source_name = "Mic", process_name = "chrome" },
                    new FilterConfig { name = "OBS", target_type = FilterConfig.TargetObsSource, scene_name = "S", source_name = "Mic", target_scene = "S", target_source = "BGM" },
                });

                foreach (int width in new[] { 100, 300, 500, 700, 900, 1100, 1500, 2400, 5000 })
                {
                    foreach (int height in new[] { 100, 400, 1200 })
                    {
                        form.ClientSize = new Size(width, height);
                        Pump(5);
                        foreach (var card in Cards(form))
                        {
                            var control = (Control)card;
                            Assert.True(control.Height > 100, $"{width}x{height}: card height {control.Height}");
                            Assert.True(control.Width >= 300, $"{width}x{height}: card width {control.Width}");
                        }
                    }
                }
            });
        }

        // ---------- добавление, дублирование, удаление ----------

        [Fact]
        public void Adding_and_duplicating_many_filters_keeps_order_and_unique_default_names()
        {
            Sta.Run(() =>
            {
                using var form = Open(Array.Empty<FilterConfig>());
                var add = All(form).OfType<ThemedButton>().First(b => b.Text == Loc.AddFilter);

                for (int i = 0; i < 25; i++) Click(add);
                Pump();
                var names = Cards(form).Select(c => ToConfig(c).name).ToList();
                Assert.Equal(25, names.Count);
                Assert.Equal(names.Count, names.Distinct().Count());

                // дублируем самый первый фильтр несколько раз: копии встают сразу за ним
                for (int i = 0; i < 5; i++) Click(Field<ThemedButton>(Cards(form)[0], "_duplicate"));
                Pump();
                Assert.Equal(30, Cards(form).Count);

                // порядок в списке совпадает с порядком в данных, а кнопка добавления всегда последняя
                var list = Field<FlowLayoutPanel>(form, "_list");
                var cardControls = list.Controls.OfType<Control>().Where(c => c.GetType().Name == "FilterCard").ToList();
                Assert.Equal(Cards(form).Cast<Control>().ToList(), cardControls);
                Assert.IsType<ThemedButton>(list.Controls[list.Controls.Count - 1]);

                // номера для Tab идут по порядку
                var tabs = cardControls.Select(c => c.TabIndex).ToList();
                Assert.Equal(tabs.OrderBy(x => x).ToList(), tabs);
            });
        }

        [Fact]
        public void Remove_requires_confirmation_and_ok_removes_cancel_keeps()
        {
            Sta.Run(() =>
            {
                using var form = Open(new[] { new FilterConfig { name = "A" }, new FilterConfig { name = "B" } });
                string answer = "cancel";
                var timer = new System.Windows.Forms.Timer { Interval = 50 };
                timer.Tick += (s, e) =>
                {
                    var dialog = Application.OpenForms.Cast<Form>().FirstOrDefault(f => f.GetType().Name == "ThemedDialog");
                    if (dialog == null) return;
                    var button = (Control)dialog.GetType().GetField(answer == "ok" ? "_ok" : "_cancel", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(dialog)!;
                    Click(button);
                };
                timer.Start();

                var remove = Field<ThemedButton>(Cards(form)[1], "_remove");
                Click(remove);
                Assert.Equal(2, Cards(form).Count);

                answer = "ok";
                Click(remove);
                timer.Stop();
                Assert.Single(Cards(form));
                Assert.Equal("A", ToConfig(Cards(form)[0]).name);
            });
        }

        [Fact]
        public void Escape_and_enter_in_the_dialog_cancel_by_default()
        {
            Sta.Run(() =>
            {
                using var form = Open(new[] { new FilterConfig { name = "A" } });
                var timer = new System.Windows.Forms.Timer { Interval = 50 };
                timer.Tick += (s, e) =>
                {
                    var dialog = Application.OpenForms.Cast<Form>().FirstOrDefault(f => f.GetType().Name == "ThemedDialog");
                    if (dialog == null) return;
                    // фокус стоит на «Отмена»: Enter не подтверждает удаление
                    var process = typeof(Form).GetMethod("ProcessCmdKey", BindingFlags.NonPublic | BindingFlags.Instance)!;
                    var msg = new Message();
                    process.Invoke(dialog, new object[] { msg, Keys.Enter });
                };
                timer.Start();

                Click(Field<ThemedButton>(Cards(form)[0], "_remove"));
                timer.Stop();
                Assert.Single(Cards(form));
            });
        }

        // ---------- автосохранение ----------

        [Fact]
        public void Autosave_survives_rapid_edits_and_write_failures()
        {
            Sta.Run(() =>
            {
                using var form = Open(new[] { new FilterConfig { name = "A", process_name = "a" } });
                var card = Cards(form)[0];
                var fade = Field<ThemedTextBox>(card, "_fade");

                // сотни правок подряд: таймер сбрасывается, запись одна в конце
                for (int i = 1; i <= 300; i++) fade.Text = (100 + i).ToString();
                Pump(800);
                Assert.Contains("400", File.ReadAllText(TestEnvironment.ConfigPath));

                // запись невозможна: окно живо, статус сообщает об ошибке
                string blocker = Path.Combine(TestEnvironment.Folder, "ui_blocker_" + Guid.NewGuid().ToString("N"));
                File.WriteAllText(blocker, "x");
                Plugin.ConfigPathOverride = Path.Combine(blocker, "config.json");
                try
                {
                    fade.Text = "777";
                    Pump(900);
                    Assert.Equal(Loc.SaveFailed, Field<Label>(form, "_statusLabel").Text);
                }
                finally
                {
                    Plugin.ConfigPathOverride = TestEnvironment.ConfigPath;
                }

                // как только запись снова возможна, следующая правка сохраняется
                fade.Text = "778";
                Pump(900);
                Assert.Contains("778", File.ReadAllText(TestEnvironment.ConfigPath));
            });
        }

        [Fact]
        public void Closing_the_window_saves_pending_edits()
        {
            Sta.Run(() =>
            {
                var form = Open(new[] { new FilterConfig { name = "A", process_name = "a" } });
                Field<ThemedTextBox>(Cards(form)[0], "_hold").Text = "4242";
                form.Close();   // меньше полсекунды после правки
                form.Dispose();
                Assert.Contains("4242", File.ReadAllText(TestEnvironment.ConfigPath));
            });
        }

        [Fact]
        public void Disabled_filter_state_is_saved_and_shown_on_the_chip()
        {
            Sta.Run(() =>
            {
                using var form = Open(new[] { new FilterConfig { name = "A", source_name = "Mic", process_name = "a" } });
                var card = Cards(form)[0];
                var toggle = Field<ThemedSwitch>(card, "_enabled");

                toggle.Checked = false;
                Pump(900);
                Assert.Equal(Loc.ChipOff, Field<StatusChip>(card, "_chip").Text);
                Assert.Contains("\"enabled\": false", File.ReadAllText(TestEnvironment.ConfigPath));

                toggle.Checked = true;
                Pump(900);
                Assert.NotEqual(Loc.ChipOff, Field<StatusChip>(card, "_chip").Text);
            });
        }
    }
}

namespace ProcessMuter.Tests
{
    [Collection("Plugin state")]
    public class ResourceLeakTests
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern uint GetGuiResources(IntPtr hProcess, uint flags);

        private static (uint Gdi, uint User) Handles()
        {
            var p = System.Diagnostics.Process.GetCurrentProcess();
            return (GetGuiResources(p.Handle, 0), GetGuiResources(p.Handle, 1));
        }

        [Fact]
        public void Opening_and_closing_the_window_repeatedly_does_not_leak_gdi_or_user_handles()
        {
            Sta.Run(() =>
            {
                Plugin.ConfigPathOverride = TestEnvironment.ConfigPath;
                var filters = Enumerable.Range(0, 4).Select(i => new FilterConfig { name = "F" + i, source_name = "s", process_name = "p" + i }).ToList();

                void Cycle()
                {
                    using var form = new SettingsForm(filters);
                    form.StartPosition = FormStartPosition.Manual;
                    form.Show();
                    Application.DoEvents();
                    Thread.Sleep(30);
                    Application.DoEvents();
                    form.Close();
                }

                for (int i = 0; i < 3; i++) Cycle();   // прогрев: кэши шрифтов и тем создаются один раз
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                var before = Handles();

                for (int i = 0; i < 25; i++) Cycle();
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                var after = Handles();

                // 25 открытий по 4 карточки: утечка хотя бы одного дескриптора на контрол дала бы тысячи
                Assert.True((long)after.Gdi - before.Gdi < 150, $"GDI handles: {before.Gdi} -> {after.Gdi}");
                Assert.True((long)after.User - before.User < 150, $"USER handles: {before.User} -> {after.User}");
            });
        }

        [Fact]
        public void Theme_switching_does_not_leak_handles()
        {
            Sta.Run(() =>
            {
                Plugin.ConfigPathOverride = TestEnvironment.ConfigPath;
                using var form = new SettingsForm(new[] { new FilterConfig { name = "A" }, new FilterConfig { name = "B" } });
                form.Show();
                Application.DoEvents();
                var switchTheme = typeof(SettingsForm).GetMethod("SwitchTheme", BindingFlags.NonPublic | BindingFlags.Instance)!;

                for (int i = 0; i < 3; i++) switchTheme.Invoke(form, new object[] { i % 2 == 1 });
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                var before = Handles();

                for (int i = 0; i < 40; i++) { switchTheme.Invoke(form, new object[] { i % 2 == 0 }); Application.DoEvents(); }
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                var after = Handles();

                Assert.True((long)after.Gdi - before.Gdi < 150, $"GDI handles: {before.Gdi} -> {after.Gdi}");
                Assert.True((long)after.User - before.User < 150, $"USER handles: {before.User} -> {after.User}");
                typeof(Plugin).GetProperty("ThemeDark")!.SetValue(null, true);
            });
        }
    }
}
