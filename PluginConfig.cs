using System.Globalization;
using System.Text.Json.Serialization;

namespace ProcessMuterByChannel
{
    // Один фильтр: следим за звуковым источником OBS и глушим процесс Windows или другой звуковой источник OBS
    public class FilterConfig
    {
        public const float DefaultThresholdDb = -60.0f;
        public const int DefaultFadeMs = 600;
        public const int DefaultHoldMs = 1500;

        public const string TargetProcess = "process";
        public const string TargetObsSource = "obs_source";

        // Название фильтра, которое пользователь видит в окне настроек. Пустое значит «по умолчанию»
        public string name { get; set; } = "";

        // Что отслеживаем. Пустая сцена означает глобальные источники (микрофон, desktop audio)
        public string scene_name { get; set; } = "";
        public string source_name { get; set; } = "";

        // Что глушим: "process" (процесс Windows) или "obs_source" (звуковой источник OBS).
        // Оба набора полей хранятся всегда, чтобы при переключении вида цели введённое не терялось
        public string target_type { get; set; } = TargetProcess;
        public string process_name { get; set; } = "";
        public string target_scene { get; set; } = "";
        public string target_source { get; set; } = "";

        public float threshold_db { get; set; } = DefaultThresholdDb;
        public int fade_ms { get; set; } = DefaultFadeMs;
        // Сколько мс после тишины в источнике держать приглушение, чтобы не возвращать звук в паузах
        public int hold_ms { get; set; } = DefaultHoldMs;

        [JsonIgnore]
        public bool TargetsObsSource => string.Equals(target_type, TargetObsSource, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Ключ цели для DuckingEngine: имя процесса или "obs:" + имя источника OBS.
        /// В именах файлов двоеточие запрещено, поэтому с именем процесса ключ не пересечётся. Пустой ключ - цель не задана
        /// </summary>
        [JsonIgnore]
        public string TargetKey => TargetsObsSource
            ? (target_source.Length > 0 ? ObsSourceBackend.Prefix + target_source : "")
            : process_name;

        public FilterConfig Clone() => new FilterConfig
        {
            name = (name ?? "").Trim(),
            scene_name = scene_name ?? "",
            source_name = source_name ?? "",
            target_type = TargetsObsSource ? TargetObsSource : TargetProcess,
            process_name = (process_name ?? "").Trim(),
            target_scene = target_scene ?? "",
            target_source = target_source ?? "",
            threshold_db = threshold_db,
            fade_ms = fade_ms,
            hold_ms = hold_ms
        };

        public static float ParseThreshold(string? text)
        {
            if (float.TryParse((text ?? "").Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out float value)
                && float.IsFinite(value))
            {
                return value;
            }
            return DefaultThresholdDb;
        }

        public static int ParseHold(string? text)
        {
            return int.TryParse((text ?? "").Trim(), out int value) && value >= 0 ? value : DefaultHoldMs;
        }

        public static int ParseFade(string? text)
        {
            return int.TryParse((text ?? "").Trim(), out int value) && value > 0 ? value : DefaultFadeMs;
        }
    }

    // Файл конфига. Поля старого формата (до появления фильтров) нужны только для миграции
    public class PluginConfig
    {
        public int version { get; set; } = 2;
        // Тема окна настроек: "dark" (по умолчанию) или "light"
        public string theme { get; set; } = "dark";
        public List<FilterConfig> filters { get; set; } = new List<FilterConfig>();

        public string? process_name { get; set; }
        public string? source_name { get; set; }
        public string? threshold_db { get; set; }
        public string? fade_ms { get; set; }

        public FilterConfig? MigrateLegacy()
        {
            if (string.IsNullOrWhiteSpace(source_name)) return null;

            return new FilterConfig
            {
                scene_name = "",
                source_name = source_name.Trim(),
                process_name = (process_name ?? "").Trim(),
                threshold_db = FilterConfig.ParseThreshold(threshold_db),
                fade_ms = FilterConfig.ParseFade(fade_ms)
            };
        }
    }

    // Source Generator для NativeAOT (убирает требование рефлексии)
    [JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonSerializable(typeof(PluginConfig))]
    internal partial class PluginConfigJsonContext : JsonSerializerContext;
}
