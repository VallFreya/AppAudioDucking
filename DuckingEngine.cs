using System.Threading;
using System.Threading.Tasks;

namespace ProcessMuterByChannel
{
    /// <summary>
    /// Управление громкостью цели: процесса Windows или звукового источника OBS.
    /// Выделено в интерфейс, чтобы логику можно было проверять без OBS.
    /// </summary>
    internal interface IVolumeBackend
    {
        /// <summary>Нужно ли периодически повторно применять громкость к приглушённой цели (новые аудиосессии процесса)</summary>
        bool SupportsEnforce(string target);

        /// <summary>
        /// Выставляет громкость цели как «исходная × factor», запоминая исходную в baseVolumes.
        /// Возвращает true, если цель найдена (у процесса есть аудиосессия, источник OBS существует)
        /// </summary>
        bool ApplyFactor(string target, float factor, Dictionary<string, float> baseVolumes);

        /// <summary>Возвращает исходную громкость и забывает её. true, если цель была найдена</summary>
        bool RestoreVolume(string target, Dictionary<string, float> baseVolumes);

        /// <summary>Поднимает громкость до baseVolume, если она осталась сниженной после аварийного выхода. true, если цель найдена</summary>
        bool Recover(string target, float baseVolume);
    }

    internal sealed class CoreAudioBackend : IVolumeBackend
    {
        public bool SupportsEnforce(string target) => true;

        public bool ApplyFactor(string target, float factor, Dictionary<string, float> baseVolumes)
            => ProcessAudioControl.ApplyFactor(target, factor, baseVolumes);

        public bool RestoreVolume(string target, Dictionary<string, float> baseVolumes)
            => ProcessAudioControl.RestoreVolume(target, baseVolumes);

        public bool Recover(string target, float baseVolume)
            => ProcessAudioControl.RecoverVolume(target, baseVolume);
    }

    /// <summary>Что известно о цели на текущей проверке: есть ли звук выше порога хотя бы в одном её фильтре</summary>
    internal struct ProcessLevel
    {
        public bool Over;
        public int FadeMs;
        public int HoldMs;
        // Фильтр, который ближе всего к порогу или выше него (для сообщений в журнале)
        public string Source;
        public float PeakDb;
        public float ThresholdDb;
    }

    /// <summary>
    /// Решает, когда приглушать и возвращать громкость целей, и делает это плавно.
    /// Все методы вызываются под одной внешней блокировкой.
    /// </summary>
    internal sealed class DuckingEngine
    {
        // Состояние цели. Одну цель могут глушить несколько фильтров
        private sealed class ProcessState
        {
            public bool IsMuted;
            public CancellationTokenSource? Cts;

            // Когда в последний раз был звук выше порога. Нужно для задержки возврата
            public long LastActiveMs;
            public long MutedSinceMs;
            public long LastEnforceMs;
            public long LastStatusLogMs;

            // Gate защищает Factor, BaseVolumes и RecoveryRecorded: ими пользуются таймер и задачи затухания
            public readonly object Gate = new object();
            // Во сколько раз сейчас снижена громкость относительно исходной (1 = без изменений)
            public float Factor = 1.0f;
            // Исходные громкости цели, запоминаются на время приглушения
            public readonly Dictionary<string, float> BaseVolumes = new Dictionary<string, float>();
            // Исходная громкость уже записана в файл восстановления
            public bool RecoveryRecorded;
        }

        private const int StatusLogEveryMs = 15000;
        private const int RecoverEveryMs = 2000;

        private readonly Dictionary<string, ProcessState> _processes = new Dictionary<string, ProcessState>(StringComparer.OrdinalIgnoreCase);
        private readonly IVolumeBackend _backend;
        private readonly Action<string> _log;
        private readonly int _enforceEveryMs;

        // Восстановление громкости после аварийного выхода: цель → исходная громкость
        private readonly IRecoveryStore? _store;
        private readonly object _recoveryLock = new object();
        private readonly Dictionary<string, float> _recovery = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _leftovers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly int _recoverWindowMs;
        private long _recoverStartMs = -1;
        private long _nextRecoverMs;

        /// <param name="enforceEveryMs">
        /// Как часто повторно применять громкость к уже приглушённому процессу. Нужно, потому что приложение может
        /// создать новую аудиосессию (смена трека, перезапуск воспроизведения) или само сбросить громкость.
        /// 0 отключает повторное применение (используется только в проверках).
        /// </param>
        /// <param name="store">Файл восстановления. Если в нём остались записи прошлого запуска, громкость этих целей будет возвращена</param>
        /// <param name="recoverWindowMs">Сколько ждать появления цели (источники OBS загружаются позже плагина), прежде чем отказаться</param>
        public DuckingEngine(IVolumeBackend backend, Action<string> log, int enforceEveryMs = 300,
            IRecoveryStore? store = null, int recoverWindowMs = 600000)
        {
            _backend = backend;
            _log = log;
            _enforceEveryMs = enforceEveryMs;
            _store = store;
            _recoverWindowMs = recoverWindowMs;

            if (store != null)
            {
                foreach (var (key, volume) in store.Load())
                {
                    _recovery[key] = volume;
                    _leftovers.Add(key);
                }

                if (_leftovers.Count > 0)
                {
                    _log($"Found {_leftovers.Count} target(s) left ducked after the previous session, restoring their volume");
                }
            }
        }

        public bool IsMuted(string target) => _processes.TryGetValue(target, out var state) && state.IsMuted;

        public float GetFactor(string target)
        {
            if (!_processes.TryGetValue(target, out var state)) return 1.0f;
            lock (state.Gate) return state.Factor;
        }

        /// <summary>Одна проверка: levels - по записи на каждую цель, упомянутую в фильтрах</summary>
        public void Tick(long now, IReadOnlyDictionary<string, ProcessLevel> levels)
        {
            RecoverLeftovers(now);

            foreach (var (process, level) in levels)
            {
                // пока громкость этой цели не возвращена после прошлого запуска, глушить её нельзя:
                // иначе в качестве исходной запомнилась бы уже сниженная громкость
                if (_leftovers.Contains(process)) continue;

                _processes.TryGetValue(process, out var state);
                bool isMuted = state?.IsMuted ?? false;

                if (level.Over && state != null) state.LastActiveMs = now;

                // Возврат звука откладывается: пока с последнего звука прошло меньше HoldMs, держим приглушение.
                // Так паузы в источнике (речь, TTS, оповещения) не вызывают возврат и повторное приглушение
                bool holding = isMuted && !level.Over && state != null && now - state.LastActiveMs < level.HoldMs;
                bool shouldMute = level.Over || holding;

                if (shouldMute != isMuted)
                {
                    state ??= _processes[process] = new ProcessState();
                    state.IsMuted = shouldMute;
                    if (shouldMute)
                    {
                        state.LastActiveMs = now;
                        state.MutedSinceMs = now;
                        state.LastStatusLogMs = now;
                        _log($"Mute '{process}': '{level.Source}' {level.PeakDb:F1}dB > {level.ThresholdDb:F1}dB");
                    }
                    else
                    {
                        _log($"Unmute '{process}': no sound above threshold for {now - state.LastActiveMs} ms (hold {level.HoldMs} ms)");
                    }

                    state.Cts?.Cancel();
                    var cts = state.Cts = new CancellationTokenSource();
                    var stateForFade = state;
                    float target = shouldMute ? 0.0f : 1.0f;
                    int durationMs = level.FadeMs;
                    Task.Run(() => FadeAsync(process, stateForFade, target, durationMs, cts.Token));
                }
                else if (state != null && isMuted)
                {
                    // Долгое приглушение: раз в несколько секунд пишем, что его держит, чтобы это было видно в журнале
                    if (now - state.LastStatusLogMs >= StatusLogEveryMs)
                    {
                        state.LastStatusLogMs = now;
                        string why = level.Over
                            ? $"'{level.Source}' {level.PeakDb:F1}dB > {level.ThresholdDb:F1}dB"
                            : $"waiting {Math.Max(0, level.HoldMs - (now - state.LastActiveMs))} ms of silence";
                        _log($"Still muted '{process}' for {(now - state.MutedSinceMs) / 1000} s: {why}");
                    }
                }
            }

            Enforce(now);
        }

        // Приглушённый процесс периодически получает громкость заново: новая аудиосессия приложения
        // (например, при смене трека) или сброс громкости самим приложением иначе остались бы громкими,
        // потому что громкость меняется только в момент перехода mute/unmute
        private void Enforce(long now)
        {
            if (_enforceEveryMs <= 0) return;

            foreach (var (target, state) in _processes)
            {
                if (now - state.LastEnforceMs < _enforceEveryMs) continue;
                if (!_backend.SupportsEnforce(target)) continue;

                lock (state.Gate)
                {
                    if (state.Factor >= 1.0f) continue;

                    state.LastEnforceMs = now;
                    _backend.ApplyFactor(target, state.Factor, state.BaseVolumes);
                }
            }
        }

        /// <summary>Возвращает исходную громкость целям, которых больше нет в фильтрах</summary>
        public void ReleaseUnused(ISet<string> used)
        {
            foreach (var name in new List<string>(_processes.Keys))
            {
                if (!used.Contains(name)) Release(name);
            }
        }

        public void ReleaseAll()
        {
            foreach (var name in new List<string>(_processes.Keys)) Release(name);
        }

        private void Release(string name)
        {
            if (!_processes.TryGetValue(name, out var state)) return;

            state.Cts?.Cancel();

            bool restored, recorded;
            lock (state.Gate)
            {
                // возвращаем исходную громкость, а не 100%
                restored = _backend.RestoreVolume(name, state.BaseVolumes);
                state.Factor = 1.0f;
                recorded = state.RecoveryRecorded;
                state.RecoveryRecorded = false;
            }

            // если цели уже нет (например, OBS при выходе удалил источники), запись остаётся до следующего запуска
            if (recorded && restored) ClearRecovery(name);
            _processes.Remove(name);
        }

        // --- восстановление после аварийного выхода ---

        private void SetRecovery(string key, float baseVolume)
        {
            lock (_recoveryLock)
            {
                _recovery[key] = baseVolume;
                _store?.Save(new Dictionary<string, float>(_recovery));
            }
        }

        private void ClearRecovery(string key)
        {
            lock (_recoveryLock)
            {
                if (_recovery.Remove(key)) _store?.Save(new Dictionary<string, float>(_recovery));
            }
        }

        private void RecoverLeftovers(long now)
        {
            if (_leftovers.Count == 0) return;

            if (_recoverStartMs < 0) _recoverStartMs = now;
            if (now < _nextRecoverMs) return;
            _nextRecoverMs = now + RecoverEveryMs;

            foreach (var key in new List<string>(_leftovers))
            {
                float volume;
                lock (_recoveryLock) volume = _recovery.TryGetValue(key, out var v) ? v : 1.0f;

                if (_backend.Recover(key, volume))
                {
                    _log($"Restored volume of '{key}' to {volume:F2} (it was left ducked by the previous session)");
                    _leftovers.Remove(key);
                    ClearRecovery(key);
                }
            }

            if (_leftovers.Count > 0 && now - _recoverStartMs > _recoverWindowMs)
            {
                foreach (var key in new List<string>(_leftovers))
                {
                    _log($"Gave up restoring volume of '{key}': target was not found");
                    _leftovers.Remove(key);
                    ClearRecovery(key);
                }
            }
        }

        // Плавно меняет громкость цели от текущего коэффициента до целевого.
        // Если затухание прервано новым, оно продолжается с того места, где остановилось
        private async Task FadeAsync(string target, ProcessState state, float targetFactor, int durationMs, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(target)) return;

            try
            {
                float startFactor;
                lock (state.Gate) startFactor = state.Factor;

                float distance = Math.Abs(targetFactor - startFactor);
                if (distance < 0.001f)
                {
                    // менять нечего, но при возврате к 1.0 запомненные громкости больше не нужны
                    if (targetFactor >= 1.0f) CompleteRestore(target, state, ct, applied: true);
                    return;
                }

                int steps = 20;
                int stepTime = Math.Max(10, (int)(durationMs * distance) / steps);
                bool lastApplied = false;

                for (int i = 1; i <= steps; i++)
                {
                    float factor = startFactor + (targetFactor - startFactor) * i / steps;
                    float? recordBase = null;

                    lock (state.Gate)
                    {
                        if (ct.IsCancellationRequested) return;
                        lastApplied = _backend.ApplyFactor(target, factor, state.BaseVolumes);
                        state.Factor = factor;

                        // как только исходная громкость запомнена, сохраняем её в файл восстановления
                        if (lastApplied && !state.RecoveryRecorded && state.BaseVolumes.Count > 0)
                        {
                            state.RecoveryRecorded = true;
                            recordBase = state.BaseVolumes.Values.Max();
                        }
                    }

                    if (recordBase.HasValue) SetRecovery(target, recordBase.Value);

                    await Task.Delay(stepTime, ct);
                }

                // громкость вернулась к исходной, запомненные значения сбрасываем,
                // чтобы при следующем приглушении взять актуальную громкость
                if (targetFactor >= 1.0f) CompleteRestore(target, state, ct, lastApplied);
            }
            catch (TaskCanceledException) { }
            catch (Exception ex)
            {
                _log($"Error fading audio for '{target}': {ex.Message}");
            }
        }

        private void CompleteRestore(string target, ProcessState state, CancellationToken ct, bool applied)
        {
            bool recorded;
            lock (state.Gate)
            {
                if (ct.IsCancellationRequested) return;

                state.BaseVolumes.Clear();
                recorded = state.RecoveryRecorded;
                state.RecoveryRecorded = false;
            }

            // запись снимаем только если громкость действительно вернули (цель была найдена)
            if (recorded && applied) ClearRecovery(target);
        }
    }
}
