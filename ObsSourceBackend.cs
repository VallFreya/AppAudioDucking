using System.Globalization;
using System.Runtime.InteropServices;

namespace ProcessMuterByChannel
{
    /// <summary>
    /// Меняет громкость звукового источника OBS (фейдер в микшере) по тому же принципу, что и громкость процесса:
    /// «исходная громкость × factor». Громкость источника OBS хранится в коллекции сцен, поэтому исходное значение
    /// ещё и записывается в файл восстановления (см. RecoveryStore), на случай если OBS закроется во время приглушения.
    /// </summary>
    internal sealed class ObsSourceBackend : IVolumeBackend
    {
        public const string Prefix = "obs:";
        private const string VolumeKey = "volume";

        [DllImport("obs", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr obs_get_source_by_name([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

        [DllImport("obs", CallingConvention = CallingConvention.Cdecl)]
        private static extern void obs_source_release(IntPtr source);

        [DllImport("obs", CallingConvention = CallingConvention.Cdecl)]
        private static extern float obs_source_get_volume(IntPtr source);

        [DllImport("obs", CallingConvention = CallingConvention.Cdecl)]
        private static extern void obs_source_set_volume(IntPtr source, float volume);

        // Для источника OBS одна громкость и нет «новых сессий», поэтому повторно её применять не нужно
        public bool SupportsEnforce(string target) => false;

        public bool ApplyFactor(string target, float factor, Dictionary<string, float> baseVolumes)
        {
            IntPtr source = Find(target);
            if (source == IntPtr.Zero) return false;

            try
            {
                if (!baseVolumes.TryGetValue(VolumeKey, out float baseVolume))
                {
                    baseVolume = obs_source_get_volume(source);
                    baseVolumes[VolumeKey] = baseVolume;
                }

                // громкость источника в OBS может быть выше 1 (усиление), поэтому сверху не ограничиваем
                obs_source_set_volume(source, Math.Max(0.0f, baseVolume * factor));
                return true;
            }
            catch (Exception ex)
            {
                Plugin.LogError($"Failed to set volume of OBS source '{target}': {ex.Message}");
                return false;
            }
            finally
            {
                obs_source_release(source);
            }
        }

        public bool RestoreVolume(string target, Dictionary<string, float> baseVolumes)
        {
            if (baseVolumes.Count == 0) return true;

            bool applied = ApplyFactor(target, 1.0f, baseVolumes);
            baseVolumes.Clear();
            return applied;
        }

        public bool Recover(string target, float baseVolume)
        {
            IntPtr source = Find(target);
            if (source == IntPtr.Zero) return false;

            try
            {
                obs_source_set_volume(source, Math.Max(0.0f, baseVolume));
                return true;
            }
            catch (Exception ex)
            {
                Plugin.LogError($"Failed to recover volume of OBS source '{target}': {ex.Message}");
                return false;
            }
            finally
            {
                obs_source_release(source);
            }
        }

        private static IntPtr Find(string target)
        {
            try
            {
                return obs_get_source_by_name(target.Substring(Prefix.Length));
            }
            catch
            {
                // нет obs.dll (проверка вне OBS)
                return IntPtr.Zero;
            }
        }
    }

    /// <summary>Выбирает бэкенд по ключу цели: «obs:Имя» - источник OBS, иначе имя процесса Windows</summary>
    internal sealed class RoutingBackend : IVolumeBackend
    {
        private readonly IVolumeBackend _process = new CoreAudioBackend();
        private readonly IVolumeBackend _obs = new ObsSourceBackend();

        private IVolumeBackend For(string target) => target.StartsWith(ObsSourceBackend.Prefix, StringComparison.Ordinal) ? _obs : _process;

        public bool SupportsEnforce(string target) => For(target).SupportsEnforce(target);
        public bool ApplyFactor(string target, float factor, Dictionary<string, float> baseVolumes) => For(target).ApplyFactor(target, factor, baseVolumes);
        public bool RestoreVolume(string target, Dictionary<string, float> baseVolumes) => For(target).RestoreVolume(target, baseVolumes);
        public bool Recover(string target, float baseVolume) => For(target).Recover(target, baseVolume);
    }

    /// <summary>Файл восстановления: цель → исходная громкость, пока цель приглушена</summary>
    internal interface IRecoveryStore
    {
        Dictionary<string, float> Load();
        void Save(IReadOnlyDictionary<string, float> entries);
    }

    /// <summary>
    /// Простой текстовый файл «громкость[TAB]ключ» в папке настроек плагина. Пишется в момент начала приглушения
    /// и стирается, когда громкость возвращена. Если OBS завершился во время приглушения (сбой, принудительное
    /// закрытие, или OBS успел сохранить сцены с пониженной громкостью), при следующем запуске громкость вернётся.
    /// </summary>
    internal sealed class RecoveryStore : IRecoveryStore
    {
        private readonly string _path;
        private readonly object _lock = new object();

        public RecoveryStore(string path) { _path = path; }

        public Dictionary<string, float> Load()
        {
            var result = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);

            try
            {
                if (!File.Exists(_path)) return result;

                foreach (var line in File.ReadAllLines(_path))
                {
                    int tab = line.IndexOf('\t');
                    if (tab <= 0) continue;

                    if (float.TryParse(line.AsSpan(0, tab), NumberStyles.Float, CultureInfo.InvariantCulture, out float volume) && float.IsFinite(volume))
                    {
                        result[line.Substring(tab + 1)] = volume;
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.LogError($"Failed to read recovery file '{_path}': {ex.Message}");
            }

            return result;
        }

        public void Save(IReadOnlyDictionary<string, float> entries)
        {
            lock (_lock)
            {
                try
                {
                    if (entries.Count == 0)
                    {
                        if (File.Exists(_path)) File.Delete(_path);
                        return;
                    }

                    var lines = new List<string>(entries.Count);
                    foreach (var (key, volume) in entries) lines.Add(volume.ToString("R", CultureInfo.InvariantCulture) + "\t" + key);
                    File.WriteAllLines(_path, lines);
                }
                catch (Exception ex)
                {
                    Plugin.LogError($"Failed to write recovery file '{_path}': {ex.Message}");
                }
            }
        }
    }
}
