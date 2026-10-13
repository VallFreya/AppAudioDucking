using ProcessMuterByChannel;
using Xunit;

namespace ProcessMuter.Tests
{
    /// <summary>Предпросмотр громкости, измерители уровней и работа с аудиосессиями на отсутствующих целях и странных значениях</summary>
    [Collection("Plugin state")]
    public class ProbeAndPreviewTests
    {
        private static readonly string[] StrangeTargets =
        {
            "definitely_not_a_running_process_xyz", "obs:Нет такого источника", "obs:", "", "  ", new string('я', 5000), "a\0b", "con", "aux:", "Spotify.exe", "..\\..\\x",
        };

        // ---------- VolumePreview ----------

        [Fact]
        public void Preview_on_missing_targets_does_nothing_and_cleans_up()
        {
            var preview = new VolumePreview();
            foreach (var target in StrangeTargets)
            {
                Assert.Null(Record.Exception(() => preview.SetLevel(target, 0.5f)));
                Assert.Null(Record.Exception(() => preview.SetLevel(target, 0.5f, isReturn: true)));
                preview.Hold();
                preview.Tick();
                preview.Release();
                preview.Cancel();
                Assert.False(preview.IsActive);
            }
        }

        [Theory]
        [InlineData(float.NaN)]
        [InlineData(float.PositiveInfinity)]
        [InlineData(float.NegativeInfinity)]
        [InlineData(-1f)]
        [InlineData(1e30f)]
        public void Preview_with_nonfinite_levels_does_not_throw(float level)
        {
            var preview = new VolumePreview();
            Assert.Null(Record.Exception(() =>
            {
                preview.SetLevel("definitely_not_a_running_process_xyz", level);
                preview.SetLevel("definitely_not_a_running_process_xyz", level, isReturn: true);
                preview.Tick();
                preview.Cancel();
            }));
        }

        [Fact]
        public void Preview_calls_in_any_order_are_safe()
        {
            var preview = new VolumePreview();
            preview.Release();
            preview.Hold();
            preview.Tick();
            preview.Cancel();
            preview.Cancel();
            VolumePreview.RestoreAll();
            VolumePreview.RestoreAll();

            for (int i = 0; i < 2000; i++)
            {
                preview.SetLevel("definitely_not_a_running_process_xyz", (i % 100) / 100f, isReturn: i % 2 == 0);
                if (i % 7 == 0) preview.Hold();
                if (i % 11 == 0) preview.Release();
                if (i % 13 == 0) preview.Tick();
            }
            preview.Cancel();
            Assert.False(preview.IsActive);
        }

        [Fact]
        public void Preview_from_many_threads_is_safe()
        {
            var preview = new VolumePreview();
            var errors = new System.Collections.Concurrent.ConcurrentBag<Exception>();
            var threads = Enumerable.Range(0, 8).Select(n => new Thread(() =>
            {
                try
                {
                    for (int i = 0; i < 500; i++)
                    {
                        preview.SetLevel("definitely_not_a_running_process_xyz" + (n % 2), i / 500f);
                        preview.Tick();
                        if (i % 5 == 0) preview.Release();
                        if (i % 17 == 0) VolumePreview.RestoreAll();
                    }
                }
                catch (Exception ex) { errors.Add(ex); }
            })).ToList();
            threads.ForEach(t => t.Start());
            threads.ForEach(t => t.Join());
            preview.Cancel();
            Assert.Empty(errors);
        }

        // ---------- ProcessAudioControl ----------

        [Fact]
        public void Audio_sessions_of_missing_processes_report_not_found()
        {
            foreach (var name in StrangeTargets)
            {
                var bases = new Dictionary<string, float>();
                Assert.False(ProcessAudioControl.ApplyFactor(name, 0.5f, 0f, float.PositiveInfinity, bases));
                Assert.Empty(bases);
                Assert.False(ProcessAudioControl.RecoverVolume(name, 1f));
                Assert.True(ProcessAudioControl.RestoreVolume(name, float.PositiveInfinity, bases));   // нечего возвращать
            }
        }

        [Theory]
        [InlineData(float.NaN, float.NaN, float.NaN)]
        [InlineData(-1f, 5f, float.PositiveInfinity)]
        [InlineData(float.PositiveInfinity, float.NegativeInfinity, 0f)]
        public void Audio_control_with_strange_numbers_does_not_throw(float factor, float duck, float restore)
        {
            Assert.Null(Record.Exception(() =>
                ProcessAudioControl.ApplyFactor("definitely_not_a_running_process_xyz", factor, duck, restore, new Dictionary<string, float>())));
        }

        [Fact]
        public void Running_process_list_is_sorted_unique_and_without_extensions()
        {
            var names = ProcessAudioControl.GetRunningProcessNames();
            Assert.NotEmpty(names);
            Assert.Equal(names.Length, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.DoesNotContain(names, n => n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void Process_name_match_is_cached_and_tolerates_dead_pids()
        {
            var cache = new Dictionary<uint, bool>();
            Assert.False(ProcessAudioControl.IsProcessNamed(uint.MaxValue, "x", cache));
            Assert.False(ProcessAudioControl.IsProcessNamed(0, "x", cache));
            Assert.True(cache.ContainsKey(uint.MaxValue));

            uint self = (uint)Environment.ProcessId;
            string selfName = System.Diagnostics.Process.GetCurrentProcess().ProcessName;
            Assert.True(ProcessAudioControl.IsProcessNamed(self, selfName.ToUpperInvariant(), new Dictionary<uint, bool>()));   // регистр не важен
        }

        // ---------- ProcessLevelProbe ----------

        [Fact]
        public void ProcessLevelProbe_handles_garbage_names_and_double_dispose()
        {
            var probe = new ProcessLevelProbe();
            foreach (var name in StrangeTargets.Concat(new string?[] { null }))
            {
                probe.ProcessName = name!;
                Assert.Null(Record.Exception(() => probe.ReadPeak()));
                Assert.Null(Record.Exception(() => probe.ReadVolume()));
                Assert.False(probe.HasSessions);
            }

            probe.Dispose();
            probe.Dispose();
            Assert.Null(Record.Exception(() => probe.ReadPeak()));   // чтение после Dispose заново ищет сессии, а не падает
            probe.Dispose();
        }

        [Fact]
        public void ProcessLevelProbe_peak_is_always_in_range()
        {
            var probe = new ProcessLevelProbe { ProcessName = System.Diagnostics.Process.GetCurrentProcess().ProcessName + ".exe" };
            for (int i = 0; i < 50; i++) Assert.InRange(probe.ReadPeak(), 0f, 1f);
            probe.Dispose();
        }

        // ---------- ObsLevelProbe / ProbeHub без OBS ----------

        [Fact]
        public void ObsLevelProbe_without_obs_is_silent_not_attached_and_disposes_twice()
        {
            var probe = new ObsLevelProbe("Микрофон");
            Assert.False(probe.Attached);
            Assert.False(probe.IsActive);
            Assert.Equal(-96f, probe.VolumeDb);
            Assert.Equal(-96f, probe.TakePeakDb());
            probe.Dispose();
            probe.Dispose();
            Assert.Equal(-96f, probe.TakePeakDb());
        }

        [Fact]
        public void ObsLevelProbe_DisposeAll_closes_every_open_probe()
        {
            var probes = Enumerable.Range(0, 10).Select(i => new ObsLevelProbe("s" + i)).ToList();
            ObsLevelProbe.DisposeAll();
            Assert.All(probes, p => Assert.Equal(-96f, p.TakePeakDb()));
            ObsLevelProbe.DisposeAll();
        }

        [Fact]
        public void ProbeHub_counts_references_and_ignores_unbalanced_release()
        {
            using var hub = new ProbeHub();
            var a1 = hub.Acquire("A");
            var a2 = hub.Acquire("A");
            Assert.Same(a1, a2);

            hub.Release(null);
            hub.Release(new ObsLevelProbe("never acquired"));   // чужой измеритель: тихо игнорируется

            hub.Release(a1);
            Assert.Equal(-96f, a2.TakePeakDb());   // ссылка ещё одна, измеритель жив
            hub.Release(a2);
            hub.Release(a2);                        // лишний Release не бросает
            Assert.NotSame(a1, hub.Acquire("A"));  // после полного освобождения создаётся новый
        }

        [Fact]
        public void ProbeHub_survives_many_names_and_dispose_in_the_middle()
        {
            var hub = new ProbeHub();
            for (int i = 0; i < 500; i++) hub.Acquire("src" + (i % 50));
            hub.Dispose();
            hub.Dispose();
        }
    }
}
