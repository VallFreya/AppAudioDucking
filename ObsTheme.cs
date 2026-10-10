using System.Drawing;
using System.Globalization;

namespace ProcessMuterByChannel
{
    /// <summary>
    /// Две темы интерфейса, повторяющие палитры OBS Yami: тёмная (Yami) и светлая (Yami Light).
    /// Тему выбирает пользователь в окне настроек, выбор хранится в config.json.
    /// </summary>
    internal static class ObsTheme
    {
        public const string FontFamily = "Segoe UI";

        public static bool IsDark { get; private set; } = true;
        public static float FontPt { get; private set; } = 10f;

        public static Color WindowBg, BaseBg, Text, TextMuted;
        public static Color InputBg, InputBgHover, InputBgFocus, InputBorderHover, InputBorderFocus;
        public static Color ButtonBg, ButtonBgHover, ButtonBgDown, ButtonBorderHover;
        public static Color Primary, PrimaryLight, ScrollbarHandle;

        private static Color Rgb(int value) => Color.FromArgb((value >> 16) & 0xFF, (value >> 8) & 0xFF, value & 0xFF);

        public static void Apply(bool dark)
        {
            IsDark = dark;
            FontPt = ReadObsFontScale();

            if (dark)
            {
                WindowBg = Rgb(0x1D1F26);
                BaseBg = Rgb(0x272A33);
                Text = Rgb(0xFFFFFF);
                TextMuted = Rgb(0x969696);
                InputBg = Rgb(0x3C404D);
                InputBgHover = Rgb(0x1D1F26);
                InputBgFocus = Rgb(0x1D1F26);
                InputBorderHover = Rgb(0x5B6273);
                InputBorderFocus = Rgb(0x284CB8);
                ButtonBg = Rgb(0x3C404D);
                ButtonBgHover = Rgb(0x464B59);
                ButtonBgDown = Rgb(0x1D1F26);
                ButtonBorderHover = Rgb(0x5B6273);
                Primary = Rgb(0x284CB8);
                PrimaryLight = Rgb(0x476BD7);
                ScrollbarHandle = Rgb(0x3C404D);
            }
            else
            {
                WindowBg = Rgb(0xD3D3D3);
                BaseBg = Rgb(0xE5E5E5);
                Text = Rgb(0x0A0A0A);
                TextMuted = Rgb(0x646464);
                InputBg = Rgb(0xF5F5F5);
                InputBgHover = Rgb(0xFEFEFE);
                InputBgFocus = Rgb(0xFEFEFE);
                InputBorderHover = Rgb(0x828282);
                InputBorderFocus = Rgb(0x8CB5FF);
                ButtonBg = Rgb(0xF5F5F5);
                ButtonBgHover = Rgb(0xFEFEFE);
                ButtonBgDown = Rgb(0xD3D3D3);
                ButtonBorderHover = Rgb(0x828282);
                Primary = Rgb(0x8CB5FF);
                PrimaryLight = Rgb(0xB2CFFF);
                ScrollbarHandle = Rgb(0xFEFEFE);
            }
        }

        // Размер текста берём из настройки «Масштаб шрифта» в OBS (user.ini), по умолчанию 10
        private static float ReadObsFontScale()
        {
            try
            {
                string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "obs-studio", "user.ini");
                if (!File.Exists(path)) return 10f;

                foreach (var line in File.ReadAllLines(path))
                {
                    if (line.StartsWith("FontScale=", StringComparison.OrdinalIgnoreCase)
                        && int.TryParse(line.Substring("FontScale=".Length).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int scale))
                    {
                        return Math.Clamp(scale, 8, 16);
                    }
                }
            }
            catch
            {
                // остаётся размер по умолчанию
            }

            return 10f;
        }
    }
}
