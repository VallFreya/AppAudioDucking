using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Windows.Forms;

namespace ProcessMuterByChannel
{
    public static class Plugin
    {
        public static IntPtr ModuleContext { get; private set; }
        private static readonly ObsFrontendCb MenuCallbackDelegate = OnMenuClicked;

        private static readonly string LogFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Temp",
            "ProcessMuter_debug.log"
        );

        private static readonly object LogLock = new object();

        // Контекст сериализации с поддержкой NativeAOT и сохранения кириллицы
        private static readonly PluginConfigJsonContext JsonContext = new PluginConfigJsonContext(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        });

        private static int _settingsOpen;

        /// <summary>Тема окна настроек: тёмная по умолчанию, выбор хранится в config.json</summary>
        public static bool ThemeDark { get; private set; } = true;
        private static IntPtr _moduleName = IntPtr.Zero;

        public static void LogInfo(string msg) => WriteLog("INFO", msg);
        public static void LogWarn(string msg) => WriteLog("WARN", msg);
        public static void LogError(string msg) => WriteLog("ERROR", msg);

        /// <summary>Выключается в проверках, чтобы они не писали в настоящий журнал плагина</summary>
        internal static bool LogEnabled { get; set; } = true;

        private static void WriteLog(string level, string message)
        {
            if (!LogEnabled) return;

            lock (LogLock)
            {
                try
                {
                    string line = $"[{DateTime.Now:HH:mm:ss.fff}] [{level}] {message}{Environment.NewLine}";
                    File.AppendAllText(LogFilePath, line, Encoding.UTF8);
                }
                catch { }
            }
        }

        // --- OBS Native Imports ---
        [DllImport("obs", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr obs_module_get_config_path(IntPtr module, [MarshalAs(UnmanagedType.LPStr)] string file);

        [DllImport("obs", CallingConvention = CallingConvention.Cdecl)]
        private static extern void bfree(IntPtr ptr);

        [DllImport("obs-frontend-api", CallingConvention = CallingConvention.Cdecl)]
        private static extern void obs_frontend_add_tools_menu_item(
            [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
            ObsFrontendCb callback,
            IntPtr privateData
        );

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void ObsFrontendCb(IntPtr privateData);

        // --- Module Exports ---
        [UnmanagedCallersOnly(EntryPoint = "obs_module_set_pointer", CallConvs = new[] { typeof(CallConvCdecl) })]
        public static void ObsModuleSetPointer(IntPtr module)
        {
            ModuleContext = module;
        }

        [UnmanagedCallersOnly(EntryPoint = "obs_module_ver", CallConvs = new[] { typeof(CallConvCdecl) })]
        public static uint ObsModuleVer()
        {
            return (30u << 24) | (0u << 16) | 0u;
        }

        [UnmanagedCallersOnly(EntryPoint = "obs_module_name", CallConvs = new[] { typeof(CallConvCdecl) })]
        public static unsafe char* ObsModuleName()
        {
            // OBS ждёт UTF-8, строку создаём один раз и держим до выгрузки
            if (_moduleName == IntPtr.Zero)
            {
                _moduleName = Marshal.StringToCoTaskMemUTF8(Loc.PluginName);
            }
            return (char*)_moduleName;
        }

        [UnmanagedCallersOnly(EntryPoint = "obs_module_load", CallConvs = new[] { typeof(CallConvCdecl) })]
        public static bool ObsModuleLoad()
        {
            RotateLogIfLarge();
            LogInfo("===========================================");
            LogInfo("=== Plugin ObsModuleLoad Executing ===");
            LogInfo($"Log File Path: {LogFilePath}");

            try
            {
                obs_frontend_add_tools_menu_item(Loc.PluginName, MenuCallbackDelegate, IntPtr.Zero);
                LogInfo("Tools menu item registered.");
            }
            catch (Exception ex)
            {
                LogError($"Failed to register menu item: {ex.Message}");
            }

            // Раздельно: сбой чтения настроек не должен лишать плагин таймера (он тогда хотя бы заработает с настройками из окна)
            try
            {
                LoadConfig();
            }
            catch (Exception ex)
            {
                LogError($"Failed to load the config: {ex.Message}");
            }

            try
            {
                AudioMuterManager.Start();
                LogInfo("AudioMuterManager started successfully.");
            }
            catch (Exception ex)
            {
                LogError($"Failed to start AudioMuterManager: {ex.Message}");
            }

            return true;
        }

        [UnmanagedCallersOnly(EntryPoint = "obs_module_unload", CallConvs = new[] { typeof(CallConvCdecl) })]
        public static void ObsModuleUnload()
        {
            LogInfo("=== Plugin ObsModuleUnload ===");
            try
            {
                CloseSettingsWindow();
                AudioMuterManager.Stop();
            }
            catch (Exception ex)
            {
                // исключение из экспортируемого метода уронило бы OBS при выходе
                LogError($"Error during unload: {ex.Message}");
            }
        }

        /// <summary>Путь к config.json вместо выданного OBS (для проверок вне OBS)</summary>
        internal static string? ConfigPathOverride { get; set; }

        private static string GetConfigFilePath()
        {
            if (ConfigPathOverride != null) return ConfigPathOverride;
            if (ModuleContext == IntPtr.Zero) return "process_muter_config.json";

            // Если OBS не отдал папку настроек или её нельзя создать, плагин не должен падать (этот путь нужен ещё при
            // создании движка громкости: исключение там сделало бы AudioMuterManager непригодным до перезапуска OBS)
            try
            {
                IntPtr pathPtr = obs_module_get_config_path(ModuleContext, "config.json");
                string? path = pathPtr != IntPtr.Zero ? Marshal.PtrToStringUTF8(pathPtr) : null;
                if (pathPtr != IntPtr.Zero)
                {
                    // строка выделена в OBS, освобождать её нужно через bfree; сбой здесь не должен менять путь к конфигу
                    try { bfree(pathPtr); } catch { }
                }

                if (!string.IsNullOrEmpty(path))
                {
                    string? dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }

                    return path;
                }
            }
            catch (Exception ex)
            {
                LogError($"Failed to get the OBS config folder: {ex.Message}");
            }

            return FallbackConfigPath();
        }

        // Запасное место для настроек: папка в профиле пользователя
        private static string FallbackConfigPath()
        {
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProcessMuterByChannel");
                Directory.CreateDirectory(dir);
                return Path.Combine(dir, "config.json");
            }
            catch
            {
                return "process_muter_config.json";
            }
        }

        /// <summary>Путь к служебному файлу плагина рядом с config.json</summary>
        public static string GetDataFilePath(string fileName)
        {
            string? dir = Path.GetDirectoryName(GetConfigFilePath());
            return string.IsNullOrEmpty(dir) ? fileName : Path.Combine(dir, fileName);
        }

        public static void LoadConfig()
        {
            string configPath = GetConfigFilePath();
            if (!File.Exists(configPath))
            {
                LogWarn($"Config file not found at '{configPath}'. No filters configured.");
                return;
            }

            try
            {
                string json = File.ReadAllText(configPath, Encoding.UTF8);

                if (!TryParseConfig(json, out var config, out string? error))
                {
                    // Файл не разобрался (правили вручную с ошибкой или оборвалась запись). Окно настроек при первом
                    // сохранении перезапишет его пустым списком, поэтому сначала сохраняем копию рядом
                    LogError($"Config file '{configPath}' is not valid and was not loaded: {error}");
                    BackupBrokenConfig(configPath);
                    return;
                }

                var filters = ExtractFilters(config!);
                ThemeDark = !string.Equals(config!.theme, "light", StringComparison.OrdinalIgnoreCase);

                LogInfo($"Loaded config from '{configPath}': {filters.Count} filter(s)");

                AudioMuterManager.SetFilters(filters);
            }
            catch (Exception ex)
            {
                LogError($"Error reading config file: {ex.Message}");
            }
        }

        /// <summary>Разбирает текст config.json. false и описание ошибки, если это не JSON нужной формы</summary>
        internal static bool TryParseConfig(string json, out PluginConfig? config, out string? error)
        {
            config = null;
            error = null;

            try
            {
                config = JsonSerializer.Deserialize(json, PluginConfigJsonContext.Default.PluginConfig);
                if (config == null) error = "the file is empty or contains null";
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }

            return config != null;
        }

        /// <summary>
        /// Фильтры из разобранного конфига: пустые элементы ("null" в списке) отбрасываются, конфиг самой первой версии
        /// (один источник и один процесс без списка) превращается в один фильтр
        /// </summary>
        internal static List<FilterConfig> ExtractFilters(PluginConfig config)
        {
            var filters = new List<FilterConfig>();
            if (config.filters != null)
            {
                foreach (var filter in config.filters)
                {
                    if (filter != null) filters.Add(filter);
                }
            }

            if (filters.Count == 0)
            {
                var legacy = config.MigrateLegacy();
                if (legacy != null)
                {
                    filters.Add(legacy);
                    LogInfo("Migrated legacy config to a filter.");
                }
            }

            return filters;
        }

        private static void BackupBrokenConfig(string configPath)
        {
            try
            {
                File.Copy(configPath, configPath + ".broken", overwrite: true);
                LogWarn($"A copy of the unreadable config was saved to '{configPath}.broken'.");
            }
            catch (Exception ex)
            {
                LogError($"Could not back up the unreadable config: {ex.Message}");
            }
        }

        /// <summary>Записывает конфиг и применяет фильтры. false, если файл записать не удалось (фильтры при этом всё равно применены)</summary>
        public static bool SaveConfig(List<FilterConfig> filters)
        {
            bool written = WriteConfig(filters);
            AudioMuterManager.SetFilters(filters);
            return written;
        }

        private static bool WriteConfig(List<FilterConfig> filters)
        {
            string configPath = "";

            try
            {
                configPath = GetConfigFilePath();
                var config = new PluginConfig { filters = filters, theme = ThemeDark ? "dark" : "light" };

                string json = JsonSerializer.Serialize(config, JsonContext.PluginConfig);
                AtomicFile.WriteAllText(configPath, json, Encoding.UTF8);

                LogInfo($"Saved config to file '{configPath}': {filters.Count} filter(s)");
                return true;
            }
            catch (Exception ex)
            {
                LogError($"Failed to save config to '{configPath}': {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Запоминает тему окна. Записывает те фильтры, что уже применены, и не перезапускает мониторинг
        /// (иначе при смене темы во время стрима приглушённые приложения возвращались бы к полной громкости)
        /// </summary>
        public static void SetTheme(bool dark)
        {
            ThemeDark = dark;
            WriteConfig(AudioMuterManager.GetFilters());
        }

        private static void OnMenuClicked(IntPtr privateData)
        {
            // второе окно настроек не открываем
            if (Interlocked.CompareExchange(ref _settingsOpen, 1, 0) != 0) return;

            var thread = new Thread(() =>
            {
                try
                {
                    // Исключение в обработчике события окна (таймер, щелчок) иначе открыло бы системное окно ошибки .NET
                    // поверх OBS, и так на каждый тик таймера. Вместо этого пишем в журнал, и окно продолжает работать
                    Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException, threadScope: true);
                    if (Interlocked.Exchange(ref _threadExceptionHooked, 1) == 0)
                    {
                        Application.ThreadException += (s, e) => LogError($"Unhandled exception in the settings window: {e.Exception}");
                    }

                    using var form = new SettingsForm(AudioMuterManager.GetFilters());
                    _settingsForm = form;
                    form.ShowDialog();
                }
                catch (Exception ex)
                {
                    LogError($"Settings window failed: {ex}");
                }
                finally
                {
                    _settingsForm = null;
                    Interlocked.Exchange(ref _settingsOpen, 0);
                }
            });
            thread.IsBackground = true; // окно не должно удерживать процесс OBS при закрытии
            thread.SetApartmentState(ApartmentState.STA);
            _settingsThread = thread;
            thread.Start();
        }

        private static int _threadExceptionHooked;
        private static volatile SettingsForm? _settingsForm;
        private static Thread? _settingsThread;

        /// <summary>
        /// Закрывает окно настроек, если оно открыто. Вызывается при выгрузке плагина: окно живёт в своём потоке
        /// и иначе продолжало бы работать с кодом, который OBS уже выгружает. Закрытие записывает несохранённые правки
        /// </summary>
        private static void CloseSettingsWindow()
        {
            var form = _settingsForm;
            var thread = _settingsThread;
            if (form == null || thread == null || !thread.IsAlive) return;

            try
            {
                if (form.IsHandleCreated) form.BeginInvoke(new Action(form.Close));
                thread.Join(3000);
            }
            catch (Exception ex)
            {
                LogError($"Failed to close the settings window: {ex.Message}");
            }
        }

        // Журнал растёт, пока плагин жив; при загрузке слишком большой файл откладывается в сторону
        private static void RotateLogIfLarge()
        {
            try
            {
                var info = new FileInfo(LogFilePath);
                if (info.Exists && info.Length > 5 * 1024 * 1024)
                {
                    lock (LogLock) File.Move(LogFilePath, LogFilePath + ".old", overwrite: true);
                }
            }
            catch
            {
                // журнал не критичен
            }
        }
    }
}
