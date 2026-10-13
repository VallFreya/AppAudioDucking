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
        public static string AutoSaveHint => Ru ? "Изменения сохраняются и применяются сразу" : "Changes are saved and applied right away";
        public static string Saved => Ru ? "Сохранено" : "Saved";
        public static string SaveFailed => Ru
            ? "Не удалось записать настройки в файл (подробности в журнале). Фильтры применены, но после перезапуска OBS пропадут"
            : "Could not write the settings file (see the log). The filters are applied but will be lost after OBS restarts";
        public static string Ok => Ru ? "ОК" : "OK";
        public static string Cancel => Ru ? "Отмена" : "Cancel";
        public static string ConfirmRemoveTitle => Ru ? "Удалить фильтр?" : "Remove filter?";
        public static string ConfirmRemoveBody(string name) => Ru
            ? $"Фильтр «{name}» будет удалён вместе со всеми его настройками. Это действие нельзя отменить."
            : $"The filter \"{name}\" will be removed together with all its settings. This cannot be undone.";
        public static string RefreshProcesses => Ru ? "Обновить список процессов" : "Refresh process list";
        public static string RemoveFilter => Ru ? "Удалить фильтр" : "Remove filter";
        public static string DuplicateFilter => Ru ? "Дублировать фильтр" : "Duplicate filter";
        public static string CopyName(string name) => Ru ? $"{name} (копия)" : $"{name} (copy)";
        public static string SwitchTip => Ru ? "Включить или выключить фильтр" : "Turn the filter on or off";
        public static string EditNameTip => Ru ? "Название фильтра: нажмите, чтобы изменить" : "Filter name: click to edit";

        // Плашка состояния в заголовке карточки
        public static string ChipDucking => Ru ? "Приглушает" : "Ducking";
        public static string ChipWaiting => Ru ? "Ждёт звука" : "Waiting for sound";
        public static string ChipOff => Ru ? "Выключен" : "Off";
        public static string ChipNotReady => Ru ? "Не настроен" : "Not set up";

        // Значения рядом с индикаторами
        public static string ReadoutThreshold => Ru ? "Порог" : "Trigger";
        public static string ReadoutLevel => Ru ? "Звук сейчас" : "Level now";
        public static string ReadoutObsVolume => Ru ? "Громкость в OBS" : "OBS volume";
        public static string ReadoutDuck => Ru ? "Глушить до" : "Duck to";
        public static string ReadoutReturn => Ru ? "Возврат до" : "Return to";
        public static string ReadoutNow => Ru ? "Сейчас" : "Now";
        public static string ReadoutOriginal => Ru ? "исходная" : "original";
        public static string ReadoutSilence => Ru ? "тишина" : "silence";
        public static string ReadoutUnknown => "-";
        public static string Db(string value) => Ru ? $"{value} дБ" : $"{value} dB";
        public static string MsUnit => Ru ? "мс" : "ms";

        // Фраза-сводка. Части, обрамлённые ** , выводятся жирным
        public static string SummaryTitle => Ru ? "Как это сработает" : "What will happen";
        public static string SummaryPick => Ru
            ? "Выберите источник звука и что приглушать, и здесь появится описание."
            : "Pick an audio source and what to duck, and a description will appear here.";
        public static string Summary(string source, string threshold, string target, string duck, string fade, string hold, string back) => Ru
            ? $"Когда **{source}** громче **{threshold}**, **{target}** плавно стихает до **{duck}** за **{fade}**. Через **{hold}** после тишины звук возвращается до **{back}**."
            : $"When **{source}** is louder than **{threshold}**, **{target}** fades to **{duck}** over **{fade}**. **{hold}** after the sound stops, it returns to **{back}**.";
        public static string SummaryBackOriginal => Ru ? "исходной громкости" : "its original volume";
        public static string SummaryToSilence => Ru ? "тишины" : "silence";
        public static string FilterTitle(int number) => Ru ? $"Фильтр {number}" : $"Filter {number}";

        public static string Scene => Ru ? "Сцена:" : "Scene:";
        public static string TargetTitle => Ru ? "Что приглушать" : "What to duck";
        public static string TimeTitle => Ru ? "Настройки" : "Settings";
        public static string TargetKindProcess => Ru ? "Процесс Windows" : "Windows process";
        public static string TargetKindObs => Ru ? "Звук в OBS" : "OBS audio source";
        public static string TargetScene => Ru ? "Сцена цели:" : "Target scene:";
        public static string TargetSource => Ru ? "Источник цели:" : "Target source:";
        public static string AudioSource => Ru ? "Источник звука:" : "Audio source:";
        public static string Process => Ru ? "Процесс (без .exe):" : "Process (without .exe):";
        public static string Fade => Ru ? "Время затухания:" : "Fade duration:";

        public static string MonitorTitle => Ru ? "Что отслеживать" : "What to monitor";
        public static string ThresholdTitle => Ru ? "Порог срабатывания" : "Trigger threshold";
        public static string ThresholdBody => Ru
            ? "Приглушение начнётся, когда уровень источника поднимется выше ползунка.\nСерый ползунок: как громкость источника выставлена в OBS."
            : "Ducking starts when the source level rises above the slider.\nGrey slider: the source volume currently set in OBS.";
        public static string DuckTitle => Ru ? "Приглушить до" : "Duck to";
        public static string ThresholdShort => Ru ? "Выше этого уровня начинается приглушение." : "Ducking starts above this level.";
        public static string DuckShort => Ru ? "Нажмите на ползунок и держите, чтобы послушать." : "Press and hold a slider to listen.";
        public static string SliderThreshold => Ru ? "Порог" : "Trigger";
        public static string SliderDuck => Ru ? "Глушить до" : "Duck to";
        public static string SliderReturn => Ru ? "Возврат до" : "Return to";
        public static string DuckBodyObs => Ru
            ? "Глушить до: до какого уровня опускать (внизу тишина).\nВозврат до: до какого вернуть. Двойной щелчок: до исходного.\nПри движении громкость меняется сразу."
            : "Duck to: the level to lower to (bottom is silence).\nReturn to: the level to go back to. Double-click: to the original.\nThe volume follows a slider as you drag it.";
        public static string DuckBodyProcess => Ru
            ? "Глушить до: до какой громкости опускать (внизу тишина).\nВозврат до: до какой вернуть. Двойной щелчок: до исходной.\nПри движении громкость меняется сразу."
            : "Duck to: the volume to lower to (bottom is silence).\nReturn to: the volume to go back to. Double-click: to the original.\nThe volume follows a slider as you drag it.";

        public static string SceneInactive => Ru ? "Сцена неактивна" : "Scene is inactive";
        public static string PickSource => Ru ? "Источник не выбран" : "No source selected";
        public static string SourceNotFound => Ru ? "Источник не найден" : "Source not found";
        public static string ProcessNotSelected => Ru ? "Процесс не выбран" : "No process selected";
        public static string ProcessNotRunning => Ru ? "Процесс не запущен или не играет" : "Process is not running or not playing";
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
        public static string Milliseconds => Ru ? "мс" : "ms";
        public static string GlobalSources => Ru ? "(Глобальные источники звука)" : "(Global audio sources)";
        public static string Theme => Ru ? "Тема:" : "Theme:";
        public static string ThemeDarkName => Ru ? "Тёмная" : "Dark";
        public static string ThemeLightName => Ru ? "Светлая" : "Light";
    }
}
