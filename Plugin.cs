using System;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Forms;

namespace ProcessMuterByChannel
{
    // Класс-модель для JSON-конфига
    public class PluginConfig
    {
        public string process_name { get; set; } = "";
        public string source_name { get; set; } = "";
        public string threshold_db { get; set; } = "-60";
        public string fade_ms { get; set; } = "600";
    }

    // Source Generator для NativeAOT (убирает требование рефлексии)
    [JsonSourceGenerationOptions(WriteIndented = true)]
    [JsonSerializable(typeof(PluginConfig))]
    internal partial class PluginConfigJsonContext : JsonSerializerContext;

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

        // Опции сериализации с поддержкой NativeAOT и сохранения кириллицы
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            TypeInfoResolver = PluginConfigJsonContext.Default
        };

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
            [MarshalAs(UnmanagedType.LPStr)] string name,
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
            return (char*)Marshal.StringToHGlobalAnsi("ProcessMuterByChannel");
        }

        [UnmanagedCallersOnly(EntryPoint = "obs_module_load", CallConvs = new[] { typeof(CallConvCdecl) })]
        public static bool ObsModuleLoad()
        {
            LogInfo("===========================================");
            LogInfo("=== Plugin ObsModuleLoad Executing ===");
            LogInfo($"Log File Path: {LogFilePath}");

            try
            {
                obs_frontend_add_tools_menu_item("ProcessMuterByChannel", MenuCallbackDelegate, IntPtr.Zero);
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

        public static void LoadConfig()
        {
            string configPath = GetConfigFilePath();
            if (!File.Exists(configPath))
            {
                LogWarn($"Config file not found at '{configPath}'. Using defaults.");
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

                string proc = config.process_name ?? "";
                string src = config.source_name ?? "";
                string threshStr = config.threshold_db ?? "-60";
                string fadeStr = config.fade_ms ?? "600";

                float.TryParse(threshStr.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out float threshDb);
                int.TryParse(fadeStr, out int fadeMs);

                if (threshDb == 0) threshDb = -60.0f;
                if (fadeMs <= 0) fadeMs = 600;

                LogInfo($"Successfully Loaded Config from file -> Process: '{proc}', Source: '{src}', Threshold: {threshDb}dB, Fade: {fadeMs}ms");

                AudioMuterManager.UpdateSettings(proc, src, threshDb, fadeMs);
            }
            catch (Exception ex)
            {
                LogError($"Error reading config file: {ex.Message}");
            }
        }

        public static void SaveConfig(string processName, string sourceName, float thresholdDb, int fadeMs)
        {
            string configPath = GetConfigFilePath();

            try
            {
                var config = new PluginConfig
                {
                    process_name = processName,
                    source_name = sourceName,
                    threshold_db = thresholdDb.ToString(CultureInfo.InvariantCulture),
                    fade_ms = fadeMs.ToString()
                };

                string json = JsonSerializer.Serialize(config, JsonOptions);
                File.WriteAllText(configPath, json, Encoding.UTF8);

                LogInfo($"Saved Config to file '{configPath}' -> Process: '{processName}', Source: '{sourceName}', Threshold: {thresholdDb}dB, Fade: {fadeMs}ms");
            }
            catch (Exception ex)
            {
                LogError($"Failed to save config to '{configPath}': {ex.Message}");
            }

            AudioMuterManager.UpdateSettings(processName, sourceName, thresholdDb, fadeMs);
        }

        private static void OnMenuClicked(IntPtr privateData)
        {
            var thread = new System.Threading.Thread(() =>
            {
                ShowNativeSettingsForm();
            });
            thread.SetApartmentState(System.Threading.ApartmentState.STA);
            thread.Start();
        }

        private static void ShowNativeSettingsForm()
        {
            using (var form = new Form())
            {
                form.Text = "ProcessMuterByChannel Settings";
                form.Width = 440;
                form.Height = 270;
                form.FormBorderStyle = FormBorderStyle.FixedDialog;
                form.MaximizeBox = false;
                form.MinimizeBox = false;
                form.StartPosition = FormStartPosition.CenterScreen;

                var lblProc = new Label() { Left = 20, Top = 20, Text = "Process Name (without .exe):", Width = 210 };
                var txtProc = new TextBox() { Left = 240, Top = 18, Text = AudioMuterManager.TargetProcessName, Width = 160 };

                var lblSrc = new Label() { Left = 20, Top = 60, Text = "Monitored Audio Source:", Width = 210 };
                var txtSrc = new TextBox() { Left = 240, Top = 58, Text = AudioMuterManager.TargetAudioSource, Width = 160 };

                var lblThresh = new Label() { Text = "Threshold Level:", Left = 20, Top = 100, Width = 210 };
                var txtThresh = new TextBox() { Left = 240, Top = 98, Text = AudioMuterManager.ThresholdDb.ToString(CultureInfo.InvariantCulture), Width = 120 };
                var lblDb = new Label() { Left = 365, Top = 100, Text = "dB", Width = 35 };

                var lblFade = new Label() { Left = 20, Top = 140, Text = "Fade Duration:", Width = 210 };
                var txtFade = new TextBox() { Left = 240, Top = 138, Text = AudioMuterManager.FadeDurationMs.ToString(), Width = 120 };
                var lblMs = new Label() { Left = 365, Top = 140, Text = "ms", Width = 35 };

                var btnSave = new Button() { Text = "Save", Left = 240, Top = 185, Width = 100 };

                btnSave.Click += (s, e) =>
                {
                    string proc = txtProc.Text.Trim();
                    string src = txtSrc.Text.Trim();

                    if (!float.TryParse(txtThresh.Text.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out float thresh))
                    {
                        thresh = -60.0f;
                    }

                    if (!int.TryParse(txtFade.Text.Trim(), out int fadeMs))
                    {
                        fadeMs = 600;
                    }

                    SaveConfig(proc, src, thresh, fadeMs);

                    MessageBox.Show("Settings saved successfully!", "ProcessMuterByChannel", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    form.Close();
                };

                form.Controls.Add(lblProc);
                form.Controls.Add(txtProc);
                form.Controls.Add(lblSrc);
                form.Controls.Add(txtSrc);
                form.Controls.Add(lblThresh);
                form.Controls.Add(txtThresh);
                form.Controls.Add(lblDb);
                form.Controls.Add(lblFade);
                form.Controls.Add(txtFade);
                form.Controls.Add(lblMs);
                form.Controls.Add(btnSave);

                form.ShowDialog();
            }
        }
    }
}