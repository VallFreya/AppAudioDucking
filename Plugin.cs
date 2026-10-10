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

        private static void WriteLog(string level, string message)
        {
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

            try
            {
                LoadConfig();
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
            AudioMuterManager.Stop();
        }

        private static string GetConfigFilePath()
        {
            if (ModuleContext == IntPtr.Zero) return "process_muter_config.json";
            IntPtr pathPtr = obs_module_get_config_path(ModuleContext, "config.json");

            string path = Marshal.PtrToStringUTF8(pathPtr) ?? "process_muter_config.json";

            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            return path;
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

                var config = JsonSerializer.Deserialize(json, PluginConfigJsonContext.Default.PluginConfig);

                if (config == null)
                {
                    LogWarn($"Failed to parse config JSON at '{configPath}'.");
                    return;
                }

                var filters = config.filters ?? new List<FilterConfig>();

                // конфиг старого формата (один фильтр без списка) превращаем в фильтр
                if (filters.Count == 0)
                {
                    var legacy = config.MigrateLegacy();
                    if (legacy != null)
                    {
                        filters.Add(legacy);
                        LogInfo("Migrated legacy config to a filter.");
                    }
                }

                ThemeDark = !string.Equals(config.theme, "light", StringComparison.OrdinalIgnoreCase);

                LogInfo($"Loaded config from '{configPath}': {filters.Count} filter(s)");

                AudioMuterManager.SetFilters(filters);
            }
            catch (Exception ex)
            {
                LogError($"Error reading config file: {ex.Message}");
            }
        }

        public static void SaveConfig(List<FilterConfig> filters)
        {
            WriteConfig(filters);
            AudioMuterManager.SetFilters(filters);
        }

        private static void WriteConfig(List<FilterConfig> filters)
        {
            string configPath = GetConfigFilePath();

            try
            {
                var config = new PluginConfig { filters = filters, theme = ThemeDark ? "dark" : "light" };

                string json = JsonSerializer.Serialize(config, JsonContext.PluginConfig);
                File.WriteAllText(configPath, json, Encoding.UTF8);

                LogInfo($"Saved config to file '{configPath}': {filters.Count} filter(s)");
            }
            catch (Exception ex)
            {
                LogError($"Failed to save config to '{configPath}': {ex.Message}");
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
                    using var form = new SettingsForm(AudioMuterManager.GetFilters());
                    form.ShowDialog();
                }
                catch (Exception ex)
                {
                    LogError($"Settings window failed: {ex}");
                }
                finally
                {
                    Interlocked.Exchange(ref _settingsOpen, 0);
                }
            });
            thread.IsBackground = true; // окно не должно удерживать процесс OBS при закрытии
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }
    }
}
