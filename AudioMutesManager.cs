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
            store: new RecoveryStore(Plugin.GetDataFilePath("ducked_volumes.txt")));

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
                _checkTimer?.Dispose();
                _checkTimer = null;

                DetachAll();

                // не оставляем процессы заглушенными после выгрузки плагина
                Engine.ReleaseAll();
            }
        }

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
                    var copy = config.Clone();
                    Filters.Add(new FilterRuntime(copy));
                    if (copy.TargetKey.Length > 0) usedProcesses.Add(copy.TargetKey);
                }

                // процессы, которых больше нет в фильтрах, возвращаем к полной громкости
                Engine.ReleaseUnused(usedProcesses);

                Plugin.LogInfo($"Filters updated: {Filters.Count} filter(s)");

                foreach (var filter in Filters) TryAttach(filter);
            }
        }

        private static void TryAttach(FilterRuntime filter)
        {
            string sourceName = filter.Config.source_name;
            if (string.IsNullOrWhiteSpace(sourceName) || filter.Volmeter != IntPtr.Zero) return;

            IntPtr source = obs_get_source_by_name(sourceName);
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

        private static void OnTimerTick(object? state)
        {
            lock (SyncLock)
            {
                if (!_running) return;

                long now = Environment.TickCount64;

                // Перепривязываем volmeter, если источник появился позже или отвалился (не на каждом тике)
                if (_tickCounter++ % AttachRetryEveryTicks == 0)
                {
                    foreach (var filter in Filters) TryAttach(filter);
                }

                // Для каждого процесса: есть ли звук выше порога хотя бы в одном его фильтре
                var levels = new Dictionary<string, ProcessLevel>(StringComparer.OrdinalIgnoreCase);
                foreach (var filter in Filters)
                {
                    // максимум берём всегда, чтобы накопленное значение не копилось у неактивных фильтров
                    float db = filter.TakePeak();

                    string process = filter.Config.TargetKey;
                    if (process.Length == 0) continue;

                    // источник, за которым сами следим, глушить нельзя: его громкость влияла бы на измеряемый уровень
                    if (filter.Config.TargetsObsSource
                        && string.Equals(filter.Config.target_source, filter.Config.source_name, StringComparison.OrdinalIgnoreCase)) continue;

                    float threshold = filter.Config.threshold_db;
                    bool over = filter.Volmeter != IntPtr.Zero && db > threshold;

                    if (!levels.TryGetValue(process, out var level))
                    {
                        level = new ProcessLevel { Source = filter.Config.source_name, PeakDb = db, ThresholdDb = threshold };
                    }
                    else if (db - threshold > level.PeakDb - level.ThresholdDb)
                    {
                        // в журнале показываем фильтр, который ближе всего к порогу или выше него
                        level.Source = filter.Config.source_name;
                        level.PeakDb = db;
                        level.ThresholdDb = threshold;
                    }

                    level.Over |= over;
                    level.FadeMs = Math.Max(level.FadeMs, filter.Config.fade_ms);
                    level.HoldMs = Math.Max(level.HoldMs, filter.Config.hold_ms);
                    levels[process] = level;
                }

                Engine.Tick(now, levels);
            }
        }
    }
}
