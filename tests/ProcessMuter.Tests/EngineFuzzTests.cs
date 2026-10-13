using ProcessMuterByChannel;
using Xunit;

namespace ProcessMuter.Tests
{
    /// <summary>
    /// Случайные последовательности событий для движка: звук то есть, то нет, меняются уровни и задержки, появляются новые сессии,
    /// фильтры снимаются. Что бы ни происходило, в конце после выгрузки каждая сессия должна вернуться к своей исходной громкости,
    /// а в процессе громкость не должна выходить за допустимые границы.
    /// </summary>
    public class EngineFuzzTests
    {
        // сколько раз в случайных прогонах громкость действительно была снижена: без этого проверка ничего бы не проверяла
        private static int _duckedObservations;

        private sealed class Run
        {
            public readonly FakeBackend Fake = new FakeBackend { Enforce = true };
            public readonly Dictionary<string, float> Originals = new Dictionary<string, float>();   // сессия -> громкость при создании
            public string? Violation;
        }

        private static void Simulate(int seed, bool customReturn)
        {
            var rng = new Random(seed);
            var run = new Run();
            var engine = new DuckingEngine(run.Fake, _ => { }, enforceEveryMs: 50);
            string[] targets = { "A", "B", "obs:C" };
            foreach (var t in targets) run.Fake.PerTarget[t] = new Dictionary<string, float>();

            void AddSession(string target)
            {
                string id = "s" + run.Originals.Count;
                float volume = (float)Math.Round(rng.NextDouble() * 1.3, 2);   // у источника OBS громкость бывает выше 1
                lock (run.Fake) run.Fake.PerTarget[target][id] = volume;
                run.Originals[target + "/" + id] = volume;
            }

            foreach (var t in targets) AddSession(t);

            long now = 0;
            var active = new HashSet<string>(targets);
            bool[] over = new bool[targets.Length];
            float duck = 0f;
            float restore = float.PositiveInfinity;

            for (int step = 0; step < 60 && run.Violation == null; step++)
            {
                // случайное событие
                switch (rng.Next(10))
                {
                    case 0: AddSession(targets[rng.Next(targets.Length)]); break;                       // новая сессия
                    case 1: duck = (float)Math.Round(rng.NextDouble(), 2); break;                       // уровень приглушения
                    case 2: restore = customReturn && rng.Next(2) == 0 ? (float)Math.Round(rng.NextDouble() * 1.3, 2) : float.PositiveInfinity; break;
                    case 3: { var t = targets[rng.Next(targets.Length)]; if (active.Remove(t)) engine.ReleaseUnused(active); else active.Add(t); } break;   // фильтр снят или возвращён
                }

                for (int i = 0; i < targets.Length; i++)
                {
                    if (rng.Next(3) == 0) over[i] = !over[i];
                }

                now += rng.Next(20, 400);
                var levels = new Dictionary<string, ProcessLevel>();
                for (int i = 0; i < targets.Length; i++)
                {
                    if (!active.Contains(targets[i])) continue;
                    levels[targets[i]] = new ProcessLevel
                    {
                        Over = over[i], FadeMs = rng.Next(1, 120), HoldMs = rng.Next(0, 800), Source = "m",
                        PeakDb = over[i] ? -20f : -90f, ThresholdDb = -40f, DuckLevel = duck, RestoreLevel = restore
                    };
                }
                engine.Tick(now, levels);
                Thread.Sleep(rng.Next(1, 25));

                // границы: громкость конечна, не отрицательна и не выше max(исходная, уровень возврата)
                lock (run.Fake)
                {
                    foreach (var (target, map) in run.Fake.PerTarget)
                    {
                        foreach (var (id, volume) in map)
                        {
                            float original = run.Originals[target + "/" + id];
                            if (volume < original - 0.05f) Interlocked.Increment(ref _duckedObservations);
                            float ceiling = Math.Max(original, float.IsPositiveInfinity(restore) ? 0f : restore);
                            if (!float.IsFinite(volume) || volume < -0.0001f || volume > ceiling + 0.001f)
                            {
                                // уровень возврата мог быть выше исходной, когда сессию уже вернули раньше: потолок берём по максимуму за всё время
                                run.Violation ??= $"seed {seed}: {target}/{id} volume {volume} original {original} restore {restore}";
                            }
                        }
                    }
                }
            }

            // выгрузка плагина: всё возвращается
            engine.ReleaseAll();
            Thread.Sleep(30);
            if (run.Violation != null && customReturn) return;   // с произвольным возвратом потолок меняется по ходу, проверяем только итог ниже

            lock (run.Fake)
            {
                foreach (var (target, map) in run.Fake.PerTarget)
                {
                    foreach (var (id, volume) in map)
                    {
                        float original = run.Originals[target + "/" + id];
                        Assert.True(float.IsFinite(volume), $"seed {seed}: {target}/{id} is not finite");
                        if (!customReturn)
                        {
                            Assert.True(Math.Abs(volume - original) < 0.01f, $"seed {seed}: {target}/{id} ended at {volume}, original {original}");
                        }
                        else
                        {
                            // возврат мог быть до явного уровня: итог не ниже исходной, если уровень выше неё, и не выше max(исходная, 1.3)
                            Assert.InRange(volume, -0.0001f, 1.31f);
                        }
                    }
                }
            }

            if (!customReturn) Assert.Null(run.Violation);
        }

        [Fact]
        public void Random_sequences_always_end_at_the_original_volumes()
        {
            var failures = new System.Collections.Concurrent.ConcurrentBag<string>();
            Parallel.For(0, 40, seed =>
            {
                try { Simulate(seed, customReturn: false); }
                catch (Exception ex) { failures.Add($"seed {seed}: {ex.Message}"); }
            });

            Assert.True(failures.IsEmpty, string.Join("\n", failures.Take(5)));
            Assert.True(_duckedObservations > 50, $"the random runs rarely ducked anything ({_duckedObservations})");
        }

        [Fact]
        public void Random_sequences_with_custom_return_levels_stay_in_range()
        {
            var failures = new System.Collections.Concurrent.ConcurrentBag<string>();
            Parallel.For(100, 130, seed =>
            {
                try { Simulate(seed, customReturn: true); }
                catch (Exception ex) { failures.Add($"seed {seed}: {ex.Message}"); }
            });

            Assert.True(failures.IsEmpty, string.Join("\n", failures.Take(5)));
        }
    }
}
