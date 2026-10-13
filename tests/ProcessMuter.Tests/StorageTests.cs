using System.Text;
using ProcessMuterByChannel;
using Xunit;

namespace ProcessMuter.Tests
{
    /// <summary>Файлы плагина: атомарная запись, файл восстановления, чтение и запись config.json</summary>
    public class AtomicFileTests
    {
        private static string NewPath(string name) => Path.Combine(TestEnvironment.Folder, Guid.NewGuid().ToString("N") + "_" + name);

        [Fact]
        public void Writes_new_file_and_overwrites_existing_without_leftovers()
        {
            string path = NewPath("a.txt");
            AtomicFile.WriteAllText(path, "first");
            AtomicFile.WriteAllText(path, "second");

            Assert.Equal("second", File.ReadAllText(path));
            Assert.False(File.Exists(path + ".tmp"));
        }

        [Fact]
        public void Failure_leaves_the_old_file_untouched()
        {
            string path = NewPath("locked.txt");
            File.WriteAllText(path, "old");

            // каталог на месте временного файла: запись в него невозможна
            Directory.CreateDirectory(path + ".tmp");
            try
            {
                Assert.ThrowsAny<Exception>(() => AtomicFile.WriteAllText(path, "new"));
                Assert.Equal("old", File.ReadAllText(path));
            }
            finally
            {
                Directory.Delete(path + ".tmp");
            }
        }

        [Fact]
        public void Unicode_text_is_preserved()
        {
            string path = NewPath("u.txt");
            AtomicFile.WriteAllText(path, "Привет 🎵 мир", Encoding.UTF8);
            Assert.Equal("Привет 🎵 мир", File.ReadAllText(path, Encoding.UTF8));
        }

        [Fact]
        public void Missing_directory_is_reported_as_exception()
        {
            string path = Path.Combine(TestEnvironment.Folder, "no_such_dir_" + Guid.NewGuid().ToString("N"), "x.txt");
            Assert.ThrowsAny<Exception>(() => AtomicFile.WriteAllText(path, "x"));
        }

        [Fact]
        public void Short_lock_by_another_program_is_waited_out()
        {
            string path = NewPath("busy.txt");
            File.WriteAllText(path, "old");

            using var holder = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            // отдельный поток, а не пул: при параллельном прогоне других проверок пул может опоздать с освобождением файла
            var release = new Thread(() => { Thread.Sleep(80); holder.Dispose(); });
            release.Start();

            AtomicFile.WriteAllText(path, "new");
            release.Join();
            Assert.Equal("new", File.ReadAllText(path));
        }

        [Fact]
        public void Permanent_lock_is_reported_and_leaves_old_content_and_no_temp_file()
        {
            string path = NewPath("locked_forever.txt");
            File.WriteAllText(path, "old");

            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Assert.ThrowsAny<Exception>(() => AtomicFile.WriteAllText(path, "new"));
            }

            Assert.Equal("old", File.ReadAllText(path));
            Assert.False(File.Exists(path + ".tmp"));
        }
    }

    public class RecoveryStoreTests
    {
        private static string NewPath() => Path.Combine(TestEnvironment.Folder, "rec_" + Guid.NewGuid().ToString("N") + ".txt");

        [Fact]
        public void Roundtrip_keeps_keys_and_volumes()
        {
            string path = NewPath();
            var store = new RecoveryStore(path);
            store.Save(new Dictionary<string, float> { ["obs:Музыка"] = 0.8f, ["chrome"] = 1.35f });

            var loaded = new RecoveryStore(path).Load();
            Assert.Equal(2, loaded.Count);
            Assert.Equal(0.8f, loaded["obs:Музыка"]);
            Assert.Equal(1.35f, loaded["chrome"]);
        }

        [Fact]
        public void Saving_nothing_deletes_the_file()
        {
            string path = NewPath();
            var store = new RecoveryStore(path);
            store.Save(new Dictionary<string, float> { ["a"] = 1f });
            Assert.True(File.Exists(path));

            store.Save(new Dictionary<string, float>());
            Assert.False(File.Exists(path));
        }

        [Fact]
        public void Missing_or_empty_file_gives_no_entries()
        {
            Assert.Empty(new RecoveryStore(NewPath()).Load());

            string empty = NewPath();
            File.WriteAllText(empty, "");
            Assert.Empty(new RecoveryStore(empty).Load());
        }

        [Theory]
        [InlineData("NaN\tobs:A\n")]
        [InlineData("Infinity\tobs:A\n")]
        [InlineData("-Infinity\tobs:A\n")]
        [InlineData("abc\tobs:A\n")]
        [InlineData("\tobs:A\n")]
        [InlineData("0.5\n")]
        [InlineData("0.5\t\n")]
        [InlineData("no tab at all\n")]
        [InlineData("\n\n\n")]
        public void Broken_lines_are_skipped(string content)
        {
            string path = NewPath();
            File.WriteAllText(path, content);
            Assert.Empty(new RecoveryStore(path).Load());
        }

        [Theory]
        [InlineData("1e30", RecoveryStore.MaxVolume)]
        [InlineData("1000", RecoveryStore.MaxVolume)]
        [InlineData("-5", 0f)]
        [InlineData("0", 0f)]
        [InlineData("1.35", 1.35f)]
        public void Volumes_are_clamped_so_a_corrupt_file_cannot_blast_the_stream(string text, float expected)
        {
            string path = NewPath();
            File.WriteAllText(path, text + "\tobs:A\n");
            Assert.Equal(expected, new RecoveryStore(path).Load()["obs:A"]);
        }

        [Fact]
        public void Good_lines_survive_next_to_bad_ones_and_crlf_is_handled()
        {
            string path = NewPath();
            File.WriteAllText(path, "garbage\r\n0.5\tobs:A\r\nNaN\tobs:B\r\n0.25\tchrome\r\n");
            var loaded = new RecoveryStore(path).Load();
            Assert.Equal(2, loaded.Count);
            Assert.Equal(0.5f, loaded["obs:A"]);
            Assert.Equal(0.25f, loaded["chrome"]);
        }

        [Fact]
        public void Line_breaks_inside_a_key_do_not_corrupt_the_file()
        {
            string path = NewPath();
            new RecoveryStore(path).Save(new Dictionary<string, float> { ["obs:two\nlines"] = 0.5f, ["obs:ok"] = 0.7f });
            var loaded = new RecoveryStore(path).Load();
            Assert.Equal(0.7f, loaded["obs:ok"]);
            Assert.Equal(2, loaded.Count);
        }

        [Fact]
        public void Unwritable_location_does_not_throw()
        {
            // путь, в котором «каталог» на самом деле файл
            string blocker = NewPath();
            File.WriteAllText(blocker, "x");
            var store = new RecoveryStore(Path.Combine(blocker, "rec.txt"));
            Assert.Null(Record.Exception(() => store.Save(new Dictionary<string, float> { ["a"] = 1f })));
            Assert.Empty(store.Load());
        }

        [Fact]
        public void Parallel_saves_leave_a_readable_file()
        {
            string path = NewPath();
            var store = new RecoveryStore(path);
            Parallel.For(0, 200, i => store.Save(new Dictionary<string, float> { ["k" + (i % 5)] = i / 10f }));

            var loaded = store.Load();
            Assert.Single(loaded);
            Assert.False(File.Exists(path + ".tmp"));
        }
    }

    /// <summary>Чтение и запись config.json через Plugin и применение фильтров без OBS</summary>
    [Collection("Plugin state")]
    public class PluginConfigIoTests
    {
        private static void Reset()
        {
            Plugin.ConfigPathOverride = TestEnvironment.ConfigPath;
            foreach (var f in Directory.GetFiles(TestEnvironment.Folder, "config.json*")) File.Delete(f);
            AudioMuterManager.SetFilters(Array.Empty<FilterConfig>());
        }

        [Fact]
        public void Missing_config_is_not_an_error()
        {
            Reset();
            Assert.Null(Record.Exception(() => Plugin.LoadConfig()));
            Assert.Empty(AudioMuterManager.GetFilters());
        }

        [Fact]
        public void Save_then_load_restores_filters_and_theme()
        {
            Reset();
            var filters = new List<FilterConfig>
            {
                new FilterConfig { name = "Музыка", source_name = "Mic", process_name = "chrome.exe", threshold_db = -33, fade_ms = 250, hold_ms = 900, enabled = false },
                new FilterConfig { name = "Донаты", source_name = "Alerts", target_type = FilterConfig.TargetObsSource, target_source = "BGM" }
            };

            Assert.True(Plugin.SaveConfig(filters));
            Assert.False(File.Exists(TestEnvironment.ConfigPath + ".tmp"));

            AudioMuterManager.SetFilters(Array.Empty<FilterConfig>());
            Plugin.LoadConfig();

            var loaded = AudioMuterManager.GetFilters();
            Assert.Equal(2, loaded.Count);
            Assert.Equal("chrome", loaded[0].process_name);   // «.exe» снято
            Assert.False(loaded[0].enabled);
            Assert.Equal(-33f, loaded[0].threshold_db);
            Assert.Equal("BGM", loaded[1].target_source);
        }

        [Fact]
        public void Broken_config_is_backed_up_and_does_not_throw()
        {
            Reset();
            File.WriteAllText(TestEnvironment.ConfigPath, "{ \"filters\": [ { \"name\": \"важное\"");   // оборванный файл

            Assert.Null(Record.Exception(() => Plugin.LoadConfig()));
            Assert.Empty(AudioMuterManager.GetFilters());
            Assert.True(File.Exists(TestEnvironment.ConfigPath + ".broken"));
            Assert.Contains("важное", File.ReadAllText(TestEnvironment.ConfigPath + ".broken"));

            // и следующее сохранение из окна не уничтожает копию
            Plugin.SaveConfig(new List<FilterConfig> { new FilterConfig { name = "new" } });
            Assert.Contains("важное", File.ReadAllText(TestEnvironment.ConfigPath + ".broken"));
        }

        [Fact]
        public void Config_with_null_filter_loads_the_others()
        {
            Reset();
            File.WriteAllText(TestEnvironment.ConfigPath, "{\"filters\":[null,{\"name\":\"ok\",\"source_name\":\"m\"}]}");
            Plugin.LoadConfig();
            Assert.Equal("ok", AudioMuterManager.GetFilters().Single().name);
        }

        [Fact]
        public void Save_to_an_unwritable_location_reports_failure_but_still_applies_filters()
        {
            Reset();
            string blocker = Path.Combine(TestEnvironment.Folder, "blocker_" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(blocker, "x");
            Plugin.ConfigPathOverride = Path.Combine(blocker, "config.json");   // «каталог» - это файл
            try
            {
                bool written = true;
                Assert.Null(Record.Exception(() => written = Plugin.SaveConfig(new List<FilterConfig> { new FilterConfig { name = "kept" } })));
                Assert.False(written);
                Assert.Equal("kept", AudioMuterManager.GetFilters().Single().name);
            }
            finally
            {
                Plugin.ConfigPathOverride = TestEnvironment.ConfigPath;
            }
        }

        [Fact]
        public void SetFilters_tolerates_nulls_garbage_and_duplicates_without_obs()
        {
            Reset();
            var list = new List<FilterConfig>
            {
                null!,
                new FilterConfig { name = null!, source_name = null!, process_name = null!, threshold_db = float.NaN, duck_percent = float.NaN, fade_ms = int.MinValue, hold_ms = int.MaxValue },
                new FilterConfig { name = "dup", source_name = "m", process_name = "x" },
                new FilterConfig { name = "dup", source_name = "m", process_name = "x" },
            };

            Assert.Null(Record.Exception(() => AudioMuterManager.SetFilters(list)));
            var filters = AudioMuterManager.GetFilters();
            Assert.Equal(3, filters.Count);   // null отброшен, остальные (в том числе повторы) сохранены
            Assert.Equal(-60f, filters[0].threshold_db);
            Assert.Equal(1, filters[0].fade_ms);
            Assert.Equal(FilterConfig.MaxHoldMs, filters[0].hold_ms);
        }

        [Fact]
        public void Applying_filters_after_Stop_does_not_touch_obs()
        {
            Reset();
            AudioMuterManager.Stop();   // плагин выгружается, а окно настроек ещё открыто и сохраняет правку
            var ex = Record.Exception(() => AudioMuterManager.SetFilters(new[] { new FilterConfig { name = "late", source_name = "Mic" } }));
            Assert.Null(ex);   // без obs.dll привязка к источнику бросила бы исключение: после Stop её быть не должно
            Assert.Equal("late", AudioMuterManager.GetFilters().Single().name);
        }

        [Fact]
        public void Many_filters_are_applied_quickly()
        {
            Reset();
            var list = Enumerable.Range(0, 3000).Select(i => new FilterConfig { name = "f" + i, source_name = "s" + i, process_name = "p" + i }).ToList();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            AudioMuterManager.SetFilters(list);
            Assert.True(sw.ElapsedMilliseconds < 3000, $"took {sw.ElapsedMilliseconds} ms");
            Assert.Equal(3000, AudioMuterManager.GetFilters().Count);
        }
    }

    [CollectionDefinition("Plugin state", DisableParallelization = true)]
    public class PluginStateCollection { }
}

namespace ProcessMuter.Tests
{
    /// <summary>Таймер плагина должен переживать любые сбои: исключение в его потоке завершило бы процесс OBS</summary>
    [Collection("Plugin state")]
    public class TimerResilienceTests
    {
        [Fact]
        public void Running_timer_survives_missing_obs_and_stops_cleanly()
        {
            Plugin.ConfigPathOverride = TestEnvironment.ConfigPath;
            AudioMuterManager.SetFilters(new[]
            {
                new FilterConfig { name = "a", source_name = "Mic", target_type = FilterConfig.TargetObsSource, target_source = "BGM" },
                new FilterConfig { name = "b", source_name = "Mic2", process_name = "chrome" },
            });

            AudioMuterManager.Start();
            try
            {
                // за 1.2 с проходит около десяти проверок, на каждой привязка к источнику падает (obs.dll нет)
                Thread.Sleep(1200);

                // окно настроек в это время меняет фильтры: блокировка не должна зависнуть
                var sw = System.Diagnostics.Stopwatch.StartNew();
                AudioMuterManager.SetFilters(new[] { new FilterConfig { name = "c", source_name = "Mic3" } });
                Assert.True(sw.ElapsedMilliseconds < 2000);
                Thread.Sleep(300);
            }
            finally
            {
                AudioMuterManager.Stop();
            }

            // если бы исключение вышло из таймера, процесс тестов уже был бы завершён; дошли сюда - значит пережили
            Assert.Equal("c", AudioMuterManager.GetFilters().Single().name);
        }

        [Fact]
        public void Start_and_Stop_can_be_repeated()
        {
            for (int i = 0; i < 5; i++)
            {
                AudioMuterManager.Start();
                Thread.Sleep(50);
                AudioMuterManager.Stop();
            }
        }
    }
}

namespace ProcessMuter.Tests
{
    [Collection("Plugin state")]
    public class TimerExceptionTests
    {
        [Fact]
        public void Exception_inside_a_tick_does_not_kill_the_process_or_the_timer()
        {
            int calls = 0;
            AudioMuterManager.TickHook = () => { calls++; throw new InvalidOperationException("boom"); };
            AudioMuterManager.Start();
            try
            {
                // исключение на каждой проверке: если бы оно вышло из таймера, процесс тестов завершился бы
                Thread.Sleep(700);
                Assert.True(calls >= 3, $"timer stopped after {calls} calls");

                // блокировка после сбоя освобождена: фильтры по-прежнему можно менять
                var sw = System.Diagnostics.Stopwatch.StartNew();
                AudioMuterManager.SetFilters(new[] { new FilterConfig { name = "after-failure" } });
                Assert.True(sw.ElapsedMilliseconds < 1000);
            }
            finally
            {
                AudioMuterManager.TickHook = null;
                AudioMuterManager.Stop();
            }
        }

        [Fact]
        public void Slow_tick_does_not_queue_up_callbacks()
        {
            int concurrent = 0, maxConcurrent = 0, calls = 0;
            AudioMuterManager.TickHook = () =>
            {
                int now = Interlocked.Increment(ref concurrent);
                int seen;
                while ((seen = Volatile.Read(ref maxConcurrent)) < now && Interlocked.CompareExchange(ref maxConcurrent, now, seen) != seen) { }
                Interlocked.Increment(ref calls);
                Thread.Sleep(450);   // каждая проверка длится в несколько раз дольше периода таймера
                Interlocked.Decrement(ref concurrent);
            };
            AudioMuterManager.Start();
            try
            {
                Thread.Sleep(1600);
            }
            finally
            {
                AudioMuterManager.TickHook = null;
                AudioMuterManager.Stop();
            }

            Assert.Equal(1, maxConcurrent);   // проверки не накладываются друг на друга
            Assert.InRange(calls, 2, 5);      // и не копятся в очереди: за 1.6 с их было не больше четырёх
        }
    }
}

namespace ProcessMuter.Tests
{
    /// <summary>Всё сразу из разных потоков: таймер плагина, окно настроек (сохранение, предпросмотр, опрос состояния), выгрузка</summary>
    [Collection("Plugin state")]
    public class ConcurrencyStressTests
    {
        [Fact]
        public void Hammering_the_manager_from_many_threads_does_not_deadlock_or_throw()
        {
            Plugin.ConfigPathOverride = TestEnvironment.ConfigPath;
            var errors = new System.Collections.Concurrent.ConcurrentBag<Exception>();
            using var stop = new CancellationTokenSource();

            Thread Spawn(Action body) => new Thread(() =>
            {
                try { while (!stop.IsCancellationRequested) body(); }
                catch (Exception ex) { errors.Add(ex); }
            }) { IsBackground = true };

            int n = 0;
            var preview = new VolumePreview();
            var threads = new List<Thread>
            {
                // окно: автосохранение
                Spawn(() => Plugin.SaveConfig(new List<FilterConfig>
                {
                    new FilterConfig { name = "a" + (n++ % 7), source_name = "Mic", process_name = "p" + (n % 3), threshold_db = -(n % 60) },
                    new FilterConfig { name = "b", source_name = "Mic2", target_type = FilterConfig.TargetObsSource, target_source = "BGM" },
                })),
                Spawn(() => AudioMuterManager.SetFilters(Array.Empty<FilterConfig>())),
                // окно: опрос состояния 25 раз в секунду по каждой карточке
                Spawn(() => { AudioMuterManager.TryGetDuckedOriginal("p1", out _); AudioMuterManager.GetFilters(); Thread.Sleep(1); }),
                // окно: прослушивание
                Spawn(() => { preview.SetLevel("p1", 0.3f); preview.Hold(); preview.Tick(); preview.Release(); }),
                // выгрузка и повторный запуск плагина
                Spawn(() => { AudioMuterManager.Start(); Thread.Sleep(20); AudioMuterManager.Stop(); Thread.Sleep(5); }),
            };

            threads.ForEach(t => t.Start());
            Thread.Sleep(3000);
            stop.Cancel();

            foreach (var t in threads)
            {
                Assert.True(t.Join(10_000), "a thread did not finish: probable deadlock");
            }

            AudioMuterManager.Stop();
            Assert.Empty(errors);
        }
    }
}
