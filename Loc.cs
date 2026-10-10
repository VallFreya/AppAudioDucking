using System.Runtime.InteropServices;

namespace ProcessMuterByChannel
{
    /// <summary>
    /// Тексты интерфейса. Язык берётся из OBS (obs_get_locale): русский для ru-*, иначе английский.
    /// Пункт меню Сервис нельзя переименовать после регистрации, поэтому язык определяется один раз
    /// и меняется вместе с языком OBS (после перезапуска OBS).
    /// </summary>
    internal static class Loc
    {
        [DllImport("obs", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr obs_get_locale();

        private static bool _initialized;
        private static bool _ru;

        private static bool Ru
        {
            get
            {
                if (!_initialized)
                {
                    try
                    {
                        string? locale = Marshal.PtrToStringUTF8(obs_get_locale());
                        _ru = locale != null && locale.StartsWith("ru", StringComparison.OrdinalIgnoreCase);
                    }
                    catch
                    {
                        _ru = false;
                    }
                    _initialized = true;
                }
                return _ru;
            }
        }

        // Название плагина: пункт меню Сервис и заголовок окна
        public static string PluginName => Ru ? "Автоприглушение приложений" : "App Audio Ducking";

        public static string WindowTitle => Ru ? $"{PluginName} — настройки" : $"{PluginName} Settings";
        public static string AddFilter => Ru ? "Добавить фильтр" : "Add filter";
        public static string NoFilters => Ru ? "Фильтров пока нет. Нажмите «Добавить фильтр»." : "No filters yet. Click \"Add filter\".";
        public static string Save => Ru ? "Сохранить" : "Save";
        public static string Saved => Ru ? "Настройки сохранены" : "Settings saved";
        public static string RefreshProcesses => Ru ? "Обновить список процессов" : "Refresh process list";
        public static string RemoveFilter => Ru ? "Удалить фильтр" : "Remove filter";
        public static string FilterTitle(int number) => Ru ? $"Фильтр {number}" : $"Filter {number}";

        public static string Scene => Ru ? "Сцена:" : "Scene:";
        public static string TargetKind => Ru ? "Что глушить:" : "What to mute:";
        public static string TargetKindProcess => Ru ? "Процесс Windows" : "Windows process";
        public static string TargetKindObs => Ru ? "Звук в OBS" : "OBS audio source";
        public static string TargetScene => Ru ? "Сцена цели:" : "Target scene:";
        public static string TargetSource => Ru ? "Источник цели:" : "Target source:";
        public static string AudioSource => Ru ? "Источник звука:" : "Audio source:";
        public static string Process => Ru ? "Процесс (без .exe):" : "Process (without .exe):";
        public static string Threshold => Ru ? "Порог срабатывания:" : "Threshold level:";
        public static string Fade => Ru ? "Время затухания:" : "Fade duration:";
        public static string Hold => Ru ? "Задержка возврата:" : "Restore delay:";
        public static string HoldHint => Ru
            ? "Задержка возврата звука\n\n" +
              "Зачем. В источнике бывают паузы: речь, оповещения, озвучка доната. " +
              "Без задержки в каждой паузе звук приложения возвращался бы, а через мгновение снова приглушался, и музыка «дёргалась» бы.\n\n" +
              "Как работает. Когда звук в источнике стихает, приложение остаётся приглушённым ещё указанное время. " +
              "Если за это время звук появился снова, приглушение не снимается. Если тишина длится дольше, громкость плавно возвращается.\n\n" +
              "Пример. При 1500 мс пауза короче 1,5 секунды звук приложения не вернёт.\n" +
              "0 - возвращать звук сразу, как только источник стих."
            : "Restore delay\n\n" +
              "Why. A source often has pauses: speech, alerts, donation voice-overs. " +
              "Without a delay the app would come back in every pause and be ducked again a moment later, so the music would keep jumping.\n\n" +
              "How it works. When the source goes quiet, the app stays ducked for the time set here. " +
              "If sound returns within that time, the ducking is not released. If the silence lasts longer, the volume fades back smoothly.\n\n" +
              "Example. With 1500 ms, a pause shorter than 1.5 seconds will not bring the app's sound back.\n" +
              "0 = restore as soon as the source goes quiet.";
        public static string Decibels => Ru ? "дБ" : "dB";
        public static string Milliseconds => Ru ? "мс" : "ms";
        public static string GlobalSources => Ru ? "(Глобальные источники звука)" : "(Global audio sources)";
        public static string Theme => Ru ? "Тема:" : "Theme:";
        public static string ThemeDarkName => Ru ? "Тёмная" : "Dark";
        public static string ThemeLightName => Ru ? "Светлая" : "Light";
    }
}
