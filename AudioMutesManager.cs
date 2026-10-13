using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Timer = System.Threading.Timer;

namespace ProcessMuterByChannel
{
    public static class AudioMuterManager
    {
        private const float SilenceDb = -96.0f;
        private const int MaxChannels = 8; // MAX_AUDIO_CHANNELS в libobs
        private const int TickMs = 100;
        private const int AttachRetryEveryTicks = 5; // повторная привязка отсутствующих источников раз в 500 мс

        // Состояние одного фильтра: его volmeter и самый громкий уровень с прошлой проверки
        private sealed class FilterRuntime
        {
            public FilterRuntime(FilterConfig config) { Config = config; }

            public FilterConfig Config { get; }
            public IntPtr Volmeter = IntPtr.Zero;
            public IntPtr Source = IntPtr.Zero;
            public GCHandle Handle;
            public bool AttachProblemLogged;

            private readonly object _peakLock = new object();
            private float _peakDb = SilenceDb;

            // Колбэк OBS запоминает максимум, а не последнее значение: короткий звук между проверками не должен теряться
            public void ReportLevel(float db)
            {
                lock (_peakLock)
                {
                    if (db > _peakDb) _peakDb = db;
                }
            }

            // Возвращает максимум с прошлого вызова и начинает накопление заново
            public float TakePeak()
            {
                lock (_peakLock)
                {
                    float peak = _peakDb;
                    _peakDb = SilenceDb;
                    return peak;
                }
            }
        }

        private static readonly object SyncLock = new object();
        private static Timer? _checkTimer;
        private static bool _running;

        private static readonly List<FilterRuntime> Filters = new List<FilterRuntime>();
        // Решает, когда и как менять громкость процессов (см. DuckingEngine)
        // Цели: процессы Windows и звуковые источники OBS. Исходные громкости пишутся в файл восстановления
        private static readonly DuckingEngine Engine = new DuckingEngine(
            new RoutingBackend(), Plugin.LogInfo,
            store: new RecoveryStore(Plugin.GetDataFilePath("ducked_volumes.txt")),
            external: new PreviewRegistry());

        private static readonly VolmeterCallback DelegateCallback = OnVolmeterUpdated;

        // --- OBS Native Imports ---
        [DllImport("obs", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr obs_get_source_by_name([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

        [DllImport("obs", CallingConvention = CallingConvention.Cdecl)]
        private static extern void obs_source_release(IntPtr source);

        [DllImport("obs", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr obs_volmeter_create(int type); // 0 = OBS_FADER_LOG

        [DllImport("obs", CallingConvention = CallingConvention.Cdecl)]
        private static extern void obs_volmeter_destroy(IntPtr volmeter);

        [DllImport("obs", CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.U1)]
        private static extern bool obs_volmeter_attach_source(IntPtr volmeter, IntPtr source);

        [DllImport("obs", CallingConvention = CallingConvention.Cdecl)]
        private static extern void obs_volmeter_detach_source(IntPtr volmeter);

        [DllImport("obs", CallingConvention = CallingConvention.Cdecl)]
        private static extern void obs_volmeter_add_callback(IntPtr volmeter, VolmeterCallback callback, IntPtr param);

        [DllImport("obs", CallingConvention = CallingConvention.Cdecl)]
        private static extern void obs_volmeter_remove_callback(IntPtr volmeter, VolmeterCallback callback, IntPtr param);

        [DllImport("obs", CallingConvention = CallingConvention.Cdecl)]
        private static extern int obs_volmeter_get_nr_channels(IntPtr volmeter);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void VolmeterCallback(IntPtr param, IntPtr mag, IntPtr peak, IntPtr inputPeak);

        public static void Start()
        {
            lock (SyncLock)
            {
                _running = true;
                _checkTimer ??= new Timer(OnTimerTick, null, TickMs, TickMs);
            }
        }

        public static void Stop()
        {
            lock (SyncLock)
            {
                _running = false;
                VolumePreview.RestoreAll();   // предпросмотр уровня в окне настроек, если оно открыто в момент выгрузки
                ObsLevelProbe.DisposeAll();   // измерители окна настроек, если оно открыто в момент выгрузки
                _checkTimer?.Dispose();
                _checkTimer = null;

                DetachAll();

                // не оставляем процессы заглушенными после выгрузки плагина
                Engine.ReleaseAll();
            }
        }

        /// <summary>
        /// Цель сейчас приглушена (или возвращается) движком. Тогда original - громкость до приглушения: окно настроек
        /// не должно принимать приглушённую громкость за исходную и не должно само менять громкость этой цели
        /// </summary>
        public static bool TryGetDuckedOriginal(string targetKey, out float original)
        {
            lock (SyncLock) return Engine.TryGetDuckedOriginal(targetKey, out original);
        }

        // Запись в файл восстановления из окна настроек. Берёт только блокировку самого файла восстановления, но не SyncLock:
        // окно вызывает это из-под блокировки предпросмотра, а Stop держит SyncLock и ждёт эту блокировку (иначе взаимное ожидание)
        internal static void NoteRecovery(string key, float baseVolume) => Engine.NoteRecovery(key, baseVolume);
        internal static void ForgetRecovery(string key) => Engine.ForgetRecovery(key);

        /// <summary>Копия текущих фильтров для окна настроек</summary>
        public static List<FilterConfig> GetFilters()
        {
            lock (SyncLock)
            {
                var result = new List<FilterConfig>(Filters.Count);
                foreach (var filter in Filters) result.Add(filter.Config.Clone());
                return result;
            }
        }

        /// <summary>Заменяет набор фильтров целиком</summary>
        public static void SetFilters(IEnumerable<FilterConfig> filters)
        {
            lock (SyncLock)
            {
                DetachAll();
                Filters.Clear();

                var usedProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var config in filters)
                {
                    if (config == null) continue;   // "null" в списке фильтров конфига, написанного вручную

                    var copy = config.Clone();
                    Filters.Add(new FilterRuntime(copy));
                    // выключенный фильтр хранится (окно настроек его показывает), но цель не занимает: она вернётся к исходной громкости
                    if (copy.enabled && copy.TargetKey.Length > 0) usedProcesses.Add(copy.TargetKey);
                }

                // процессы, которых больше нет в фильтрах, возвращаем к полной громкости
                Engine.ReleaseUnused(usedProcesses);

                Plugin.LogInfo($"Filters updated: {Filters.Count} filter(s)");

                // Привязка идёт только пока плагин работает. До Start() (конфиг читается раньше запуска таймера) её сделает
                // первая проверка, а после Stop() (окно настроек ещё открыто, OBS выгружает плагин) создавать volmeter нельзя
                if (_running)
                {
                    foreach (var filter in Filters) TryAttach(filter);
                }
            }
        }

        private static void TryAttach(FilterRuntime filter)
        {
            string sourceName = filter.Config.source_name;
            if (!filter.Config.enabled || string.IsNullOrWhiteSpace(sourceName) || filter.Volmeter != IntPtr.Zero) return;

            IntPtr source;
            try
            {
                source = obs_get_source_by_name(sourceName);
            }
            catch (Exception ex)
            {
                // например, нет obs.dll (проверки вне OBS): привязка не получилась, но остальные фильтры работают
                LogAttachProblemOnce(filter, $"Cannot look up OBS source '{sourceName}': {ex.Message}", isError: true);
                return;
            }

            if (source == IntPtr.Zero)
            {
                LogAttachProblemOnce(filter, $"OBS source '{sourceName}' not found! Will retry searching on tick.", isError: false);
                return;
            }

            IntPtr volmeter = IntPtr.Zero;
            try
            {
                volmeter = obs_volmeter_create(0);
                if (volmeter == IntPtr.Zero)
                {
                    LogAttachProblemOnce(filter, "Failed to create obs_volmeter!", isError: true);
                    obs_source_release(source);
                    return;
                }

                if (!obs_volmeter_attach_source(volmeter, source))
                {
                    LogAttachProblemOnce(filter, $"Failed to attach volmeter to source: '{sourceName}'", isError: true);
                    obs_volmeter_destroy(volmeter);
                    obs_source_release(source);
                    return;
                }

                filter.Handle = GCHandle.Alloc(filter);
                filter.Volmeter = volmeter;
                filter.Source = source;
                filter.AttachProblemLogged = false;
                obs_volmeter_add_callback(volmeter, DelegateCallback, GCHandle.ToIntPtr(filter.Handle));

                Plugin.LogInfo($"SUCCESS: Volmeter attached to OBS source: '{sourceName}'");
            }
            catch (Exception ex)
            {
                Plugin.LogError($"Exception during attach of '{sourceName}': {ex.Message}");
                DetachFilter(filter);
                if (filter.Volmeter == IntPtr.Zero)
                {
                    if (volmeter != IntPtr.Zero) obs_volmeter_destroy(volmeter);
                    obs_source_release(source);
                }
            }
        }

        // Повторы одной и той же ошибки на каждом тике засоряют лог, поэтому пишем её один раз
        private static void LogAttachProblemOnce(FilterRuntime filter, string message, bool isError)
        {
            if (filter.AttachProblemLogged) return;
            filter.AttachProblemLogged = true;

            if (isError) Plugin.LogError(message);
            else Plugin.LogWarn(message);
        }

        private static void DetachAll()
        {
            foreach (var filter in Filters) DetachFilter(filter);
        }

        private static void DetachFilter(FilterRuntime filter)
        {
            filter.TakePeak();

            if (filter.Volmeter != IntPtr.Zero)
            {
                try
                {
                    // после remove_callback колбэк гарантированно не работает, handle можно освобождать
                    obs_volmeter_remove_callback(filter.Volmeter, DelegateCallback, GCHandle.ToIntPtr(filter.Handle));
                    obs_volmeter_detach_source(filter.Volmeter);
                    obs_volmeter_destroy(filter.Volmeter);
                }
                catch (Exception ex)
                {
                    Plugin.LogError($"Error destroying volmeter: {ex.Message}");
                }
                finally
                {
                    filter.Volmeter = IntPtr.Zero;
                }
            }

            if (filter.Handle.IsAllocated) filter.Handle.Free();

            if (filter.Source != IntPtr.Zero)
            {
                try
                {
                    obs_source_release(filter.Source);
                }
                catch (Exception ex)
                {
                    Plugin.LogError($"Error releasing source: {ex.Message}");
                }
                finally
                {
                    filter.Source = IntPtr.Zero;
                }
            }
        }


        // Вызывается из аудиопотока OBS. Здесь нельзя брать SyncLock: таймер держит его,
        // пока ждёт завершения этого колбэка в obs_volmeter_remove_callback
        private static unsafe void OnVolmeterUpdated(IntPtr param, IntPtr mag, IntPtr peak, IntPtr inputPeak)
        {
            IntPtr levels = peak != IntPtr.Zero ? peak : mag;
            if (levels == IntPtr.Zero || param == IntPtr.Zero) return;

            if (GCHandle.FromIntPtr(param).Target is not FilterRuntime filter) return;

            IntPtr volmeter = filter.Volmeter;
            if (volmeter == IntPtr.Zero) return;

            int channels = obs_volmeter_get_nr_channels(volmeter);
            if (channels <= 0 || channels > MaxChannels) channels = 2;

            // OBS отдаёт уровни в dB, тишина приходит как -inf
            float* values = (float*)levels;
            float maxDb = SilenceDb;
            for (int i = 0; i < channels; i++)
            {
                float db = float.IsFinite(values[i]) ? values[i] : SilenceDb;
                if (db > maxDb) maxDb = db;
            }

            filter.ReportLevel(maxDb);
        }

        private static int _tickCounter;

        private static long _lastTickErrorMs;

        private static void OnTimerTick(object? state)
        {
            // Если прошлая проверка ещё идёт (медленный ответ звуковой подсистемы) или окно настроек меняет фильтры,
            // эту проверку пропускаем: иначе вызовы таймера копились бы в очереди и занимали бы потоки пула.
            // Исключение здесь убило бы весь процесс OBS, поэтому наружу оно не выходит
            if (!Monitor.TryEnter(SyncLock)) return;
            try
            {
                TickLocked();
            }
            catch (Exception ex)
            {
                long now = Environment.TickCount64;
                if (now - _lastTickErrorMs > 10000)
                {
                    _lastTickErrorMs = now;
                    Plugin.LogError($"Timer tick failed: {ex}");
                }
            }
            finally
            {
                Monitor.Exit(SyncLock);
            }
        }

        /// <summary>Вызывается в начале каждой проверки. Нужен только проверкам, чтобы вызвать сбой внутри таймера</summary>
        internal static Action? TickHook { get; set; }

        private static void TickLocked()
        {
            if (!_running) return;
            TickHook?.Invoke();

            long now = Environment.TickCount64;

            // Перепривязываем volmeter, если источник появился позже или отвалился (не на каждом тике)
            if (_tickCounter++ % AttachRetryEveryTicks == 0)
            {
                foreach (var filter in Filters) TryAttach(filter);
            }

            // максимум берём у всех фильтров, в том числе выключенных, чтобы накопленное значение не копилось
            var inputs = new List<(FilterConfig Config, float PeakDb, bool Attached)>(Filters.Count);
            foreach (var filter in Filters) inputs.Add((filter.Config, filter.TakePeak(), filter.Volmeter != IntPtr.Zero));

            Engine.Tick(now, BuildLevels(inputs));
        }

        /// <summary>
        /// Решение по целям из состояния фильтров: для каждой цели - есть ли звук выше порога хотя бы в одном её фильтре,
        /// самые низкие уровни приглушения и возврата, самые большие время затухания и задержка. Выключенные фильтры
        /// и фильтры, цель которых совпадает с отслеживаемым источником, пропускаются.
        /// </summary>
        internal static Dictionary<string, ProcessLevel> BuildLevels(IEnumerable<(FilterConfig Config, float PeakDb, bool Attached)> inputs)
        {
            var levels = new Dictionary<string, ProcessLevel>(StringComparer.OrdinalIgnoreCase);
            foreach (var (config, db, attached) in inputs)
            {
                if (!config.enabled) continue;

                string process = config.TargetKey;
                if (process.Length == 0) continue;

                // источник, за которым сами следим, глушить нельзя: его громкость влияла бы на измеряемый уровень
                if (config.TargetsObsSource
                    && string.Equals(config.target_source, config.source_name, StringComparison.OrdinalIgnoreCase)) continue;

                float threshold = config.threshold_db;
                bool over = attached && db > threshold;

                if (!levels.TryGetValue(process, out var level))
                {
                    level = new ProcessLevel { Source = config.source_name, PeakDb = db, ThresholdDb = threshold, DuckLevel = config.DuckLevel, RestoreLevel = config.RestoreLevel };
                }
                else if (db - threshold > level.PeakDb - level.ThresholdDb)
                {
                    // в журнале показываем фильтр, который ближе всего к порогу или выше него
                    level.Source = config.source_name;
                    level.PeakDb = db;
                    level.ThresholdDb = threshold;
                }

                level.Over |= over;
                // если одну цель глушат несколько фильтров, берётся самый низкий уровень
                level.DuckLevel = Math.Min(level.DuckLevel, config.DuckLevel);
                level.RestoreLevel = Math.Min(level.RestoreLevel, config.RestoreLevel);
                level.FadeMs = Math.Max(level.FadeMs, config.fade_ms);
                level.HoldMs = Math.Max(level.HoldMs, config.hold_ms);
                levels[process] = level;
            }

            return levels;
        }
    }
}
