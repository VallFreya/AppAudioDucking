using System.Diagnostics;
using System.Runtime.CompilerServices;
using ProcessMuterByChannel;

namespace ProcessMuter.Tests
{
    /// <summary>Общая подготовка: конфиг и файлы плагина идут во временную папку, а не в рабочий каталог OBS или проекта</summary>
    internal static class TestEnvironment
    {
        public static readonly string Folder = Path.Combine(Path.GetTempPath(), "ProcessMuterTests_" + Guid.NewGuid().ToString("N"));
        public static string ConfigPath => Path.Combine(Folder, "config.json");

        [ModuleInitializer]
        internal static void Init()
        {
            Directory.CreateDirectory(Folder);
            // до первого обращения к AudioMuterManager: его движок берёт путь к файлу восстановления при создании
            Plugin.ConfigPathOverride = ConfigPath;
            Plugin.LogEnabled = false;
        }
    }

    internal static class Wait
    {
        /// <summary>Ждёт, пока условие станет истинным (затухание идёт в фоновых задачах, в реальном времени)</summary>
        public static bool Until(Func<bool> condition, int timeoutMs = 5000)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (condition()) return true;
                Thread.Sleep(10);
            }
            return condition();
        }
    }

    /// <summary>Запуск кода в STA-потоке: окна WinForms и их контролы требуют его</summary>
    internal static class Sta
    {
        public static void Run(Action action)
        {
            Exception? error = null;
            var thread = new Thread(() =>
            {
                try { action(); }
                catch (Exception ex) { error = ex; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            if (!thread.Join(TimeSpan.FromSeconds(120))) throw new TimeoutException("STA test timed out");
            if (error != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
        }
    }

    /// <summary>Модель цели: несколько «сессий» процесса или одна громкость источника OBS. Можно заставить бросать исключения</summary>
    internal sealed class FakeBackend : IVolumeBackend
    {
        private readonly object _lock = new object();
        public readonly Dictionary<string, float> Volumes = new Dictionary<string, float>();
        public bool Enforce = true;
        public bool Found = true;
        public bool RecoverOk = true;
        public int ApplyCalls;

        // цели, на которых соответствующий вызов бросает исключение
        public readonly HashSet<string> ThrowOnApply = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> ThrowOnRestore = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> ThrowOnRecover = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // у нескольких целей свои громкости: ключ «цель/сессия»
        public readonly Dictionary<string, Dictionary<string, float>> PerTarget = new Dictionary<string, Dictionary<string, float>>(StringComparer.OrdinalIgnoreCase);

        public bool SupportsEnforce(string target) => Enforce;

        private Dictionary<string, float> VolumesOf(string target)
        {
            if (PerTarget.Count == 0) return Volumes;
            if (!PerTarget.TryGetValue(target, out var map)) PerTarget[target] = map = new Dictionary<string, float> { ["s"] = 1.0f };
            return map;
        }

        public bool ApplyFactor(string target, float factor, float duckLevel, float restoreLevel, Dictionary<string, float> bases)
        {
            lock (_lock)
            {
                if (ThrowOnApply.Contains(target)) throw new InvalidOperationException("device lost");
                if (!Found) return false;
                ApplyCalls++;
                var volumes = VolumesOf(target);
                foreach (var id in volumes.Keys.ToList())
                {
                    if (!bases.TryGetValue(id, out float b)) { b = volumes[id]; bases[id] = b; }
                    float ducked = Math.Min(b, duckLevel);
                    float restored = float.IsPositiveInfinity(restoreLevel) ? b : Math.Max(ducked, restoreLevel);
                    volumes[id] = Math.Max(0f, ducked + (restored - ducked) * factor);
                }
                return true;
            }
        }

        public bool RestoreVolume(string target, float restoreLevel, Dictionary<string, float> bases)
        {
            if (ThrowOnRestore.Contains(target)) throw new InvalidOperationException("device lost");
            if (bases.Count == 0) return true;
            bool ok = ApplyFactor(target, 1f, 0f, restoreLevel, bases);
            bases.Clear();
            return ok;
        }

        public bool Recover(string target, float baseVolume)
        {
            lock (_lock)
            {
                if (ThrowOnRecover.Contains(target)) throw new InvalidOperationException("device lost");
                if (!RecoverOk) return false;
                var volumes = VolumesOf(target);
                foreach (var id in volumes.Keys.ToList()) if (volumes[id] < baseVolume) volumes[id] = baseVolume;
                return true;
            }
        }

        public float V(string id = "volume") { lock (_lock) return Volumes[id]; }
        public float V(string target, string id) { lock (_lock) return VolumesOf(target)[id]; }
    }

    internal static class Levels
    {
        public static IReadOnlyDictionary<string, ProcessLevel> One(string target, bool over, int holdMs = 1500, int fadeMs = 100,
            float duck = 0f, float restore = float.PositiveInfinity) =>
            new Dictionary<string, ProcessLevel>
            {
                [target] = new ProcessLevel
                {
                    Over = over, FadeMs = fadeMs, HoldMs = holdMs, Source = "mic",
                    PeakDb = over ? -30f : -96f, ThresholdDb = -60f, DuckLevel = duck, RestoreLevel = restore
                }
            };
    }
}
