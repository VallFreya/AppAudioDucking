using System;
using System.Diagnostics;
using NAudio.CoreAudioApi;

namespace ProcessMuterByChannel
{
    public static class ProcessAudioControl
    {
        public static void SetProcessVolume(string processName, float volume)
        {
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                var sessionManager = device.AudioSessionManager;
                var sessions = sessionManager.Sessions;

                for (int i = 0; i < sessions.Count; i++)
                {
                    var session = sessions[i];
                    var process = session.GetProcessID != 0 ? Process.GetProcessById((int)session.GetProcessID) : null;

                    if (process != null && process.ProcessName.Equals(processName, StringComparison.OrdinalIgnoreCase))
                    {
                        session.SimpleAudioVolume.Volume = Math.Clamp(volume, 0.0f, 1.0f);
                        session.SimpleAudioVolume.Mute = false;
                    }
                }
            }
            catch
            {
                // Игнорируем ошибки при обращении к процессам без аудиосессий
            }
        }
    }
}