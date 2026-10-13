using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using ProcessMuterByChannel;
using Xunit;

#pragma warning disable CS0618 // WasapiOut помечен устаревшим, но для проверки сессии подходит

namespace ProcessMuter.Tests
{
    /// <summary>
    /// Работа с настоящими аудиосессиями Windows: тестовый процесс сам играет тишину и проверяется как «приложение».
    /// Если на машине нет устройства вывода звука, проверки молча пропускаются (так бывает на сборочных агентах).
    /// </summary>
    [Collection("Plugin state")]
    public class AudioSessionTests : IDisposable
    {
        private readonly WasapiOut? _output;
        private readonly string _me = Process.GetCurrentProcess().ProcessName;

        public AudioSessionTests()
        {
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                _output = new WasapiOut();
                _output.Init(new SilenceProvider(new WaveFormat(44100, 16, 2)));
                _output.Play();
                Thread.Sleep(300);   // сессия появляется не мгновенно
            }
            catch
            {
                _output = null;
            }
        }

        public void Dispose() => _output?.Dispose();

        private bool HasDevice => _output != null;

        private float OwnVolume()
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var sessions = device.AudioSessionManager.Sessions;
            float max = -1;
            for (int i = 0; i < sessions.Count; i++)
            {
                var s = sessions[i];
                if (s.GetProcessID == (uint)Environment.ProcessId) max = Math.Max(max, s.SimpleAudioVolume.Volume);
            }
            return max;
        }

        private void SetOwnVolume(float volume)
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var sessions = device.AudioSessionManager.Sessions;
            for (int i = 0; i < sessions.Count; i++)
            {
                var s = sessions[i];
                if (s.GetProcessID == (uint)Environment.ProcessId) s.SimpleAudioVolume.Volume = volume;
            }
        }

        [Fact]
        public void Duck_and_restore_on_a_real_session()
        {
            if (!HasDevice) return;
            SetOwnVolume(0.6f);
            var bases = new Dictionary<string, float>();

            Assert.True(ProcessAudioControl.ApplyFactor(_me, 0.0f, 0.1f, float.PositiveInfinity, bases));
            Assert.InRange(OwnVolume(), 0.09f, 0.11f);
            Assert.NotEmpty(bases);

            // полу-затухание: между уровнем приглушения и исходной громкостью
            Assert.True(ProcessAudioControl.ApplyFactor(_me, 0.5f, 0.1f, float.PositiveInfinity, bases));
            Assert.InRange(OwnVolume(), 0.34f, 0.36f);

            Assert.True(ProcessAudioControl.RestoreVolume(_me, float.PositiveInfinity, bases));
            Assert.InRange(OwnVolume(), 0.59f, 0.61f);
            Assert.Empty(bases);
        }

        [Fact]
        public void Return_level_above_the_original_is_applied_on_a_real_session()
        {
            if (!HasDevice) return;
            SetOwnVolume(0.3f);
            var bases = new Dictionary<string, float>();

            ProcessAudioControl.ApplyFactor(_me, 0.0f, 0.1f, 0.8f, bases);
            Assert.InRange(OwnVolume(), 0.09f, 0.11f);
            ProcessAudioControl.RestoreVolume(_me, 0.8f, bases);
            Assert.InRange(OwnVolume(), 0.79f, 0.81f);
            SetOwnVolume(0.5f);
        }

        [Fact]
        public void Mute_flag_of_the_session_is_left_alone()
        {
            if (!HasDevice) return;
            SetOwnVolume(0.5f);
            var bases = new Dictionary<string, float>();
            ProcessAudioControl.ApplyFactor(_me, 0.0f, 0.0f, float.PositiveInfinity, bases);

            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var sessions = device.AudioSessionManager.Sessions;
            for (int i = 0; i < sessions.Count; i++)
            {
                if (sessions[i].GetProcessID == (uint)Environment.ProcessId) Assert.False(sessions[i].SimpleAudioVolume.Mute);
            }
            ProcessAudioControl.RestoreVolume(_me, float.PositiveInfinity, bases);
        }

        [Fact]
        public void Recover_raises_a_volume_left_low_but_never_lowers_a_higher_one()
        {
            if (!HasDevice) return;
            SetOwnVolume(0.05f);
            Assert.True(ProcessAudioControl.RecoverVolume(_me, 0.7f));
            Assert.InRange(OwnVolume(), 0.69f, 0.71f);

            Assert.True(ProcessAudioControl.RecoverVolume(_me, 0.2f));   // уже громче: не трогаем
            Assert.InRange(OwnVolume(), 0.69f, 0.71f);
            SetOwnVolume(0.5f);
        }

        [Fact]
        public void Process_name_match_ignores_case_but_expects_no_exe_suffix()
        {
            if (!HasDevice) return;
            SetOwnVolume(0.5f);
            var bases = new Dictionary<string, float>();
            Assert.True(ProcessAudioControl.ApplyFactor(_me.ToUpperInvariant(), 0f, 0.1f, float.PositiveInfinity, bases));   // регистр не важен
            ProcessAudioControl.RestoreVolume(_me, float.PositiveInfinity, bases);

            // с «.exe» имя процесса не совпадает: его снимают окно и FilterConfig.Clone, а здесь проверяется именно такой контракт
            Assert.False(ProcessAudioControl.ApplyFactor(_me + ".exe", 0f, 0.1f, float.PositiveInfinity, new Dictionary<string, float>()));
        }

        [Fact]
        public void Probe_sees_the_session_volume_and_peak_range()
        {
            if (!HasDevice) return;
            SetOwnVolume(0.42f);
            using var probe = new ProcessLevelProbe { ProcessName = _me };
            probe.ReadPeak();
            Assert.True(probe.HasSessions);
            Assert.InRange(probe.ReadVolume(), 0.41f, 0.43f);
            Assert.InRange(probe.ReadPeak(), 0f, 1f);
            SetOwnVolume(0.5f);
        }

        [Fact]
        public void Real_session_full_cycle_through_the_engine()
        {
            if (!HasDevice) return;
            SetOwnVolume(0.6f);
            var engine = new DuckingEngine(new CoreAudioBackend(), _ => { }, 100);

            engine.Tick(0, Levels.One(_me, over: true, fadeMs: 100, duck: 0.05f));
            Assert.True(Wait.Until(() => OwnVolume() < 0.06f, 5000));

            engine.Tick(10_000, Levels.One(_me, over: false, holdMs: 0, fadeMs: 100, duck: 0.05f));
            Assert.True(Wait.Until(() => Math.Abs(OwnVolume() - 0.6f) < 0.02f, 5000));
            SetOwnVolume(0.5f);
        }

        [Fact]
        public void Repeated_application_is_cheap_and_does_not_grow_memory()
        {
            if (!HasDevice) return;
            SetOwnVolume(0.5f);
            var bases = new Dictionary<string, float>();

            for (int i = 0; i < 20; i++) ProcessAudioControl.ApplyFactor(_me, 0.5f, 0.1f, float.PositiveInfinity, bases);   // прогрев
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            long before = GC.GetTotalMemory(true);
            var sw = Stopwatch.StartNew();

            for (int i = 0; i < 300; i++) ProcessAudioControl.ApplyFactor(_me, 0.5f, 0.1f, float.PositiveInfinity, bases);

            double perCall = sw.ElapsedMilliseconds / 300.0;
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            long growth = GC.GetTotalMemory(true) - before;

            Assert.True(perCall < 100, $"{perCall:F1} ms per call");   // движок вызывает это раз в 300 мс на приглушённую цель
            Assert.True(growth < 5_000_000, $"memory grew by {growth} bytes");
            ProcessAudioControl.RestoreVolume(_me, float.PositiveInfinity, bases);
        }
    }
}
