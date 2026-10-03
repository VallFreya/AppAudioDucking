using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Timer = System.Threading.Timer;

namespace ProcessMuterByChannel
{
    public static class AudioMuterManager
    {
        private static readonly object SyncLock = new object();
        private static Timer? _checkTimer;

        // По умолчанию строки пустые
        public static string TargetProcessName { get; private set; } = string.Empty;
        public static string TargetAudioSource { get; private set; } = string.Empty;
        public static float ThresholdDb { get; private set; } = -60.0f;
        public static int FadeDurationMs { get; private set; } = 600;

        private static IntPtr _currentVolmeter = IntPtr.Zero;
        private static IntPtr _currentSource = IntPtr.Zero;

        private static readonly VolmeterCallback DelegateCallback = OnVolmeterUpdated;

        private static float _lastMaxDb = -96.0f;
        private static bool _isMuted = false;
        private static CancellationTokenSource? _fadeCts;

        // --- OBS Native Imports ---
        [DllImport("obs", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr obs_get_source_by_name([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

        [DllImport("obs", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr obs_get_output_source(uint channel);

        [DllImport("obs", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr obs_source_get_name(IntPtr source);

        [DllImport("obs", CallingConvention = CallingConvention.Cdecl)]
        private static extern void obs_source_release(IntPtr source);

        [DllImport("obs", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr obs_volmeter_create(int type); // 0 = OBS_FADER_LOG

        [DllImport("obs", CallingConvention = CallingConvention.Cdecl)]
        private static extern void obs_volmeter_destroy(IntPtr volmeter);

        [DllImport("obs", CallingConvention = CallingConvention.Cdecl)]
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
                ReattachVolmeter();

                if (_checkTimer == null)
                {
                    _checkTimer = new Timer(OnTimerTick, null, 500, 500);
                }
            }
        }

        public static void Stop()
        {
            lock (SyncLock)
            {
                _checkTimer?.Dispose();
                _checkTimer = null;

                DetachVolmeter();
            }
        }

        public static void UpdateSettings(string processName, string sourceName, float thresholdDb, int fadeMs)
        {
            lock (SyncLock)
            {
                string newProcessName = processName ?? string.Empty;
                string newSourceName = sourceName ?? string.Empty;

                bool sourceChanged = !string.Equals(TargetAudioSource, newSourceName, StringComparison.OrdinalIgnoreCase);

                TargetProcessName = newProcessName;
                TargetAudioSource = newSourceName;
                ThresholdDb = thresholdDb;
                FadeDurationMs = fadeMs;

                Plugin.LogInfo($"UpdateSettings -> Process: '{TargetProcessName}', Source: '{TargetAudioSource}', Threshold: {thresholdDb}dB, Fade: {fadeMs}ms");

                if (sourceChanged || (_currentVolmeter == IntPtr.Zero && !string.IsNullOrWhiteSpace(TargetAudioSource)))
                {
                    ReattachVolmeter();
                }
            }
        }

        private static IntPtr FindSourceByNameOrGlobal(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return IntPtr.Zero;

            IntPtr source = obs_get_source_by_name(name);
            if (source != IntPtr.Zero)
            {
                return source;
            }

            for (uint i = 1; i <= 6; i++)
            {
                IntPtr globalSource = obs_get_output_source(i);
                if (globalSource != IntPtr.Zero)
                {
                    IntPtr namePtr = obs_source_get_name(globalSource);
                    string? globalName = Marshal.PtrToStringUTF8(namePtr);

                    if (string.Equals(globalName, name, StringComparison.OrdinalIgnoreCase))
                    {
                        return globalSource;
                    }

                    obs_source_release(globalSource);
                }
            }

            return IntPtr.Zero;
        }

        private static void ReattachVolmeter()
        {
            DetachVolmeter();

            if (string.IsNullOrWhiteSpace(TargetAudioSource))
            {
                Plugin.LogInfo("TargetAudioSource is not set. Volmeter attach skipped.");
                return;
            }

            IntPtr source = FindSourceByNameOrGlobal(TargetAudioSource);
            if (source == IntPtr.Zero)
            {
                Plugin.LogWarn($"OBS source '{TargetAudioSource}' not found! Will retry searching on tick.");
                return;
            }

            try
            {
                IntPtr volmeter = obs_volmeter_create(0);
                if (volmeter == IntPtr.Zero)
                {
                    Plugin.LogError("Failed to create obs_volmeter!");
                    obs_source_release(source);
                    return;
                }

                if (obs_volmeter_attach_source(volmeter, source))
                {
                    obs_volmeter_add_callback(volmeter, DelegateCallback, IntPtr.Zero);
                    _currentVolmeter = volmeter;
                    _currentSource = source;
                    Plugin.LogInfo($"SUCCESS: Volmeter attached to OBS source: '{TargetAudioSource}'");
                }
                else
                {
                    Plugin.LogError($"Failed to attach volmeter to source: '{TargetAudioSource}'");
                    obs_volmeter_destroy(volmeter);
                    obs_source_release(source);
                }
            }
            catch (Exception ex)
            {
                Plugin.LogError($"Exception during ReattachVolmeter: {ex.Message}");
                obs_source_release(source);
            }
        }

        private static void DetachVolmeter()
        {
            _lastMaxDb = -96.0f;

            if (_currentVolmeter != IntPtr.Zero)
            {
                try
                {
                    obs_volmeter_remove_callback(_currentVolmeter, DelegateCallback, IntPtr.Zero);
                    obs_volmeter_detach_source(_currentVolmeter);
                    obs_volmeter_destroy(_currentVolmeter);
                }
                catch (Exception ex)
                {
                    Plugin.LogError($"Error destroying volmeter: {ex.Message}");
                }
                finally
                {
                    _currentVolmeter = IntPtr.Zero;
                }
            }

            if (_currentSource != IntPtr.Zero)
            {
                try
                {
                    obs_source_release(_currentSource);
                }
                catch (Exception ex)
                {
                    Plugin.LogError($"Error releasing source: {ex.Message}");
                }
                finally
                {
                    _currentSource = IntPtr.Zero;
                }
            }
        }

        private static void OnVolmeterUpdated(IntPtr param, IntPtr mag, IntPtr peak, IntPtr inputPeak)
        {
            IntPtr targetPtr = peak != IntPtr.Zero ? peak : mag;
            if (targetPtr == IntPtr.Zero || _currentVolmeter == IntPtr.Zero) return;

            int channels = obs_volmeter_get_nr_channels(_currentVolmeter);
            if (channels <= 0 || channels > 8) channels = 2;

            float[] rawValues = new float[channels];
            Marshal.Copy(targetPtr, rawValues, 0, channels);

            float maxVal = -96.0f;
            for (int i = 0; i < channels; i++)
            {
                float val = rawValues[i];

                float dbVal;
                if (val <= 0.0f && val >= -150.0f)
                {
                    dbVal = val;
                }
                else if (val > 0.00001f)
                {
                    dbVal = (float)(20.0 * Math.Log10(val));
                }
                else
                {
                    dbVal = -96.0f;
                }

                if (dbVal > maxVal)
                {
                    maxVal = dbVal;
                }
            }

            lock (SyncLock)
            {
                _lastMaxDb = maxVal;
            }
        }

        private static void OnTimerTick(object? state)
        {
            lock (SyncLock)
            {
                // Если имя источника не задано, просто находимся в режиме ожидания
                if (string.IsNullOrWhiteSpace(TargetAudioSource))
                {
                    return;
                }

                // Перепривязываем волумметр, если он отвалился или источник не был найден ранее
                if (_currentVolmeter == IntPtr.Zero)
                {
                    ReattachVolmeter();
                }

                float currentDb = _lastMaxDb;
                bool shouldMute = currentDb > ThresholdDb;

                Plugin.LogInfo($"[TICK] Source: '{TargetAudioSource}', Current Max dB: {currentDb:F1}dB (Threshold: {ThresholdDb:F1}dB), Muted: {_isMuted}, Target: '{TargetProcessName}'");

                // Если имя целевого процесса не задано, не выполняем глушение
                if (string.IsNullOrWhiteSpace(TargetProcessName))
                {
                    return;
                }

                if (shouldMute && !_isMuted)
                {
                    _isMuted = true;
                    _fadeCts?.Cancel();
                    _fadeCts = new CancellationTokenSource();
                    Task.Run(() => FadeProcessAudio(TargetProcessName, isMuting: true, FadeDurationMs, _fadeCts.Token));
                }
                else if (!shouldMute && _isMuted)
                {
                    _isMuted = false;
                    _fadeCts?.Cancel();
                    _fadeCts = new CancellationTokenSource();
                    Task.Run(() => FadeProcessAudio(TargetProcessName, isMuting: false, FadeDurationMs, _fadeCts.Token));
                }
            }
        }

        private static async Task FadeProcessAudio(string processName, bool isMuting, int durationMs, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(processName)) return;

            try
            {
                int steps = 20;
                int stepTime = Math.Max(10, durationMs / steps);

                float startVol = isMuting ? 1.0f : 0.0f;
                float endVol = isMuting ? 0.0f : 1.0f;

                for (int i = 0; i <= steps; i++)
                {
                    if (ct.IsCancellationRequested) return;

                    float progress = (float)i / steps;
                    float currentVol = startVol + (endVol - startVol) * progress;

                    ProcessAudioControl.SetProcessVolume(processName, currentVol);

                    await Task.Delay(stepTime, ct);
                }
            }
            catch (TaskCanceledException) { }
            catch (Exception ex)
            {
                Plugin.LogError($"Error fading audio for '{processName}': {ex.Message}");
            }
        }
    }
}