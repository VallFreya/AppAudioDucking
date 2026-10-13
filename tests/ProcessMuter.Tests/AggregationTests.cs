using ProcessMuterByChannel;
using Xunit;

namespace ProcessMuter.Tests
{
    /// <summary>Как несколько фильтров сводятся в одно решение по каждой цели</summary>
    public class AggregationTests
    {
        private static FilterConfig F(string source = "Mic", string process = "Music", float threshold = -40, bool enabled = true,
            float duckPercent = 0, int fade = 600, int hold = 1500, bool obsTarget = false, string targetSource = "") =>
            new FilterConfig
            {
                source_name = source, process_name = process, threshold_db = threshold, enabled = enabled, duck_percent = duckPercent,
                fade_ms = fade, hold_ms = hold, target_type = obsTarget ? FilterConfig.TargetObsSource : FilterConfig.TargetProcess, target_source = targetSource,
            }.Clone();

        private static Dictionary<string, ProcessLevel> Build(params (FilterConfig, float, bool)[] inputs) => AudioMuterManager.BuildLevels(inputs);

        [Fact]
        public void Over_is_true_when_any_filter_of_the_target_exceeds_its_threshold()
        {
            var levels = Build((F(source: "A", threshold: -40), -50f, true), (F(source: "B", threshold: -30), -20f, true));
            Assert.True(levels["Music"].Over);
        }

        [Fact]
        public void Not_over_when_every_filter_is_below_threshold()
        {
            var levels = Build((F(threshold: -40), -41f, true), (F(threshold: -30), -96f, true));
            Assert.False(levels["Music"].Over);
        }

        [Fact]
        public void Level_equal_to_the_threshold_is_not_over()
        {
            Assert.False(Build((F(threshold: -40), -40f, true))["Music"].Over);
            Assert.True(Build((F(threshold: -40), -39.99f, true))["Music"].Over);
        }

        [Fact]
        public void Detached_source_never_counts_as_over()
        {
            Assert.False(Build((F(threshold: -60), 0f, false))["Music"].Over);
        }

        [Fact]
        public void Disabled_filters_are_ignored_entirely()
        {
            var levels = Build((F(enabled: false), 0f, true));
            Assert.Empty(levels);

            // и не влияют на соседей по цели
            var mixed = Build((F(enabled: false, duckPercent: 0, fade: 5000), 0f, true), (F(duckPercent: 50, fade: 100), -96f, true));
            Assert.Equal(0.5f, mixed["Music"].DuckLevel, 3);
            Assert.Equal(100, mixed["Music"].FadeMs);
            Assert.False(mixed["Music"].Over);
        }

        [Fact]
        public void Duck_level_is_the_lowest_and_times_are_the_longest_among_filters()
        {
            var levels = Build((F(duckPercent: 30, fade: 200, hold: 500), -96f, true), (F(duckPercent: 10, fade: 900, hold: 100), -96f, true), (F(duckPercent: 60, fade: 50, hold: 4000), -96f, true));
            Assert.Equal(0.1f, levels["Music"].DuckLevel, 3);
            Assert.Equal(900, levels["Music"].FadeMs);
            Assert.Equal(4000, levels["Music"].HoldMs);
        }

        [Fact]
        public void Source_in_the_log_is_the_filter_closest_to_or_above_its_threshold()
        {
            var levels = Build((F(source: "Far", threshold: -40), -90f, true), (F(source: "Near", threshold: -40), -45f, true), (F(source: "Mid", threshold: -40), -70f, true));
            Assert.Equal("Near", levels["Music"].Source);
        }

        [Fact]
        public void Different_targets_are_independent_and_case_insensitive()
        {
            var levels = Build((F(process: "Spotify"), 0f, true), (F(process: "SPOTIFY"), -96f, true), (F(process: "Discord"), -96f, true));
            Assert.Equal(2, levels.Count);
            Assert.True(levels["spotify"].Over);
            Assert.False(levels["Discord"].Over);
        }

        [Fact]
        public void Filters_without_a_target_are_skipped()
        {
            Assert.Empty(Build((F(process: ""), 0f, true), (F(obsTarget: true, targetSource: ""), 0f, true)));
        }

        [Fact]
        public void A_source_is_never_ducked_by_its_own_level()
        {
            // цель - тот же источник OBS, за которым следим (в окне этого не выбрать, но конфиг можно написать вручную)
            Assert.Empty(Build((F(source: "Mic", obsTarget: true, targetSource: "mic"), 0f, true)));
            Assert.Single(Build((F(source: "Mic", obsTarget: true, targetSource: "Music"), 0f, true)));
        }

        [Fact]
        public void Obs_and_process_targets_with_the_same_name_do_not_collide()
        {
            var levels = Build((F(process: "Music"), 0f, true), (F(obsTarget: true, targetSource: "Music"), -96f, true));
            Assert.Equal(2, levels.Count);
            Assert.True(levels["Music"].Over);
            Assert.False(levels["obs:Music"].Over);
        }

        [Fact]
        public void Custom_return_level_is_the_lowest_one_and_original_means_infinity()
        {
            var a = new FilterConfig { source_name = "Mic", process_name = "Music" }.Clone();   // до исходной
            var b = new FilterConfig { source_name = "Mic", process_name = "Music", restore_custom = true, restore_percent = 70 }.Clone();
            Assert.True(float.IsPositiveInfinity(Build((a, 0f, true))["Music"].RestoreLevel));
            Assert.Equal(0.7f, Build((a, 0f, true), (b, 0f, true))["Music"].RestoreLevel, 3);
        }

        [Fact]
        public void Nan_and_infinite_levels_from_the_meter_never_throw()
        {
            foreach (float db in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity, float.MaxValue })
            {
                var ex = Record.Exception(() => Build((F(), db, true), (F(source: "B"), db, true)));
                Assert.Null(ex);
            }
        }

        [Fact]
        public void Large_inputs_are_aggregated_quickly()
        {
            var inputs = Enumerable.Range(0, 20_000).Select(i => (F(source: "s" + i, process: "p" + (i % 500)), -(i % 90) - 0.5f, true)).ToArray();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var levels = AudioMuterManager.BuildLevels(inputs);
            Assert.Equal(500, levels.Count);
            Assert.True(sw.ElapsedMilliseconds < 1000, $"{sw.ElapsedMilliseconds} ms");
        }
    }
}
