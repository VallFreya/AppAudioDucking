using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace ProcessMuterByChannel
{
    /// <summary>Общие помощники для рисования интерфейса в стиле OBS</summary>
    internal static class Ui
    {
        // Все размеры заданы для 96 dpi и умножаются на K
        public static float K = 1f;

        public static int Px(float value) => (int)Math.Round(value * K);

        public static Font Font(FontStyle style = FontStyle.Regular, float scale = 1f)
        {
            // размер в пикселях, чтобы не зависеть от того, какой dpi выберет GDI для пунктов
            return new Font(ObsTheme.FontFamily, ObsTheme.FontPt * 96f / 72f * K * scale, style, GraphicsUnit.Pixel);
        }

        public static GraphicsPath RoundedRect(RectangleF rect, float radius)
        {
            var path = new GraphicsPath();
            float d = Math.Max(0.01f, Math.Min(radius * 2, Math.Min(rect.Width, rect.Height)));

            path.AddArc(rect.X, rect.Y, d, d, 180, 90);
            path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        // --- Значки: системный шрифт значков Windows, а если его нет, обычные символы ---

        private static string? _iconFontName;

        public static string IconFontName
        {
            get
            {
                if (_iconFontName != null) return _iconFontName;

                _iconFontName = "Segoe UI Symbol";
                try
                {
                    using var fonts = new InstalledFontCollection();
                    foreach (var family in fonts.Families)
                    {
                        if (family.Name == "Segoe Fluent Icons") { _iconFontName = family.Name; break; }
                        if (family.Name == "Segoe MDL2 Assets") _iconFontName = family.Name;
                    }
                }
                catch
                {
                    // остаётся запасной шрифт
                }
                return _iconFontName;
            }
        }

        private static bool HasIconFont => IconFontName != "Segoe UI Symbol";

        public static string GlyphAdd => HasIconFont ? "" : "+";
        public static string GlyphRefresh => HasIconFont ? "" : "↻";
        public static string GlyphDelete => HasIconFont ? "" : "✕";

        public static string GlyphCopy => HasIconFont ? "" : "⧉";
        public static string GlyphEdit => HasIconFont ? "" : "✎";

        public static Font IconFont(float scale = 1f)
        {
            return new Font(IconFontName, ObsTheme.FontPt * 96f / 72f * K * scale, FontStyle.Regular, GraphicsUnit.Pixel);
        }

        // --- Оформление окна ---

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        private static extern int SetWindowTheme(IntPtr hwnd, string subAppName, string? subIdList);

        private static int ToColorRef(Color c) => c.R | (c.G << 8) | (c.B << 16);

        /// <summary>Заголовок окна в цвет темы OBS (на Windows 10 меняется только светлый/тёмный режим)</summary>
        public static void ApplyWindowChrome(IntPtr hwnd)
        {
            try
            {
                int dark = ObsTheme.IsDark ? 1 : 0;
                DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int)); // DWMWA_USE_IMMERSIVE_DARK_MODE

                int caption = ToColorRef(ObsTheme.WindowBg);
                DwmSetWindowAttribute(hwnd, 35, ref caption, sizeof(int)); // DWMWA_CAPTION_COLOR (Windows 11)

                int text = ToColorRef(ObsTheme.Text);
                DwmSetWindowAttribute(hwnd, 36, ref text, sizeof(int)); // DWMWA_TEXT_COLOR
            }
            catch
            {
                // оформление не критично
            }
        }

        /// <summary>Полосы прокрутки в тёмной теме</summary>
        public static void ApplyScrollbarTheme(IntPtr hwnd)
        {
            if (!ObsTheme.IsDark) return;

            try
            {
                SetWindowTheme(hwnd, "DarkMode_Explorer", null);
            }
            catch
            {
                // останутся системные полосы
            }
        }
    }
}
