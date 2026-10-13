using ProcessMuterByChannel;
using Xunit;

namespace ProcessMuter.Tests
{
    /// <summary>Логика приглушения на модели цели (без OBS и без настоящих аудиосессий)</summary>
    public class EngineTests
    {
        private static DuckingEngine Create(FakeBackend fake, List<string>? log = null, IRecoveryStore? store = null, int enforceMs = 300)
            => new DuckingEngine(fake, (log ?? new List<string>()).Add, enforceMs, store);

        // ---------- задержка возврата и дребезг (время задаётся вручную) ----------

        [Fact]
        public void Unmute_waits_for_hold_time_after_last_sound()
        {
            var fake = new FakeBackend { Enforce = false };
            fake.Volumes["volume"] = 0.8f;
            var e = Create(fake);

            e.Tick(0, Levels.One("obs:M", over: true));
            Assert.True(e.IsMuted("obs:M"));

            e.Tick(1000, Levels.One("obs:M", over: false, holdMs: 1500));
            Assert.True(e.IsMuted("obs:M"));   // пауза короче задержки

            e.Tick(1499, Levels.One("obs:M", over: false, holdMs: 1500));
            Assert.True(e.IsMuted("obs:M"));

            e.Tick(1600, Levels.One("obs:M", over: false, holdMs: 1500));
            Assert.False(e.IsMuted("obs:M"));
        }

        [Fact]
        public void Pauses_shorter_than_hold_never_cause_unmute()
        {
            var fake = new FakeBackend { Enforce = false };
            fake.Volumes["volume"] = 0.8f;
            var log = new List<string>();
            var e = Create(fake, log);

            // звук и пауза чередуются каждые 100 мс в течение минуты
            for (long t = 0; t < 60_000; t += 100)
            {
                e.Tick(t, Levels.One("obs:M", over: (t / 100) % 2 == 0, holdMs: 1500));
            }

            Assert.Equal(1, log.Count(l => l.StartsWith("Mute ")));
            Assert.Equal(0, log.Count(l => l.StartsWith("Unmute ")));
        }

        [Fact]
        public void Zero_hold_unmutes_on_the_first_silent_tick()
        {
            var fake = new FakeBackend { Enforce = false };
            fake.Volumes["volume"] = 1f;
            var e = Create(fake);

            e.Tick(0, Levels.One("obs:M", over: true, holdMs: 0));
            e.Tick(100, Levels.One("obs:M", over: false, holdMs: 0));
            Assert.False(e.IsMuted("obs:M"));
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(int.MinValue)]
        public void Negative_hold_behaves_like_zero(int hold)
        {
            var fake = new FakeBackend { Enforce = false };
            fake.Volumes["volume"] = 1f;
            var e = Create(fake);

            e.Tick(0, Levels.One("obs:M", over: true, holdMs: hold));
            e.Tick(100, Levels.One("obs:M", over: false, holdMs: hold));
            Assert.False(e.IsMuted("obs:M"));
        }

        [Fact]
        public void Huge_hold_keeps_target_muted_without_overflow()
        {
            var fake = new FakeBackend { Enforce = false };
            fake.Volumes["volume"] = 1f;
            var e = Create(fake);

            e.Tick(0, Levels.One("obs:M", over: true, holdMs: int.MaxValue));
            e.Tick(10L * 24 * 3600 * 1000, Levels.One("obs:M", over: false, holdMs: int.MaxValue));   // десять суток тишины, а задержка почти 25 суток
            Assert.True(e.IsMuted("obs:M"));
        }

        // ---------- громкость ----------

        [Fact]
        public void Duck_and_restore_return_the_original_volume()
        {
            var fake = new FakeBackend { Enforce = false };
            fake.Volumes["volume"] = 0.8f;
            var e = Create(fake);

            e.Tick(0, Levels.One("obs:M", over: true));
            Assert.True(Wait.Until(() => fake.V() < 0.01f));

            e.Tick(10_000, Levels.One("obs:M", over: false, holdMs: 0));
            Assert.True(Wait.Until(() => Math.Abs(fake.V() - 0.8f) < 0.01f));
        }

        [Fact]
        public void Volume_above_one_is_kept_for_obs_sources()
        {
            var fake = new FakeBackend { Enforce = false };
            fake.Volumes["volume"] = 1.35f;
            var e = Create(fake);

            e.Tick(0, Levels.One("obs:M", over: true));
            Assert.True(Wait.Until(() => fake.V() < 0.01f));
            e.Tick(10_000, Levels.One("obs:M", over: false, holdMs: 0));
            Assert.True(Wait.Until(() => Math.Abs(fake.V() - 1.35f) < 0.01f));
        }

        [Fact]
        public void Duck_level_never_makes_a_quiet_target_louder()
        {
            var fake = new FakeBackend { Enforce = false };
            fake.Volumes["quiet"] = 0.1f;
            fake.Volumes["loud"] = 0.9f;
            var e = Create(fake);

            e.Tick(0, Levels.One("Music", over: true, duck: 0.3f));
            Assert.True(Wait.Until(() => Math.Abs(fake.V("loud") - 0.3f) < 0.01f));
            Assert.InRange(fake.V("quiet"), 0.09f, 0.11f);
        }

        [Fact]
        public void Return_level_below_duck_level_is_raised_to_duck_level()
        {
            var fake = new FakeBackend { Enforce = false };
            fake.Volumes["volume"] = 0.9f;
            var e = Create(fake);

            e.Tick(0, Levels.One("Music", over: true, duck: 0.2f, restore: 0.05f));
            Assert.True(Wait.Until(() => Math.Abs(fake.V() - 0.2f) < 0.01f));
            e.Tick(10_000, Levels.One("Music", over: false, holdMs: 0, duck: 0.2f, restore: 0.05f));
            Assert.True(Wait.Until(() => Math.Abs(fake.V() - 0.2f) < 0.02f));
            Assert.True(fake.V() >= 0.19f);
        }

        [Fact]
        public void Return_level_can_be_above_the_original_volume()
        {
            var fake = new FakeBackend { Enforce = false };
            fake.Volumes["volume"] = 0.3f;
            var e = Create(fake);

            e.Tick(0, Levels.One("Music", over: true, duck: 0.1f, restore: 0.7f));
            Assert.True(Wait.Until(() => fake.V() < 0.11f));
            e.Tick(10_000, Levels.One("Music", over: false, holdMs: 0, duck: 0.1f, restore: 0.7f));
            Assert.True(Wait.Until(() => Math.Abs(fake.V() - 0.7f) < 0.02f));
        }

        [Fact]
        public void New_session_of_a_ducked_process_is_ducked_too()
        {
            var fake = new FakeBackend();
            fake.Volumes["s1"] = 0.8f;
            var e = Create(fake, enforceMs: 50);

            e.Tick(0, Levels.One("Music", over: true));
            Assert.True(Wait.Until(() => fake.V("s1") < 0.01f));

            fake.Volumes["s2"] = 1.0f;   // смена трека: приложение открыло новую сессию с полной громкостью
            long t = 0;
            Assert.True(Wait.Until(() => { t += 60; e.Tick(t, Levels.One("Music", over: true)); return fake.V("s2") < 0.01f; }));
        }

        // ---------- крайние значения уровней ----------

        [Theory]
        [InlineData(float.NaN)]
        [InlineData(float.PositiveInfinity)]
        [InlineData(float.NegativeInfinity)]
        [InlineData(-5f)]
        [InlineData(1e30f)]
        public void Strange_duck_levels_never_throw(float duck)
        {
            var fake = new FakeBackend { Enforce = false };
            fake.Volumes["volume"] = 0.8f;
            var e = Create(fake);

            var ex = Record.Exception(() =>
            {
                e.Tick(0, Levels.One("obs:M", over: true, duck: duck));
                e.Tick(100, Levels.One("obs:M", over: true, duck: duck));
                Thread.Sleep(300);
                e.Tick(200, Levels.One("obs:M", over: false, holdMs: 0, duck: duck));
                e.ReleaseAll();
            });
            Assert.Null(ex);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-100)]
        [InlineData(int.MinValue)]
        public void Zero_or_negative_fade_still_fades_quickly(int fadeMs)
        {
            var fake = new FakeBackend { Enforce = false };
            fake.Volumes["volume"] = 0.8f;
            var e = Create(fake);

            e.Tick(0, Levels.One("obs:M", over: true, fadeMs: fadeMs));
            // шаг затухания не короче 10 мс: громкость доходит до нуля за доли секунды
            Assert.True(Wait.Until(() => fake.V() < 0.01f, 5000));
            e.ReleaseAll();
            Assert.InRange(fake.V(), 0.79f, 0.81f);
        }

        [Fact]
        public void Huge_fade_time_starts_fading_and_can_be_cancelled()
        {
            var fake = new FakeBackend { Enforce = false };
            fake.Volumes["volume"] = 0.8f;
            var e = Create(fake);

            e.Tick(0, Levels.One("obs:M", over: true, fadeMs: int.MaxValue));
            // первый шаг сразу, дальше шаги по несколько секунд: переполнения нет, громкость начала снижаться
            Assert.True(Wait.Until(() => fake.V() < 0.79f, 2000));
            Assert.True(fake.V() > 0.5f);   // и не обрушилась мгновенно

            // выгрузка плагина прерывает долгое затухание и возвращает громкость
            e.ReleaseAll();
            Assert.InRange(fake.V(), 0.79f, 0.81f);
        }

        // ---------- устойчивость к сбоям звуковой подсистемы ----------

        [Fact]
        public void Failure_on_one_target_does_not_stop_the_others()
        {
            var fake = new FakeBackend { Enforce = false };
            fake.PerTarget["bad"] = new Dictionary<string, float> { ["s"] = 1f };
            fake.PerTarget["good"] = new Dictionary<string, float> { ["s"] = 1f };
            fake.ThrowOnApply.Add("bad");
            var log = new List<string>();
            var e = Create(fake, log);

            var levels = new Dictionary<string, ProcessLevel>
            {
                ["bad"] = new ProcessLevel { Over = true, FadeMs = 100, HoldMs = 100, Source = "m", DuckLevel = 0f, RestoreLevel = float.PositiveInfinity },
                ["good"] = new ProcessLevel { Over = true, FadeMs = 100, HoldMs = 100, Source = "m", DuckLevel = 0f, RestoreLevel = float.PositiveInfinity },
            };

            Assert.Null(Record.Exception(() => e.Tick(0, levels)));
            Assert.True(Wait.Until(() => fake.V("good", "s") < 0.01f));
            Assert.True(e.IsMuted("good"));
        }

        [Fact]
        public void Failure_while_enforcing_is_contained_and_logged_once_per_interval()
        {
            var fake = new FakeBackend();
            fake.Volumes["s"] = 1f;
            var log = new List<string>();
            var e = Create(fake, log, enforceMs: 10);

            e.Tick(0, Levels.One("Music", over: true));
            Assert.True(Wait.Until(() => fake.V("s") < 0.01f));

            fake.ThrowOnApply.Add("Music");
            for (long t = 100; t < 5000; t += 100)
            {
                Assert.Null(Record.Exception(() => e.Tick(t, Levels.One("Music", over: true))));
            }

            // за 5 секунд тиков с постоянной ошибкой в журнале одна запись, а не пятьдесят
            Assert.InRange(log.Count(l => l.StartsWith("Error while processing")), 1, 1);
        }

        [Fact]
        public void ReleaseAll_continues_after_a_failure()
        {
            var fake = new FakeBackend { Enforce = false };
            fake.PerTarget["a"] = new Dictionary<string, float> { ["s"] = 0.8f };
            fake.PerTarget["b"] = new Dictionary<string, float> { ["s"] = 0.6f };
            var e = Create(fake);

            var levels = new Dictionary<string, ProcessLevel>
            {
                ["a"] = new ProcessLevel { Over = true, FadeMs = 100, HoldMs = 100, DuckLevel = 0f, RestoreLevel = float.PositiveInfinity },
                ["b"] = new ProcessLevel { Over = true, FadeMs = 100, HoldMs = 100, DuckLevel = 0f, RestoreLevel = float.PositiveInfinity },
            };
            e.Tick(0, levels);
            Assert.True(Wait.Until(() => fake.V("a", "s") < 0.01f && fake.V("b", "s") < 0.01f));

            fake.ThrowOnRestore.Add("a");
            Assert.Null(Record.Exception(() => e.ReleaseAll()));
            Assert.InRange(fake.V("b", "s"), 0.59f, 0.61f);   // вторая цель вернулась, хотя на первой сбой
        }

        [Fact]
        public void Recover_failure_does_not_stop_the_tick()
        {
            var fake = new FakeBackend { Enforce = false };
            fake.Volumes["volume"] = 0.0f;
            fake.ThrowOnRecover.Add("obs:M");
            string path = Path.Combine(TestEnvironment.Folder, "recover_throw.txt");
            File.WriteAllText(path, "0.8\tobs:M\n");
            var e = Create(fake, store: new RecoveryStore(path));

            Assert.Null(Record.Exception(() => e.Tick(0, Levels.One("obs:M", over: true))));
            Assert.False(e.IsMuted("obs:M"));   // пока громкость не возвращена, цель не глушится
        }

        // ---------- освобождение ----------

        [Fact]
        public void ReleaseUnused_restores_only_targets_that_left_the_filters()
        {
            var fake = new FakeBackend { Enforce = false };
            fake.PerTarget["a"] = new Dictionary<string, float> { ["s"] = 0.8f };
            fake.PerTarget["b"] = new Dictionary<string, float> { ["s"] = 0.6f };
            var e = Create(fake);

            e.Tick(0, new Dictionary<string, ProcessLevel>
            {
                ["a"] = new ProcessLevel { Over = true, FadeMs = 100, HoldMs = 100, DuckLevel = 0f, RestoreLevel = float.PositiveInfinity },
                ["b"] = new ProcessLevel { Over = true, FadeMs = 100, HoldMs = 100, DuckLevel = 0f, RestoreLevel = float.PositiveInfinity },
            });
            Assert.True(Wait.Until(() => fake.V("a", "s") < 0.01f && fake.V("b", "s") < 0.01f));

            e.ReleaseUnused(new HashSet<string> { "b" });
            Assert.InRange(fake.V("a", "s"), 0.79f, 0.81f);
            Assert.True(fake.V("b", "s") < 0.01f);
            Assert.False(e.IsMuted("a"));
            Assert.True(e.IsMuted("b"));
        }

        [Fact]
        public void Target_names_are_case_insensitive()
        {
            var fake = new FakeBackend { Enforce = false };
            fake.Volumes["volume"] = 0.8f;
            var log = new List<string>();
            var e = Create(fake, log);

            e.Tick(0, Levels.One("Spotify", over: true));
            e.Tick(100, Levels.One("SPOTIFY", over: true));
            Assert.Equal(1, log.Count(l => l.StartsWith("Mute ")));
        }

        [Fact]
        public void A_thousand_targets_tick_fast()
        {
            var fake = new FakeBackend { Enforce = false };
            var e = Create(fake);
            var levels = new Dictionary<string, ProcessLevel>();
            for (int i = 0; i < 1000; i++)
            {
                levels["t" + i] = new ProcessLevel { Over = false, FadeMs = 100, HoldMs = 100, DuckLevel = 0f, RestoreLevel = float.PositiveInfinity };
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < 20; i++) e.Tick(i * 100, levels);
            Assert.True(sw.ElapsedMilliseconds < 2000, $"took {sw.ElapsedMilliseconds} ms");
        }

        // ---------- восстановление после аварийного выхода ----------

        [Fact]
        public void Garbage_in_the_recovery_file_is_ignored()
        {
            string path = Path.Combine(TestEnvironment.Folder, "garbage_recovery.txt");
            File.WriteAllBytes(path, new byte[] { 0xFF, 0xFE, 0x00, 0x01, 0x0A, 0x09, 0x09, 0x0A, 0x41 });

            var fake = new FakeBackend { Enforce = false };
            var e = Create(fake, store: new RecoveryStore(path));
            Assert.Null(Record.Exception(() => e.Tick(0, Levels.One("obs:M", over: true))));
        }
    }
}
