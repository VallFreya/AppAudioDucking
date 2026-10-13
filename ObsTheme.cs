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

        // Приглушённые красный и зелёный для кнопок «удалить» и «добавить»: тон лёгкий, чтобы кнопки не кричали
        // Рамка карточки, видимое кольцо фокуса и плашка состояния фильтра
        public static Color CardBorder, FocusRing, ChipBg, ChipText, ChipActiveBg, ChipActiveText;
        public static Color DangerBg, DangerBgHover, DangerBorder, DangerAccent;
        public static Color SuccessBg, SuccessBgHover, SuccessBorder, SuccessAccent;

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
                TextMuted = Rgb(0xA8A8A8);
                CardBorder = Rgb(0x343846);
                FocusRing = Rgb(0x7C9BFF);
                ChipBg = Rgb(0x3C404D);
                ChipText = Rgb(0xC4C4C4);
                ChipActiveBg = Rgb(0x284CB8);
                ChipActiveText = Rgb(0xFFFFFF);
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
                DangerBg = Rgb(0x4B2F37);
                DangerBgHover = Rgb(0x5E3841);
                DangerBorder = Rgb(0x9A5560);
                DangerAccent = Rgb(0xE8929C);
                SuccessBg = Rgb(0x2F4A3D);
                SuccessBgHover = Rgb(0x395A4A);
                SuccessBorder = Rgb(0x57906F);
                SuccessAccent = Rgb(0x8CD1A6);
            }
            else
            {
                WindowBg = Rgb(0xCDCDCD);
                BaseBg = Rgb(0xE8E8E8);
                Text = Rgb(0x0A0A0A);
                TextMuted = Rgb(0x575757);
                CardBorder = Rgb(0xAEAEAE);
                FocusRing = Rgb(0x1F4FD8);
                ChipBg = Rgb(0xD6D6D6);
                ChipText = Rgb(0x3A3A3A);
                ChipActiveBg = Rgb(0x8CB5FF);
                ChipActiveText = Rgb(0x0A0A0A);
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
                DangerBg = Rgb(0xF3DADA);
                DangerBgHover = Rgb(0xF8E6E6);
                DangerBorder = Rgb(0xCB8F8F);
                DangerAccent = Rgb(0xB03A48);
                SuccessBg = Rgb(0xD8EBDF);
                SuccessBgHover = Rgb(0xE4F3E9);
                SuccessBorder = Rgb(0x8DBB9E);
                SuccessAccent = Rgb(0x2E7D4F);
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
