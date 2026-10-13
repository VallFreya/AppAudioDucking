using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace ProcessMuterByChannel
{
    /// <summary>
    /// Живой уровень звукового источника OBS для окна настроек. Подключает собственный volmeter к источнику,
    /// выбранному в окне (в том числе ещё не сохранённому в фильтре), и показывает, активен ли источник и как выставлен его фейдер.
    /// Колбэк volmeter вызывается из аудиопотока OBS, поэтому он только запоминает пик под отдельной блокировкой.
    /// </summary>
    internal sealed class ObsLevelProbe : IDisposable
    {
        private const float SilenceDb = -96.0f;
        private const int MaxChannels = 8; // MAX_AUDIO_CHANNELS в libobs
        private const int RetryAttachMs = 1000;

        [DllImport("obs", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr obs_get_source_by_name([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

        [DllImport("obs", CallingConvention = CallingConvention.Cdecl)]
        private static extern void obs_source_release(IntPtr source);

        [DllImport("obs", CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.U1)]
        private static extern bool obs_source_active(IntPtr source);

        [DllImport("obs", CallingConvention = CallingConvention.Cdecl)]
        private static extern float obs_source_get_volume(IntPtr source);

        [DllImport("obs", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr obs_volmeter_create(int type);

        [DllImport("obs", CallingConvention = CallingConvention.Cdecl)]
        private static extern void obs_volmeter_destroy(IntPtr volmeter);

        [DllImport("obs", CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.U1)]
        private static extern bool obs_volmeter_attach_source(IntPtr volmeter, IntPtr source);

        [DllImport("obs", CallingConvention = CallingConvention.Cdecl)]
        private static extern void obs_volmeter_detach_source(IntPtr volmeter);

        [DllImport("obs", CallingConvention = CallingConvention.Cdecl)]
        private static extern void obs_volmeter_add_callback(IntPtr volmeter, ProbeCallback callback, IntPtr param);

        [DllImport("obs", CallingConvention = CallingConvention.Cdecl)]
        private static extern void obs_volmeter_remove_callback(IntPtr volmeter, ProbeCallback callback, IntPtr param);

        [DllImport("obs", CallingConvention = CallingConvention.Cdecl)]
        private static extern int obs_volmeter_get_nr_channels(IntPtr volmeter);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void ProbeCallback(IntPtr param, IntPtr mag, IntPtr peak, IntPtr inputPeak);

        private static readonly ProbeCallback Callback = OnLevels;

        // Все открытые измерители: при выгрузке плагина их нужно отключить от источников
        private static readonly List<ObsLevelProbe> Open = new List<ObsLevelProbe>();

        private readonly object _stateLock = new object();   // нативные объекты и флаг Disposed
        private readonly object _peakLock = new object();    // пик из колбэка OBS
        private IntPtr _volmeter = IntPtr.Zero;
        private IntPtr _source = IntPtr.Zero;
        private GCHandle _handle;
        private float _peakDb = SilenceDb;
        private long _nextAttachMs;
        private bool _disposed;

        public string SourceName { get; }

        public ObsLevelProbe(string sourceName)
        {
            SourceName = sourceName;
            lock (Open) Open.Add(this);
            lock (_stateLock) TryAttach(Environment.TickCount64);
        }

        /// <summary>Источник найден и к нему подключён измеритель</summary>
        public bool Attached
        {
            get { lock (_stateLock) return _volmeter != IntPtr.Zero; }
        }

        /// <summary>Источник сейчас участвует в выводе (находится в активной сцене). Если источник не найден - false</summary>
        public bool IsActive
        {
            get
            {
                lock (_stateLock)
                {
                    if (_source == IntPtr.Zero) return false;
                    try { return obs_source_active(_source); }
                    catch { return false; }
                }
            }
        }

        /// <summary>Громкость источника в OBS (фейдер) в дБ; тишина или не найден - -96</summary>
        public float VolumeDb
        {
            get
            {
                lock (_stateLock)
                {
                    if (_source == IntPtr.Zero) return SilenceDb;
                    try
                    {
                        float volume = obs_source_get_volume(_source);
                        return volume <= 0.0f ? SilenceDb : 20.0f * MathF.Log10(volume);
                    }
                    catch { return SilenceDb; }
                }
            }
        }

        /// <summary>Самый громкий уровень с прошлого вызова, дБ. Заодно повторяет подключение, если источника ещё не было</summary>
        public float TakePeakDb()
        {
            lock (_stateLock)
            {
                if (_disposed) return SilenceDb;
                if (_volmeter == IntPtr.Zero) TryAttach(Environment.TickCount64);
            }

            lock (_peakLock)
            {
                float peak = _peakDb;
                _peakDb = SilenceDb;
                return peak;
            }
        }

        private void TryAttach(long now)
        {
            if (_disposed || _volmeter != IntPtr.Zero || now < _nextAttachMs) return;
            _nextAttachMs = now + RetryAttachMs;

            IntPtr source = IntPtr.Zero;
            IntPtr volmeter = IntPtr.Zero;
            try
            {
                source = obs_get_source_by_name(SourceName);
                if (source == IntPtr.Zero) return;

                volmeter = obs_volmeter_create(0);
                if (volmeter == IntPtr.Zero || !obs_volmeter_attach_source(volmeter, source))
                {
                    if (volmeter != IntPtr.Zero) obs_volmeter_destroy(volmeter);
                    obs_source_release(source);
                    return;
                }

                _handle = GCHandle.Alloc(this);
                _volmeter = volmeter;
                _source = source;
                obs_volmeter_add_callback(volmeter, Callback, GCHandle.ToIntPtr(_handle));
            }
            catch
            {
                // нет obs.dll (проверка вне OBS) или источник пропал во время подключения
                if (volmeter != IntPtr.Zero && _volmeter == IntPtr.Zero)
                {
                    try { obs_volmeter_destroy(volmeter); } catch { }
                }
                if (source != IntPtr.Zero && _source == IntPtr.Zero)
                {
                    try { obs_source_release(source); } catch { }
                }
            }
        }

        // Из аудиопотока OBS: блокировку состояния брать нельзя (Dispose держит её, ожидая этот колбэк)
        private static unsafe void OnLevels(IntPtr param, IntPtr mag, IntPtr peak, IntPtr inputPeak)
        {
            IntPtr levels = peak != IntPtr.Zero ? peak : mag;
            if (levels == IntPtr.Zero || param == IntPtr.Zero) return;
            if (GCHandle.FromIntPtr(param).Target is not ObsLevelProbe probe) return;

            IntPtr volmeter = probe._volmeter;
            if (volmeter == IntPtr.Zero) return;

            int channels = obs_volmeter_get_nr_channels(volmeter);
            if (channels <= 0 || channels > MaxChannels) channels = 2;

            // OBS отдаёт уровни в дБ, тишина приходит как -inf
            float* values = (float*)levels;
            float maxDb = SilenceDb;
            for (int i = 0; i < channels; i++)
            {
                float db = float.IsFinite(values[i]) ? values[i] : SilenceDb;
                if (db > maxDb) maxDb = db;
            }

            lock (probe._peakLock)
            {
                if (maxDb > probe._peakDb) probe._peakDb = maxDb;
            }
        }

        public void Dispose()
        {
            lock (Open) Open.Remove(this);

            lock (_stateLock)
            {
                if (_disposed) return;
                _disposed = true;

                try
                {
                    if (_volmeter != IntPtr.Zero)
                    {
                        // после remove_callback колбэк гарантированно не работает, handle можно освобождать
                        obs_volmeter_remove_callback(_volmeter, Callback, GCHandle.ToIntPtr(_handle));
                        obs_volmeter_detach_source(_volmeter);
                        obs_volmeter_destroy(_volmeter);
                    }
                }
                catch { }
                finally
                {
                    _volmeter = IntPtr.Zero;
                    if (_handle.IsAllocated) _handle.Free();
                }

                try
                {
                    if (_source != IntPtr.Zero) obs_source_release(_source);
                }
                catch { }
                finally
                {
                    _source = IntPtr.Zero;
                }
            }
        }

        /// <summary>Отключает все измерители (выгрузка плагина при открытом окне настроек)</summary>
        public static void DisposeAll()
        {
            ObsLevelProbe[] probes;
            lock (Open) probes = Open.ToArray();
            foreach (var probe in probes) probe.Dispose();
        }
    }

    /// <summary>
    /// Живой уровень процесса Windows: пик его аудиосессий в микшере. Список сессий находится один раз и запоминается,
    /// потому что перечисление всех сессий устройства стоит заметно дороже, чем чтение пика, и повторяется раз в секунду.
    /// Используется только из потока окна настроек.
    /// </summary>
    internal sealed class ProcessLevelProbe : IDisposable
    {
        private const int RefreshMs = 1000;

        private MMDeviceEnumerator? _enumerator;
        private MMDevice? _device;
        private readonly List<AudioSessionControl> _sessions = new List<AudioSessionControl>();
        private long _nextRefreshMs;
        private string _processName = "";

        public string ProcessName
        {
            get => _processName;
            set
            {
                value = (value ?? "").Trim();
                if (value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) value = value.Substring(0, value.Length - 4).Trim();
                if (string.Equals(value, _processName, StringComparison.OrdinalIgnoreCase)) return;

                _processName = value;
                ClearSessions();
                _nextRefreshMs = 0;
            }
        }

        /// <summary>Нашлась ли хотя бы одна аудиосессия процесса (после последнего чтения пика)</summary>
        public bool HasSessions => _sessions.Count > 0;

        /// <summary>Самая большая громкость (ползунок в микшере Windows) среди сессий процесса, 0..1. Нет сессий - -1</summary>
        public float ReadVolume()
        {
            float volume = -1.0f;
            foreach (var session in _sessions)
            {
                try { volume = Math.Max(volume, session.SimpleAudioVolume.Volume); }
                catch { _nextRefreshMs = 0; }
            }
            return volume;
        }

        /// <summary>Самый громкий пик среди сессий процесса, 0..1. Если процесса нет или он молчит - 0</summary>
        public float ReadPeak()
        {
            if (_processName.Length == 0) return 0.0f;

            long now = Environment.TickCount64;
            if (now >= _nextRefreshMs) Refresh(now);

            float peak = 0.0f;
            foreach (var session in _sessions)
            {
                try
                {
                    peak = Math.Max(peak, session.AudioMeterInformation.MasterPeakValue);
                }
                catch
                {
                    // сессия закрылась, список обновится при следующем обходе
                    _nextRefreshMs = 0;
                }
            }

            return Math.Clamp(peak, 0.0f, 1.0f);
        }

        private void Refresh(long now)
        {
            _nextRefreshMs = now + RefreshMs;
            ClearSessions();

            try
            {
                _enumerator = new MMDeviceEnumerator();
                _device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                var sessions = _device.AudioSessionManager.Sessions;
                var matchByPid = new Dictionary<uint, bool>();

                for (int i = 0; i < sessions.Count; i++)
                {
                    try
                    {
                        var session = sessions[i];
                        uint pid = session.GetProcessID;
                        if (pid != 0 && ProcessAudioControl.IsProcessNamed(pid, _processName, matchByPid)) _sessions.Add(session);
                    }
                    catch
                    {
                        // сессия исчезла во время обхода
                    }
                }
            }
            catch
            {
                // нет аудиоустройства
            }
        }

        private void ClearSessions()
        {
            foreach (var session in _sessions)
            {
                try { session.Dispose(); } catch { }
            }
            _sessions.Clear();

            try { _device?.Dispose(); } catch { }
            try { _enumerator?.Dispose(); } catch { }
            _device = null;
            _enumerator = null;
        }

        public void Dispose() => ClearSessions();
    }

    /// <summary>
    /// Общие измерители уровней источников OBS для всех карточек окна: на один источник один volmeter,
    /// даже если его выбрали в нескольких местах. Освобождается вместе с окном.
    /// </summary>
    internal sealed class ProbeHub : IDisposable
    {
        private readonly Dictionary<string, (ObsLevelProbe Probe, int Refs)> _probes =
            new Dictionary<string, (ObsLevelProbe Probe, int Refs)>(StringComparer.Ordinal);

        public ObsLevelProbe Acquire(string sourceName)
        {
            if (_probes.TryGetValue(sourceName, out var entry))
            {
                _probes[sourceName] = (entry.Probe, entry.Refs + 1);
                return entry.Probe;
            }

            var probe = new ObsLevelProbe(sourceName);
            _probes[sourceName] = (probe, 1);
            return probe;
        }

        public void Release(ObsLevelProbe? probe)
        {
            if (probe == null || !_probes.TryGetValue(probe.SourceName, out var entry)) return;

            if (entry.Refs <= 1)
            {
                _probes.Remove(probe.SourceName);
                probe.Dispose();
            }
            else
            {
                _probes[probe.SourceName] = (entry.Probe, entry.Refs - 1);
            }
        }

        public void Dispose()
        {
            foreach (var (probe, _) in _probes.Values) probe.Dispose();
            _probes.Clear();
        }
    }
}
