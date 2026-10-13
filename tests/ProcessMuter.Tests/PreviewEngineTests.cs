using ProcessMuterByChannel;
using Xunit;

namespace ProcessMuter.Tests
{
    /// <summary>Предпросмотр громкости в окне и настоящее приглушение одной и той же цели одновременно</summary>
    [Collection("Plugin state")]
    public class PreviewEngineTests : IDisposable
    {
        private readonly IVolumeBackend _saved = VolumePreview.Backend;
        private readonly FakeBackend _fake = new FakeBackend { Enforce = false };

        public PreviewEngineTests()
        {
            VolumePreview.Backend = _fake;
            _fake.Volumes["volume"] = 0.8f;
        }

        public void Dispose()
        {
            VolumePreview.RestoreAll();
            VolumePreview.Backend = _saved;
        }

        private DuckingEngine Engine() => new DuckingEngine(_fake, _ => { }, 0, null, 600000, new PreviewRegistry());

        [Fact]
        public void Preview_alone_lowers_and_then_restores_the_original()
        {
            var preview = new VolumePreview();
            preview.SetLevel("Music", 0.2f);
            preview.Hold();
            Assert.InRange(_fake.V(), 0.19f, 0.21f);

            preview.Release();
            Assert.InRange(_fake.V(), 0.79f, 0.81f);
        }

        [Fact]
        public void Preview_return_level_can_exceed_the_original_and_is_undone()
        {
            var preview = new VolumePreview();
            preview.SetLevel("Music", 1.0f, isReturn: true);
            Assert.InRange(_fake.V(), 0.99f, 1.01f);
            preview.Cancel();
            Assert.InRange(_fake.V(), 0.79f, 0.81f);
        }

        [Fact]
        public void Engine_starting_to_duck_during_a_preview_keeps_the_true_original_volume()
        {
            var engine = Engine();
            var preview = new VolumePreview();

            preview.SetLevel("Music", 0.2f);   // пользователь держит ползунок: громкость 0.2
            preview.Hold();
            Assert.InRange(_fake.V(), 0.19f, 0.21f);

            engine.Tick(0, Levels.One("Music", over: true));   // в это время в микрофоне появился звук
            Assert.True(Wait.Until(() => _fake.V() < 0.01f));

            preview.Release();                                  // ползунок отпущен: это не должно снять приглушение
            Assert.True(_fake.V() < 0.01f, $"volume after release: {_fake.V()}");

            engine.Tick(10_000, Levels.One("Music", over: false, holdMs: 0));
            Assert.True(Wait.Until(() => Math.Abs(_fake.V() - 0.8f) < 0.02f), $"restored to {_fake.V()}, expected the original 0.8");
        }

        [Fact]
        public void Preview_stops_touching_a_target_the_engine_has_taken()
        {
            var engine = Engine();
            var preview = new VolumePreview();

            preview.SetLevel("Music", 0.2f);
            preview.Hold();
            engine.Tick(0, Levels.One("Music", over: true));
            Assert.True(Wait.Until(() => _fake.V() < 0.01f));

            // ползунок двигают дальше, пока звук в микрофоне: громкость цели не прыгает
            for (int i = 0; i < 20; i++)
            {
                preview.SetLevel("Music", 0.5f + i / 100f);
                Thread.Sleep(5);
            }
            Assert.True(_fake.V() < 0.01f, $"volume: {_fake.V()}");
            preview.Release();
        }

        [Fact]
        public void Engine_without_a_preview_behaves_as_before()
        {
            var engine = Engine();
            engine.Tick(0, Levels.One("Music", over: true));
            Assert.True(Wait.Until(() => _fake.V() < 0.01f));
            engine.Tick(10_000, Levels.One("Music", over: false, holdMs: 0));
            Assert.True(Wait.Until(() => Math.Abs(_fake.V() - 0.8f) < 0.02f));
        }

        [Fact]
        public void A_finished_preview_leaves_nothing_behind_for_the_engine()
        {
            var preview = new VolumePreview();
            preview.SetLevel("Music", 0.2f);
            preview.Release();
            Assert.Null(new PreviewRegistry().TakeOver("Music"));
        }

        // ---------- защита от аварийного выхода во время предпросмотра ----------

        [Fact]
        public void Crash_during_a_preview_is_repaired_on_the_next_start()
        {
            string path = Path.Combine(TestEnvironment.Folder, "preview_crash_" + Guid.NewGuid().ToString("N") + ".txt");
            var firstRun = new DuckingEngine(_fake, _ => { }, 0, new RecoveryStore(path), 600000, new PreviewRegistry());
            var oldNote = PreviewRegistry.RecoveryNote;
            var oldForget = PreviewRegistry.RecoveryForget;
            PreviewRegistry.RecoveryNote = firstRun.NoteRecovery;
            PreviewRegistry.RecoveryForget = firstRun.ForgetRecovery;
            try
            {
                var preview = new VolumePreview();
                preview.SetLevel("obs:Music", 0.05f);
                preview.Hold();
                Assert.InRange(_fake.V(), 0.04f, 0.06f);
                Assert.Contains("obs:Music", File.ReadAllText(path));   // исходная громкость записана сразу

                // «OBS упал»: Release не вызывается, громкость осталась сниженной (OBS мог сохранить её в сцены)
                var secondRun = new DuckingEngine(_fake, _ => { }, 0, new RecoveryStore(path), 600000, new PreviewRegistry());
                Assert.Equal(0.05f, _fake.V(), 2);
                secondRun.Tick(0, Levels.One("obs:Music", over: false));
                Assert.InRange(_fake.V(), 0.79f, 0.81f);
                Assert.False(File.Exists(path));
            }
            finally
            {
                PreviewRegistry.RecoveryNote = oldNote;
                PreviewRegistry.RecoveryForget = oldForget;
            }
        }

        [Fact]
        public void Finished_preview_erases_its_recovery_record()
        {
            var notes = new List<string>();
            var oldNote = PreviewRegistry.RecoveryNote;
            var oldForget = PreviewRegistry.RecoveryForget;
            PreviewRegistry.RecoveryNote = (k, v) => notes.Add($"note {k} {v.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}");
            PreviewRegistry.RecoveryForget = k => notes.Add($"forget {k}");
            try
            {
                var preview = new VolumePreview();
                preview.SetLevel("Music", 0.2f);
                preview.Release();
                Assert.Equal(new[] { "note Music 0.80", "forget Music" }, notes);
            }
            finally
            {
                PreviewRegistry.RecoveryNote = oldNote;
                PreviewRegistry.RecoveryForget = oldForget;
            }
        }

        [Fact]
        public void Preview_keeps_the_record_when_the_target_vanished_and_when_the_engine_took_over()
        {
            var notes = new List<string>();
            var oldNote = PreviewRegistry.RecoveryNote;
            var oldForget = PreviewRegistry.RecoveryForget;
            PreviewRegistry.RecoveryNote = (k, v) => notes.Add($"note {k}");
            PreviewRegistry.RecoveryForget = k => notes.Add($"forget {k}");
            try
            {
                // цель исчезла во время предпросмотра: вернуть громкость нельзя, запись остаётся
                var preview = new VolumePreview();
                preview.SetLevel("Music", 0.2f);
                _fake.Found = false;
                preview.Release();
                Assert.Equal(new[] { "note Music" }, notes);
                _fake.Found = true;

                // движок забрал цель: запись теперь его, предпросмотр её не стирает
                notes.Clear();
                var engine = Engine();
                var second = new VolumePreview();
                second.SetLevel("Other", 0.2f);
                engine.Tick(0, Levels.One("Other", over: true));
                Assert.True(Wait.Until(() => _fake.V() < 0.01f));
                second.Release();
                Assert.DoesNotContain("forget Other", notes);
            }
            finally
            {
                PreviewRegistry.RecoveryNote = oldNote;
                PreviewRegistry.RecoveryForget = oldForget;
            }
        }
    }
}
