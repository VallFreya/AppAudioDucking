using System;
using System.Collections.Generic;
using System.Diagnostics;
using NAudio.CoreAudioApi;

namespace ProcessMuterByChannel
{
    public static class ProcessAudioControl
    {
        /// <summary>Имена запущенных процессов без .exe, без повторов, по алфавиту</summary>
        public static string[] GetRunningProcessNames()
        {
            var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var process in Process.GetProcesses())
            {
                try
                {
                    names.Add(process.ProcessName);
                }
                catch
                {
                    // процесс мог завершиться или быть недоступен
                }
                finally
                {
                    process.Dispose();
                }
            }

            var result = new string[names.Count];
            names.CopyTo(result);
            return result;
        }

        /// <summary>
        /// Выставляет громкость аудиосессий процесса как «исходная громкость × factor».
        /// Исходная громкость каждой сессии запоминается в baseVolumes при первом обращении
        /// (сессия, которая появилась позже, запоминается в момент появления).
        /// Возвращает true, если у процесса нашлась хотя бы одна аудиосессия
        /// </summary>
        public static bool ApplyFactor(string processName, float factor, Dictionary<string, float> baseVolumes)
        {
            bool applied = false;

            try
            {
                using var enumerator = new MMDeviceEnumerator();
                using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                var sessions = device.AudioSessionManager.Sessions;
                var matchByPid = new Dictionary<uint, bool>();

                for (int i = 0; i < sessions.Count; i++)
                {
                    try
                    {
                        var session = sessions[i];
                        uint pid = session.GetProcessID;
                        if (pid == 0 || !IsProcessNamed(pid, processName, matchByPid)) continue;

                        string key = session.GetSessionInstanceIdentifier;
                        if (string.IsNullOrEmpty(key)) key = $"pid:{pid}:{i}";

                        var volume = session.SimpleAudioVolume;
                        if (!baseVolumes.TryGetValue(key, out float baseVolume))
                        {
                            baseVolume = volume.Volume;
                            baseVolumes[key] = baseVolume;
                        }

                        volume.Volume = Math.Clamp(baseVolume * factor, 0.0f, 1.0f);
                        applied = true;
                    }
                    catch
                    {
                        // сессия могла закрыться во время обхода, остальные продолжаем обрабатывать
                    }
                }
            }
            catch
            {
                // Игнорируем ошибки при обращении к процессам без аудиосессий
            }

            return applied;
        }

        /// <summary>Возвращает сессиям процесса запомненную громкость и забывает её. true, если сессии нашлись</summary>
        public static bool RestoreVolume(string processName, Dictionary<string, float> baseVolumes)
        {
            if (baseVolumes.Count == 0) return true;

            bool applied = ApplyFactor(processName, 1.0f, baseVolumes);
            baseVolumes.Clear();
            return applied;
        }

        /// <summary>
        /// Поднимает до baseVolume сессии процесса, у которых громкость ниже: так исправляется громкость,
        /// оставшаяся сниженной после аварийного завершения OBS. true, если у процесса есть аудиосессии
        /// </summary>
        public static bool RecoverVolume(string processName, float baseVolume)
        {
            bool found = false;

            try
            {
                using var enumerator = new MMDeviceEnumerator();
                using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                var sessions = device.AudioSessionManager.Sessions;
                var matchByPid = new Dictionary<uint, bool>();

                for (int i = 0; i < sessions.Count; i++)
                {
                    try
                    {
                        var session = sessions[i];
                        uint pid = session.GetProcessID;
                        if (pid == 0 || !IsProcessNamed(pid, processName, matchByPid)) continue;

                        found = true;
                        var volume = session.SimpleAudioVolume;
                        if (volume.Volume < baseVolume - 0.001f) volume.Volume = Math.Clamp(baseVolume, 0.0f, 1.0f);
                    }
                    catch
                    {
                        // сессия могла закрыться во время обхода
                    }
                }
            }
            catch
            {
                // нет аудиоустройства или доступ закрыт
            }

            return found;
        }

        private static bool IsProcessNamed(uint pid, string processName, Dictionary<uint, bool> cache)
        {
            if (cache.TryGetValue(pid, out bool known)) return known;

            bool match = false;
            try
            {
                using var process = Process.GetProcessById((int)pid);
                match = process.ProcessName.Equals(processName, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                // процесс уже завершился
            }

            cache[pid] = match;
            return match;
        }
    }
}
