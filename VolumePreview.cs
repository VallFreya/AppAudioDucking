namespace ProcessMuterByChannel
{
    /// <summary>
    /// Предпросмотр уровня в окне настроек: пока двигаешь ползунок, громкость цели (процесса Windows или источника OBS)
    /// сразу следует за ним, а когда ползунок отпущен, возвращается к исходной. Исходная громкость запоминается
    /// в момент первого изменения, поэтому предпросмотр ничего не меняет в настройках фильтра.
    /// Если цель сейчас приглушена настоящим фильтром, ею управляет движок, и предпросмотр её не трогает.
    /// </summary>
    internal sealed class VolumePreview
    {
        private const int IdleRestoreMs = 1500;   // через сколько после последнего движения возвращать громкость
        private const int ApplyEveryMs = 40;      // чаще громкость не выставляется, даже если ползунок двигают быстрее

        private static readonly List<VolumePreview> ActivePreviews = new List<VolumePreview>();
        /// <summary>Бэкенд громкости. Заменяется только в проверках</summary>
        internal static IVolumeBackend Backend { get; set; } = new RoutingBackend();

        private PreviewClaim? _claim;
        private bool _recoveryNoted;   // исходная громкость записана в файл восстановления на случай аварийного выхода во время предпросмотра

        private readonly object _lock = new object();
        private readonly Dictionary<string, float> _baseVolumes = new Dictionary<string, float>();
        private string _target = "";
        private float _level;
        private bool _dirty;
        private bool _exact;
        private bool _held;
        private long _lastChangeMs;
        private long _lastApplyMs;

        public bool IsActive
        {
            get { lock (_lock) return _target.Length > 0; }
        }

        /// <summary>
        /// Ползунок сдвинут: громкость цели станет level. Для ползунка приглушения (isReturn = false) цель только опускается,
        /// как при настоящем приглушении (не громче исходной). Для ползунка возврата громкость ставится ровно в level,
        /// даже если она выше исходной, потому что именно до этого уровня цель будет возвращаться.
        /// </summary>
        public void SetLevel(string target, float level, bool isReturn = false)
        {
            if (string.IsNullOrEmpty(target)) return;

            // движок уже управляет этой целью
            if (AudioMuterManager.TryGetDuckedOriginal(target, out _)) return;

            lock (_lock)
            {
                if (_target.Length > 0 && !string.Equals(_target, target, StringComparison.Ordinal)) CancelLocked();

                if (_target.Length == 0)
                {
                    _target = target;
                    _claim = new PreviewClaim();
                    PreviewRegistry.Claims[target] = _claim;
                    lock (ActivePreviews) ActivePreviews.Add(this);
                }

                // цель уже приглушает движок (начал, пока ползунок был зажат): предпросмотр её больше не трогает
                if (_claim!.TakenOver) return;

                _level = Math.Max(0.0f, level);
                _exact = isReturn;
                _dirty = true;
                _lastChangeMs = Environment.TickCount64;
            }

            Tick();
        }

        /// <summary>Вызывается таймером окна: выставляет накопленный уровень и возвращает громкость, когда ползунок давно не двигали</summary>
        public void Tick()
        {
            lock (_lock)
            {
                if (_target.Length == 0) return;

                long now = Environment.TickCount64;
                // пока ползунок зажат, громкость держится сколько угодно; иначе возвращается после простоя
                if (!_held && now - _lastChangeMs > IdleRestoreMs)
                {
                    CancelLocked();
                    return;
                }

                // движок взял цель себе: ничего не применяем и ничего не возвращаем, когда ползунок отпустят
                if (_claim!.TakenOver)
                {
                    _dirty = false;
                    return;
                }

                if (_dirty && now - _lastApplyMs >= ApplyEveryMs)
                {
                    _dirty = false;
                    _lastApplyMs = now;

                    // Сначала запоминаем исходные громкости, ничего не меняя (factor 1 и возврат до исходной), и сразу показываем их движку:
                    // иначе движок, начавший приглушать цель между изменением громкости и записью исходной, принял бы сниженную за исходную
                    if (_baseVolumes.Count == 0)
                    {
                        Backend.ApplyFactor(_target, 1.0f, 0.0f, float.PositiveInfinity, _baseVolumes);
                        _claim.Originals = new Dictionary<string, float>(_baseVolumes);

                        // Громкость источника OBS попадает в сохранение сцен, а громкость приложения Windows помнит сама система:
                        // если OBS упадёт, пока ползунок зажат, сниженная громкость осталась бы насовсем. Запись вернёт её при следующем запуске
                        if (!_recoveryNoted && _baseVolumes.Count > 0)
                        {
                            _recoveryNoted = true;
                            try { PreviewRegistry.RecoveryNote(_target, _baseVolumes.Values.Max()); } catch { }
                        }
                    }

                    // приглушение: factor 0, громкость = min(исходная, level), как во время настоящего приглушения;
                    // возврат: factor 1 и restoreLevel = level, то есть ровно level
                    if (_exact) Backend.ApplyFactor(_target, 1.0f, 0.0f, _level, _baseVolumes);
                    else Backend.ApplyFactor(_target, 0.0f, _level, float.PositiveInfinity, _baseVolumes);

                    // копия исходных громкостей для движка: если он начнёт приглушать цель, ему нужны они, а не сниженные
                    _claim.Originals = new Dictionary<string, float>(_baseVolumes);
                }
            }
        }

        /// <summary>Ползунок зажат: громкость остаётся на его уровне, пока его не отпустят (без таймера простоя)</summary>
        public void Hold()
        {
            lock (_lock)
            {
                if (_target.Length == 0) return;
                _held = true;
                _lastChangeMs = Environment.TickCount64;
            }
        }

        /// <summary>Ползунок отпущен: громкость сразу возвращается к исходной</summary>
        public void Release()
        {
            lock (_lock)
            {
                _held = false;
                CancelLocked();
            }
        }

        /// <summary>Сразу возвращает исходную громкость</summary>
        public void Cancel()
        {
            lock (_lock) CancelLocked();
        }

        private void CancelLocked()
        {
            if (_target.Length == 0) return;
            _held = false;

            // Если движок забрал цель, её громкостью управляет он (и вернёт исходную сам): возврат отсюда сорвал бы его приглушение
            bool restored = false;
            if (_claim != null && !_claim.TakenOver) restored = Backend.RestoreVolume(_target, float.PositiveInfinity, _baseVolumes);
            // запись стирается, только если громкость действительно вернули (цель нашлась) и она не перешла к движку
            if (_recoveryNoted && restored)
            {
                try { PreviewRegistry.RecoveryForget(_target); } catch { }
            }
            _recoveryNoted = false;
            if (_claim != null) PreviewRegistry.Claims.TryRemove(new KeyValuePair<string, PreviewClaim>(_target, _claim));
            _baseVolumes.Clear();
            _target = "";
            _claim = null;
            _dirty = false;
            lock (ActivePreviews) ActivePreviews.Remove(this);
        }

        /// <summary>Возвращает громкость всех предпросмотров (выгрузка плагина при открытом окне настроек)</summary>
        public static void RestoreAll()
        {
            VolumePreview[] previews;
            lock (ActivePreviews) previews = ActivePreviews.ToArray();
            foreach (var preview in previews) preview.Cancel();
        }
    }

    /// <summary>Предпросмотр, который сейчас меняет громкость цели. Виден движку приглушения</summary>
    internal sealed class PreviewClaim
    {
        // Исходные громкости до предпросмотра; заменяется целиком, поэтому движок читает её без блокировок
        public volatile Dictionary<string, float> Originals = new Dictionary<string, float>();
        // Движок начал приглушать эту цель сам: громкостью теперь управляет он
        public volatile bool TakenOver;
    }

    /// <summary>
    /// Связь предпросмотра с движком без общих блокировок (блокировки двух сторон держатся в разном порядке, и
    /// общая привела бы к взаимному ожиданию). Предпросмотр записывает сюда цель и её исходные громкости, а движок,
    /// начиная приглушать цель, забирает их и помечает цель занятой собой.
    /// </summary>
    internal sealed class PreviewRegistry : IExternalVolumeOwner
    {
        public static readonly System.Collections.Concurrent.ConcurrentDictionary<string, PreviewClaim> Claims =
            new System.Collections.Concurrent.ConcurrentDictionary<string, PreviewClaim>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Запись исходной громкости в файл восстановления и её стирание. В проверках подменяются</summary>
        public static Action<string, float> RecoveryNote { get; set; } = AudioMuterManager.NoteRecovery;
        public static Action<string> RecoveryForget { get; set; } = AudioMuterManager.ForgetRecovery;

        public Dictionary<string, float>? TakeOver(string target)
        {
            if (!Claims.TryGetValue(target, out var claim)) return null;

            claim.TakenOver = true;
            return claim.Originals;
        }
    }}
