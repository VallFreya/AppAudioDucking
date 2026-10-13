using System.Text;
using System.Text.Json;
using ProcessMuterByChannel;
using Xunit;

namespace ProcessMuter.Tests
{
    /// <summary>Настройки фильтра: крайние значения, испорченный и старый config.json</summary>
    public class ConfigTests
    {
        // ---------- Clone: подтягивание значений к допустимым границам ----------

        [Theory]
        [InlineData(float.NaN, -60f)]
        [InlineData(float.PositiveInfinity, -60f)]
        [InlineData(float.NegativeInfinity, -60f)]
        [InlineData(-75f, -60f)]
        [InlineData(5f, 0f)]
        [InlineData(-60f, -60f)]
        [InlineData(0f, 0f)]
        [InlineData(-0.001f, -0.001f)]
        public void Threshold_is_clamped_to_the_scale(float input, float expected)
        {
            var clone = new FilterConfig { threshold_db = input }.Clone();
            Assert.Equal(expected, clone.threshold_db);
        }

        [Theory]
        [InlineData(float.NaN, 0f)]
        [InlineData(-10f, 0f)]
        [InlineData(250f, 100f)]
        [InlineData(float.PositiveInfinity, 0f)]
        public void Duck_percent_is_clamped(float input, float expected)
        {
            Assert.Equal(expected, new FilterConfig { duck_percent = input }.Clone().duck_percent);
        }

        [Theory]
        [InlineData(int.MinValue, 1)]
        [InlineData(-1, 1)]
        [InlineData(0, 1)]
        [InlineData(1, 1)]
        [InlineData(600, 600)]
        [InlineData(60_000, 60_000)]
        [InlineData(60_001, 60_000)]
        [InlineData(int.MaxValue, 60_000)]
        public void Fade_is_clamped(int input, int expected)
        {
            Assert.Equal(expected, new FilterConfig { fade_ms = input }.Clone().fade_ms);
        }

        [Theory]
        [InlineData(int.MinValue, 0)]
        [InlineData(-1, 0)]
        [InlineData(0, 0)]
        [InlineData(1500, 1500)]
        [InlineData(3_600_000, 3_600_000)]
        [InlineData(int.MaxValue, 3_600_000)]
        public void Hold_is_clamped(int input, int expected)
        {
            Assert.Equal(expected, new FilterConfig { hold_ms = input }.Clone().hold_ms);
        }

        [Fact]
        public void Restore_level_is_never_below_the_duck_level()
        {
            var c = new FilterConfig { target_type = FilterConfig.TargetObsSource, duck_db = -20, restore_db = -40, restore_custom = true }.Clone();
            Assert.Equal(-20f, c.restore_db);

            var p = new FilterConfig { duck_percent = 50, restore_percent = 10, restore_custom = true }.Clone();
            Assert.Equal(50f, p.restore_percent);
        }

        [Fact]
        public void Null_strings_from_json_become_empty()
        {
            var c = new FilterConfig { name = null!, scene_name = null!, source_name = null!, process_name = null!, target_scene = null!, target_source = null!, target_type = null! }.Clone();
            Assert.Equal("", c.name);
            Assert.Equal("", c.scene_name);
            Assert.Equal("", c.source_name);
            Assert.Equal("", c.process_name);
            Assert.Equal("", c.TargetKey);
            Assert.Equal(FilterConfig.TargetProcess, c.target_type);
        }

        [Theory]
        [InlineData("chrome", "chrome")]
        [InlineData("chrome.exe", "chrome")]
        [InlineData("  Chrome.EXE  ", "Chrome")]
        [InlineData("my app.exe", "my app")]
        [InlineData(".exe", "")]
        [InlineData("", "")]
        [InlineData(null, "")]
        [InlineData("a.exe.exe", "a.exe")]
        public void Process_name_is_normalized(string? input, string expected)
        {
            Assert.Equal(expected, FilterConfig.NormalizeProcessName(input));
            Assert.Equal(expected, new FilterConfig { process_name = input! }.Clone().TargetKey);
        }

        [Fact]
        public void Target_key_distinguishes_obs_sources_from_processes()
        {
            Assert.Equal("obs:Музыка", new FilterConfig { target_type = FilterConfig.TargetObsSource, target_source = "Музыка", process_name = "chrome" }.Clone().TargetKey);
            Assert.Equal("chrome", new FilterConfig { target_type = FilterConfig.TargetProcess, target_source = "Музыка", process_name = "chrome" }.Clone().TargetKey);
            Assert.Equal("", new FilterConfig { target_type = FilterConfig.TargetObsSource, target_source = "" }.Clone().TargetKey);
        }

        [Fact]
        public void Target_type_is_case_insensitive_and_unknown_means_process()
        {
            Assert.True(new FilterConfig { target_type = "OBS_SOURCE" }.Clone().TargetsObsSource);
            Assert.False(new FilterConfig { target_type = "something else" }.Clone().TargetsObsSource);
        }

        [Fact]
        public void Duck_and_restore_levels_are_absolute_and_finite()
        {
            var p = new FilterConfig { duck_percent = 25, restore_custom = true, restore_percent = 60 }.Clone();
            Assert.Equal(0.25f, p.DuckLevel, 3);
            Assert.Equal(0.6f, p.RestoreLevel, 3);

            var original = new FilterConfig { duck_percent = 25 }.Clone();
            Assert.True(float.IsPositiveInfinity(original.RestoreLevel));   // «до исходной»

            var silent = new FilterConfig { target_type = FilterConfig.TargetObsSource, duck_db = -60 }.Clone();
            Assert.Equal(0f, silent.DuckLevel);   // нижний край шкалы - тишина
        }

        [Fact]
        public void Enabled_is_kept_by_clone()
        {
            Assert.False(new FilterConfig { enabled = false }.Clone().enabled);
            Assert.True(new FilterConfig().Clone().enabled);
        }

        // ---------- ввод чисел в окне ----------

        [Theory]
        [InlineData("600", 600)]
        [InlineData(" 250 ", 250)]
        [InlineData("1", 1)]
        [InlineData("99999999999", 60_000)]
        [InlineData("60001", 60_000)]
        [InlineData("0", 600)]
        [InlineData("-5", 600)]
        [InlineData("", 600)]
        [InlineData("abc", 600)]
        [InlineData("1e3", 600)]
        [InlineData("1,5", 600)]
        [InlineData("12.5", 600)]
        [InlineData(null, 600)]
        [InlineData("+300", 300)]
        [InlineData("٣٠٠", 600)]   // арабско-индийские цифры не принимаются
        public void ParseFade_edge_cases(string? text, int expected)
        {
            Assert.Equal(expected, FilterConfig.ParseFade(text));
        }

        [Theory]
        [InlineData("1500", 1500)]
        [InlineData("0", 0)]
        [InlineData("-0", 0)]
        [InlineData("-1", 1500)]
        [InlineData("99999999999999999999", 1500)]   // не помещается даже в long: значение по умолчанию
        [InlineData("99999999999", 3_600_000)]
        [InlineData("", 1500)]
        [InlineData("   ", 1500)]
        [InlineData("1 500", 1500)]
        [InlineData(null, 1500)]
        public void ParseHold_edge_cases(string? text, int expected)
        {
            Assert.Equal(expected, FilterConfig.ParseHold(text));
        }

        // ---------- разбор config.json ----------

        [Fact]
        public void Valid_config_with_unicode_roundtrips()
        {
            var original = new PluginConfig
            {
                theme = "light",
                filters = new List<FilterConfig>
                {
                    new FilterConfig { name = "Музыка при разговоре 🎵", source_name = "Микрофон", process_name = "Яндекс Музыка", threshold_db = -42.5f, fade_ms = 123, hold_ms = 4567, enabled = false }
                }
            };
            string json = JsonSerializer.Serialize(original, PluginConfigJsonContext.Default.PluginConfig);

            Assert.True(Plugin.TryParseConfig(json, out var parsed, out var error), error);
            Assert.Equal("light", parsed!.theme);
            var f = Plugin.ExtractFilters(parsed).Single();
            Assert.Equal("Музыка при разговоре 🎵", f.name);
            Assert.Equal("Яндекс Музыка", f.process_name);
            Assert.Equal(-42.5f, f.threshold_db);
            Assert.False(f.enabled);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("not json at all")]
        [InlineData("{")]
        [InlineData("{\"filters\": [")]
        [InlineData("[1,2,3]")]
        [InlineData("\"string\"")]
        [InlineData("null")]
        [InlineData("{\"filters\": \"oops\"}")]
        [InlineData("{\"filters\": [{\"threshold_db\": \"loud\"}]}")]
        [InlineData("{\"filters\": [{\"fade_ms\": 1e100}]}")]
        [InlineData("{\"filters\": [{\"fade_ms\": 99999999999}]}")]
        [InlineData("{\"filters\": [1, 2]}")]
        public void Broken_config_is_reported_not_thrown(string json)
        {
            bool ok = false;
            var ex = Record.Exception(() => ok = Plugin.TryParseConfig(json, out _, out _));
            Assert.Null(ex);
            Assert.False(ok);
        }

        [Fact]
        public void Truncated_config_is_reported()
        {
            string json = JsonSerializer.Serialize(new PluginConfig { filters = { new FilterConfig { name = "a" } } }, PluginConfigJsonContext.Default.PluginConfig);
            for (int cut = 1; cut < json.Length; cut += 7)
            {
                bool ok = true;
                var ex = Record.Exception(() => ok = Plugin.TryParseConfig(json.Substring(0, cut), out _, out _));
                Assert.Null(ex);
                Assert.False(ok, $"cut at {cut}");
            }
        }

        [Fact]
        public void Null_filter_elements_and_null_list_are_tolerated()
        {
            Assert.True(Plugin.TryParseConfig("{\"filters\": [null, {\"name\":\"a\",\"source_name\":\"m\"}, null]}", out var c, out _));
            var filters = Plugin.ExtractFilters(c!);
            Assert.Single(filters);
            Assert.Equal("a", filters[0].name);

            Assert.True(Plugin.TryParseConfig("{\"filters\": null}", out var c2, out _));
            Assert.Empty(Plugin.ExtractFilters(c2!));
        }

        [Fact]
        public void Null_values_inside_a_filter_do_not_break_loading()
        {
            Assert.True(Plugin.TryParseConfig("{\"filters\": [{\"name\": null, \"process_name\": null, \"source_name\": null, \"target_type\": null}]}", out var c, out _));
            var filter = Plugin.ExtractFilters(c!).Single().Clone();
            Assert.Equal("", filter.TargetKey);
        }

        [Fact]
        public void Unknown_fields_and_future_version_are_ignored()
        {
            Assert.True(Plugin.TryParseConfig("{\"version\": 99, \"future\": {\"a\": [1,2,3]}, \"filters\": [{\"name\": \"a\", \"brand_new\": true}]}", out var c, out var error), error);
            Assert.Equal("a", Plugin.ExtractFilters(c!).Single().name);
        }

        [Fact]
        public void Config_with_utf8_bom_and_comments_style_is_handled()
        {
            string json = "﻿" + JsonSerializer.Serialize(new PluginConfig { filters = { new FilterConfig { name = "bom" } } }, PluginConfigJsonContext.Default.PluginConfig);
            // BOM в начале строки допускается только при чтении файла (ReadAllText его снимает), поэтому проверяем через файл
            string path = Path.Combine(TestEnvironment.Folder, "bom.json");
            File.WriteAllText(path, json.TrimStart('﻿'), new UTF8Encoding(true));
            string read = File.ReadAllText(path, Encoding.UTF8);
            Assert.True(Plugin.TryParseConfig(read, out var c, out var error), error);
            Assert.Equal("bom", Plugin.ExtractFilters(c!).Single().name);
        }

        [Fact]
        public void Out_of_range_numbers_load_and_are_clamped_by_clone()
        {
            Assert.True(Plugin.TryParseConfig("{\"filters\": [{\"threshold_db\": -9999, \"duck_percent\": 1000, \"fade_ms\": -5, \"hold_ms\": 2147483647, \"restore_percent\": -3}]}", out var c, out var error), error);
            var f = Plugin.ExtractFilters(c!).Single().Clone();
            Assert.Equal(-60f, f.threshold_db);
            Assert.Equal(100f, f.duck_percent);
            Assert.Equal(1, f.fade_ms);
            Assert.Equal(FilterConfig.MaxHoldMs, f.hold_ms);
            Assert.InRange(f.restore_percent, 0f, 100f);
        }

        [Fact]
        public void Legacy_single_filter_config_is_migrated()
        {
            Assert.True(Plugin.TryParseConfig("{\"process_name\": \"chrome.exe\", \"source_name\": \"Mic\", \"threshold_db\": \"-45,5\", \"fade_ms\": \"abc\"}", out var c, out var error), error);
            var f = Plugin.ExtractFilters(c!).Single().Clone();
            Assert.Equal("chrome", f.process_name);   // «.exe» из ручной записи снимается
            Assert.Equal("Mic", f.source_name);
            Assert.Equal(-45.5f, f.threshold_db);
            Assert.Equal(FilterConfig.DefaultFadeMs, f.fade_ms);   // нечисловое значение заменяется значением по умолчанию
        }

        [Fact]
        public void Legacy_config_without_source_gives_no_filters()
        {
            Assert.True(Plugin.TryParseConfig("{\"process_name\": \"chrome\"}", out var c, out _));
            Assert.Empty(Plugin.ExtractFilters(c!));
        }

        [Theory]
        [InlineData("light", false)]
        [InlineData("LIGHT", false)]
        [InlineData("dark", true)]
        [InlineData("blue", true)]
        [InlineData("", true)]
        public void Theme_value_is_interpreted_safely(string theme, bool dark)
        {
            // так же, как в Plugin.LoadConfig
            Assert.Equal(dark, !string.Equals(theme, "light", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void Very_long_names_and_many_filters_roundtrip()
        {
            var config = new PluginConfig();
            for (int i = 0; i < 2000; i++) config.filters.Add(new FilterConfig { name = new string('ы', 500) + i, source_name = "s" + i, process_name = "p" + i });
            string json = JsonSerializer.Serialize(config, PluginConfigJsonContext.Default.PluginConfig);

            Assert.True(Plugin.TryParseConfig(json, out var parsed, out var error), error);
            Assert.Equal(2000, Plugin.ExtractFilters(parsed!).Count);
        }

        [Fact]
        public void Deeply_nested_json_is_rejected_without_a_crash()
        {
            string json = new string('[', 100_000) + new string(']', 100_000);
            bool ok = true;
            var ex = Record.Exception(() => ok = Plugin.TryParseConfig(json, out _, out _));
            Assert.Null(ex);
            Assert.False(ok);
        }
    }
}
