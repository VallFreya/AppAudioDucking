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
        public const float MinDb = -60.0f;

        // Верхние границы времени. Значения из ручной правки конфига (отрицательные, int.MaxValue) подтягиваются к ним:
        // иначе гигантская задержка навсегда оставила бы цель приглушённой, а гигантское затухание переполнило бы расчёт шага
        public const int MaxFadeMs = 60_000;
        public const int MaxHoldMs = 3_600_000;

        public const string TargetProcess = "process";
        public const string TargetObsSource = "obs_source";

        // Название фильтра, которое пользователь видит в окне настроек. Пустое значит «по умолчанию»
        public string name { get; set; } = "";

        // Выключенный фильтр остаётся в списке и в конфиге, но ничего не глушит. В старых конфигах поля нет, значит включён
        public bool enabled { get; set; } = true;

        // Что отслеживаем. Пустая сцена означает глобальные источники (микрофон, desktop audio)
        public string scene_name { get; set; } = "";
        public string source_name { get; set; } = "";

        // Что глушим: "process" (процесс Windows) или "obs_source" (звуковой источник OBS).
        // Оба набора полей хранятся всегда, чтобы при переключении вида цели введённое не терялось
        public string target_type { get; set; } = TargetProcess;
        public string process_name { get; set; } = "";
        public string target_scene { get; set; } = "";
        public string target_source { get; set; } = "";

        // Порог срабатывания в дБ, от MinDb до 0. Значения вне шкалы (ручной ввод в старых версиях) подтягиваются к её краям
        public float threshold_db { get; set; } = DefaultThresholdDb;

        // До какого уровня глушить: абсолютная громкость цели, а не доля от исходной.
        // Для источника OBS в дБ (от MinDb до 0, нижний край - полная тишина), для процесса Windows в процентах (0..100)
        public float duck_db { get; set; } = MinDb;
        public float duck_percent { get; set; } = 0.0f;

        // До какого уровня возвращать громкость после приглушения (если restore_custom). Это явный абсолютный уровень, а не доля от исходной:
        // его можно поставить и выше исходной громкости. Всегда не ниже уровня приглушения
        public float restore_db { get; set; } = 0.0f;
        // false: возвращать до исходной громкости (как раньше), значения restore_* игнорируются; true: возвращать до заданного уровня
        public bool restore_custom { get; set; } = false;
        public float restore_percent { get; set; } = 100.0f;
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
            enabled = enabled,
            scene_name = scene_name ?? "",
            source_name = source_name ?? "",
            target_type = TargetsObsSource ? TargetObsSource : TargetProcess,
            process_name = NormalizeProcessName(process_name),
            target_scene = target_scene ?? "",
            target_source = target_source ?? "",
            threshold_db = ClampDb(threshold_db),
            duck_db = ClampDb(duck_db),
            duck_percent = float.IsFinite(duck_percent) ? Math.Clamp(duck_percent, 0.0f, 100.0f) : 0.0f,
            restore_custom = restore_custom,
            restore_db = float.IsFinite(restore_db) ? Math.Clamp(restore_db, ClampDb(duck_db), 0.0f) : 0.0f,
            restore_percent = float.IsFinite(restore_percent) ? Math.Clamp(restore_percent, Math.Clamp(duck_percent, 0.0f, 100.0f), 100.0f) : 100.0f,
            fade_ms = Math.Clamp(fade_ms, 1, MaxFadeMs),
            hold_ms = Math.Clamp(hold_ms, 0, MaxHoldMs)
        };

        /// <summary>Имя процесса без пробелов по краям и без «.exe» (в окне оно вводится так же, а в конфиге могли написать вручную)</summary>
        public static string NormalizeProcessName(string? name)
        {
            name = (name ?? "").Trim();
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 4).Trim();
            return name;
        }

        public static float ClampDb(float db) => float.IsFinite(db) ? Math.Clamp(db, MinDb, 0.0f) : DefaultThresholdDb;

        /// <summary>Громкость источника OBS (множитель) для значения шкалы в дБ. Нижний край шкалы - полная тишина</summary>
        public static float DbToVolume(float db) => db <= MinDb ? 0.0f : MathF.Pow(10.0f, db / 20.0f);

        /// <summary>Уровень приглушения как абсолютная громкость цели: 0..1 (у источника OBS множитель)</summary>
        [JsonIgnore]
        public float DuckLevel => TargetsObsSource ? DbToVolume(ClampDb(duck_db)) : Math.Clamp(duck_percent, 0.0f, 100.0f) / 100.0f;

        /// <summary>До какой абсолютной громкости цель возвращается после приглушения; бесконечность - до исходной</summary>
        [JsonIgnore]
        public float RestoreLevel => !restore_custom
            ? float.PositiveInfinity
            : TargetsObsSource
                ? DbToVolume(Math.Max(ClampDb(duck_db), Math.Min(0.0f, restore_db)))
                : Math.Max(Math.Clamp(duck_percent, 0.0f, 100.0f), Math.Clamp(restore_percent, 0.0f, 100.0f)) / 100.0f;

        public static float ParseThreshold(string? text)
        {
            if (float.TryParse((text ?? "").Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out float value)
                && float.IsFinite(value))
            {
                return value;
            }
            return DefaultThresholdDb;
        }

        // Очень большое число (вставили лишние цифры) подтягивается к верхней границе, а не заменяется значением по умолчанию.
        // Пустой текст, буквы и отрицательные числа дают значение по умолчанию
        public static int ParseHold(string? text)
        {
            return long.TryParse((text ?? "").Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long value) && value >= 0
                ? (int)Math.Min(value, MaxHoldMs)
                : DefaultHoldMs;
        }

        public static int ParseFade(string? text)
        {
            return long.TryParse((text ?? "").Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long value) && value > 0
                ? (int)Math.Min(value, MaxFadeMs)
                : DefaultFadeMs;
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
