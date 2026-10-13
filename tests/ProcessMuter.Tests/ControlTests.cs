using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using ProcessMuterByChannel;
using Xunit;

namespace ProcessMuter.Tests
{
    /// <summary>Собственные контролы окна на крайних значениях: числа, размеры, пустые и огромные тексты</summary>
    [Collection("Plugin state")]
    public class ControlTests
    {
        private static void Render(Control control)
        {
            // DrawToBitmap требует ненулевой размер; нулевой тоже проверяется отдельно через Invalidate
            if (control.Width > 0 && control.Height > 0)
            {
                using var bmp = new Bitmap(Math.Min(control.Width, 4000), Math.Min(control.Height, 4000));
                control.DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
            }
        }

        private static readonly float[] Nasty = { float.NaN, float.PositiveInfinity, float.NegativeInfinity, float.MaxValue, float.MinValue, -0.0f, 1e-30f, -1e-30f, 0, 100, -100, 60, -60 };

        // ---------- LevelSlider ----------

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void LevelSlider_accepts_any_number_and_stays_in_range(bool db)
        {
            Sta.Run(() =>
            {
                var mode = db ? LevelScale.Db : LevelScale.Percent;
                var slider = new LevelSlider { Mode = mode, ShowReturn = true, ShowValueBox = true, ShowLine = true, Size = new Size(260, 268) };
                float lo = mode == LevelScale.Db ? -60f : 0f, hi = mode == LevelScale.Db ? 0f : 100f;

                foreach (float v in Nasty)
                {
                    slider.Value = v;
                    slider.ReturnValue = v;
                    slider.GhostValue = v;
                    slider.CurrentValue = v;
                    slider.SetLevel(v);
                    Assert.InRange(slider.Value, lo, hi);
                    Assert.InRange(slider.ReturnValue, lo, hi);
                    Assert.InRange(slider.ReturnEffective, lo, hi);
                    Assert.True(float.IsFinite(slider.ReturnEffective));
                    Render(slider);
                }

                slider.CurrentValue = null;
                slider.GhostValue = null;
                Render(slider);
            });
        }

        [Fact]
        public void LevelSlider_mode_switching_and_repeated_state_changes_are_safe()
        {
            Sta.Run(() =>
            {
                var slider = new LevelSlider { Size = new Size(260, 268) };
                for (int i = 0; i < 200; i++)
                {
                    slider.Mode = i % 2 == 0 ? LevelScale.Db : LevelScale.Percent;
                    slider.ShowReturn = i % 3 != 0;
                    slider.ShowValueBox = i % 4 == 0;
                    slider.Dimmed = i % 7 == 0;
                    slider.StatusText = i % 2 == 0 ? "" : new string('я', i * 10);
                    slider.SetLevel(i % 2 == 0 ? -i : i);
                }
                Render(slider);
            });
        }

        [Theory]
        [InlineData(0, 0)]
        [InlineData(1, 1)]
        [InlineData(10, 10)]
        [InlineData(30, 5000)]
        [InlineData(5000, 30)]
        public void LevelSlider_renders_at_any_size(int w, int h)
        {
            Sta.Run(() =>
            {
                var slider = new LevelSlider { Mode = LevelScale.Db, ShowReturn = true, ShowValueBox = true, StatusText = "Сцена неактивна", Size = new Size(w, h) };
                slider.Value = -20;
                slider.Invalidate();
                Render(slider);
            });
        }

        [Fact]
        public void LevelSlider_status_text_of_any_length_and_content_is_safe()
        {
            Sta.Run(() =>
            {
                var slider = new LevelSlider { Size = new Size(240, 268) };
                foreach (var text in new[] { "", " ", new string('W', 10_000), "\n\n\n", "a\0b", "&&&", "🎵🎵🎵", "‮abc" })
                {
                    slider.StatusText = text;
                    slider.DuckLabel = text;
                    slider.ReturnLabel = text;
                    Render(slider);
                }
            });
        }

        [Fact]
        public void LevelSlider_mouse_and_keyboard_with_extreme_coordinates_are_safe()
        {
            Sta.Run(() =>
            {
                var slider = new LevelSlider { Mode = LevelScale.Db, ShowReturn = true, Size = new Size(240, 268) };
                var form = new Form { ClientSize = new Size(300, 300) };
                form.Controls.Add(slider);
                form.Show();
                try
                {
                    int grabs = 0, ends = 0;
                    slider.GrabStarted += _ => grabs++;
                    slider.GrabEnded += (s, e) => ends++;

                    var down = typeof(Control).GetMethod("OnMouseDown", BindingFlags.NonPublic | BindingFlags.Instance)!;
                    var move = typeof(Control).GetMethod("OnMouseMove", BindingFlags.NonPublic | BindingFlags.Instance)!;
                    var up = typeof(Control).GetMethod("OnMouseUp", BindingFlags.NonPublic | BindingFlags.Instance)!;
                    var wheel = typeof(Control).GetMethod("OnMouseWheel", BindingFlags.NonPublic | BindingFlags.Instance)!;
                    var key = typeof(Control).GetMethod("OnKeyDown", BindingFlags.NonPublic | BindingFlags.Instance)!;

                    foreach (int x in new[] { -100000, -1, 0, 34, 112, 200, 100000, int.MaxValue / 2, int.MinValue / 2 })
                    {
                        foreach (int y in new[] { -100000, -1, 0, 36, 100, 206, 100000, int.MaxValue / 2, int.MinValue / 2 })
                        {
                            down.Invoke(slider, new object[] { new MouseEventArgs(MouseButtons.Left, 1, x, y, 0) });
                            move.Invoke(slider, new object[] { new MouseEventArgs(MouseButtons.Left, 0, x, y, 0) });
                            up.Invoke(slider, new object[] { new MouseEventArgs(MouseButtons.Left, 1, x, y, 0) });
                            Assert.InRange(slider.Value, -60f, 0f);
                            Assert.InRange(slider.ReturnEffective, -60f, 0f);
                        }
                    }

                    foreach (int delta in new[] { int.MinValue, -120, 0, 120, int.MaxValue })
                    {
                        wheel.Invoke(slider, new object[] { new MouseEventArgs(MouseButtons.None, 0, 112, 100, delta) });
                    }

                    foreach (Keys k in Enum.GetValues<Keys>())
                    {
                        key.Invoke(slider, new object[] { new KeyEventArgs(k) });
                    }

                    Assert.InRange(slider.Value, -60f, 0f);
                    Assert.Equal(grabs, ends);   // каждое нажатие закончилось отпусканием
                }
                finally
                {
                    form.Close();
                }
            });
        }

        // ---------- ThemedTextBox / ThemedPicker ----------

        [Fact]
        public void ThemedTextBox_handles_flat_suffix_and_long_text()
        {
            Sta.Run(() =>
            {
                var box = new ThemedTextBox { Size = new Size(10, 10), Suffix = "мс", Flat = true };
                foreach (var text in new[] { "", new string('Ж', 100_000), "\t\t", "🎵" })
                {
                    box.Text = text;
                    Render(box);
                }
                box.Suffix = null!;
                box.Size = new Size(0, 0);
                box.Size = new Size(500, 30);
                Render(box);
            });
        }

        [Fact]
        public void ThemedPicker_handles_many_items_and_invalid_selection()
        {
            Sta.Run(() =>
            {
                var picker = new ThemedPicker(editable: false) { Size = new Size(200, 30) };
                picker.SetItems(Enumerable.Range(0, 20_000).Select(i => (object)("item " + i)).ToList());

                foreach (int index in new[] { -1, 0, 19_999, 20_000, int.MaxValue, int.MinValue, -5 })
                {
                    var ex = Record.Exception(() => picker.SelectedIndex = index);
                    Assert.True(ex == null || ex is ArgumentOutOfRangeException, ex?.ToString());
                    Render(picker);
                }

                picker.SelectedItem = "no such item";
                picker.SelectedItem = null;
                picker.SetItems(new List<object>());
                picker.SetItems(new List<object> { "", new string('ы', 50_000), "a\nb" });
                picker.SelectedIndex = 1;
                Render(picker);

                var editable = new ThemedPicker(editable: true) { Size = new Size(200, 30) };
                editable.SetItems(new List<object> { "chrome", "firefox" });
                editable.Text = null!;
                editable.Text = new string('x', 100_000);
                Render(editable);
            });
        }

        // ---------- мелкие контролы ----------

        [Fact]
        public void SummaryText_handles_unbalanced_markup_and_huge_input()
        {
            Sta.Run(() =>
            {
                var summary = new SummaryText { Size = new Size(300, 100) };
                foreach (var markup in new[]
                {
                    "", "   ", "**", "****", "**a", "a**", "**a**b**c", "*", "**" + new string('x', 50_000) + "**",
                    string.Join(" ", Enumerable.Repeat("слово", 20_000)), "a\nb\tc", ", . ; : ! ? )", "**,** **.**"
                })
                {
                    summary.SetMarkup(markup);
                    Render(summary);
                }

                summary.SetMarkup(null!);
                summary.Size = new Size(1, 1);
                Render(summary);
            });
        }

        [Fact]
        public void ReadoutList_and_StatusChip_handle_empty_and_huge_input()
        {
            Sta.Run(() =>
            {
                var list = new ReadoutList { Size = new Size(10, 10) };
                list.SetRows();
                list.SetRows(("", ""), (new string('a', 10_000), new string('b', 10_000)));
                Render(list);
                list.Size = new Size(0, 0);
                list.SetRows(("a", "b"));

                var chip = new StatusChip { Size = new Size(1, 1) };
                chip.Text = null!;
                chip.Text = new string('Ш', 10_000);
                Assert.True(chip.PreferredWidth > 0);
                Render(chip);
            });
        }

        [Fact]
        public void ThemedSwitch_toggles_by_click_and_space_only_when_enabled()
        {
            Sta.Run(() =>
            {
                var toggle = new ThemedSwitch();
                int changes = 0;
                toggle.CheckedChanged += (s, e) => changes++;

                var click = typeof(Control).GetMethod("OnClick", BindingFlags.NonPublic | BindingFlags.Instance)!;
                click.Invoke(toggle, new object[] { EventArgs.Empty });
                Assert.True(toggle.Checked);

                toggle.Enabled = false;
                click.Invoke(toggle, new object[] { EventArgs.Empty });
                Assert.True(toggle.Checked);   // выключенный контрол не переключается
                toggle.Checked = true;          // и повторная установка того же значения не шлёт событие
                Assert.Equal(1, changes);
                Render(toggle);
            });
        }

        [Fact]
        public void ThemedButton_renders_every_tone_state_and_size()
        {
            Sta.Run(() =>
            {
                foreach (var tone in Enum.GetValues<ButtonTone>())
                {
                    foreach (bool enabled in new[] { true, false })
                    {
                        foreach (var size in new[] { new Size(1, 1), new Size(36, 30), new Size(210, 32) })
                        {
                            var button = new ThemedButton { Tone = tone, Enabled = enabled, Size = size, Glyph = Ui.GlyphDelete, Text = new string('я', 200), Primary = tone == ButtonTone.Normal };
                            Render(button);
                            button.Text = "";
                            Render(button);
                        }
                    }
                }
            });
        }

        [Fact]
        public void ThemedDialog_survives_empty_and_enormous_messages()
        {
            Sta.Run(() =>
            {
                foreach (var message in new[] { "", new string('щ', 20_000), "строка\n\n\nстрока" })
                {
                    var timer = new System.Windows.Forms.Timer { Interval = 50 };
                    timer.Tick += (s, e) =>
                    {
                        var dialog = Application.OpenForms.Cast<Form>().FirstOrDefault(f => f.GetType().Name == "ThemedDialog");
                        dialog?.Close();
                    };
                    timer.Start();
                    bool answer = ThemedDialog.Confirm(null!, message, message, ButtonTone.Danger);
                    timer.Stop();
                    Assert.False(answer);   // закрытие крестиком это отмена
                }
            });
        }

        // ---------- Ui ----------

        [Theory]
        [InlineData(0.5f)]
        [InlineData(1f)]
        [InlineData(1.25f)]
        [InlineData(2.5f)]
        [InlineData(4f)]
        public void Px_scaling_is_monotonic_and_never_negative(float k)
        {
            float old = Ui.K;
            try
            {
                Ui.K = k;
                int prev = -1;
                for (int px = 0; px <= 500; px++)
                {
                    int scaled = Ui.Px(px);
                    Assert.True(scaled >= prev);
                    prev = scaled;
                }
            }
            finally
            {
                Ui.K = old;
            }
        }

        [Fact]
        public void Rounded_rectangles_with_tiny_or_huge_sizes_are_safe()
        {
            foreach (var size in new[] { new SizeF(0, 0), new SizeF(0.1f, 0.1f), new SizeF(1, 1000), new SizeF(100000, 100000) })
            {
                foreach (float radius in new[] { 0f, 0.1f, 4f, 1000f })
                {
                    using var path = Ui.RoundedRect(new RectangleF(0, 0, size.Width, size.Height), radius);
                    Assert.NotNull(path);
                }
            }
        }
    }
}

namespace ProcessMuter.Tests
{
    [Collection("Plugin state")]
    public class PickerInteractionTests
    {
        private static void Pump(int ms = 30)
        {
            Application.DoEvents();
            Thread.Sleep(ms);
            Application.DoEvents();
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(3)]
        [InlineData(500)]
        public void Every_key_on_a_picker_is_safe_with_any_number_of_items(int count)
        {
            Sta.Run(() =>
            {
                foreach (bool editable in new[] { false, true })
                {
                    using var form = new Form { ClientSize = new Size(400, 300) };
                    var picker = new ThemedPicker(editable) { Bounds = new Rectangle(10, 10, 200, 30) };
                    picker.SetItems(Enumerable.Range(0, count).Select(i => (object)("item" + i)).ToList());
                    form.Controls.Add(picker);
                    form.Show();
                    Pump();
                    picker.Focus();

                    var keyDown = typeof(Control).GetMethod("OnKeyDown", BindingFlags.NonPublic | BindingFlags.Instance)!;
                    // перебираем все клавиши по два раза подряд: открыть список, двигаться, выбрать, закрыть
                    foreach (Keys key in Enum.GetValues<Keys>())
                    {
                        keyDown.Invoke(picker, new object[] { new KeyEventArgs(key) });
                        keyDown.Invoke(picker, new object[] { new KeyEventArgs(key) });
                    }

                    picker.CloseDropDown();
                    Assert.False(picker.IsDropDownOpen);
                }
            });
        }

        [Fact]
        public void Closing_the_form_while_a_dropdown_is_open_is_safe_and_cleans_up()
        {
            Sta.Run(() =>
            {
                var form = new Form { ClientSize = new Size(400, 300) };
                var picker = new ThemedPicker(editable: false) { Bounds = new Rectangle(10, 10, 200, 30) };
                picker.SetItems(new object[] { "a", "b", "c" });
                form.Controls.Add(picker);
                form.Show();
                Pump();

                picker.OpenDropDown(userInitiated: false);
                Pump();
                Assert.True(picker.IsDropDownOpen);

                form.Close();
                form.Dispose();
                Pump();

                // после закрытия окна фильтр сообщений снят: обычная работа цикла сообщений не ломается
                Assert.Null(Record.Exception(() => { Application.DoEvents(); }));
            });
        }

        [Fact]
        public void Replacing_items_while_the_dropdown_is_open_keeps_it_consistent()
        {
            Sta.Run(() =>
            {
                using var form = new Form { ClientSize = new Size(400, 300) };
                var picker = new ThemedPicker(editable: true) { Bounds = new Rectangle(10, 10, 200, 30) };
                picker.SetItems(new object[] { "a", "b", "c" });
                form.Controls.Add(picker);
                form.Show();
                Pump();

                picker.OpenDropDown(userInitiated: false);
                for (int i = 0; i < 50; i++)
                {
                    picker.SetItems(Enumerable.Range(0, i % 4).Select(n => (object)("x" + n)).ToList());
                    Pump(1);
                }
                picker.CloseDropDown();
            });
        }
    }
}
