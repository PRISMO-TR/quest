using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Microsoft.Win32;

namespace Hadaf
{
    /// <summary>
    /// Glass backdrop + colour tokens. Text sits on a tinted layer whose minimum opacity is
    /// fixed, so it stays readable over any wallpaper or window behind it.
    /// </summary>
    public static class Theme
    {
        public static bool IsDark { get; private set; } = true;
        public static bool GlassSupported => Environment.OSVersion.Version.Build >= 22621;

        const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        const int DWMWA_SYSTEMBACKDROP_TYPE = 38;

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        [DllImport("user32.dll")]
        static extern int GetWindowLong(IntPtr hwnd, int index);
        [DllImport("user32.dll")]
        static extern int SetWindowLong(IntPtr hwnd, int index, int value);

        /// <summary>
        /// With the frame extended over the whole window, DWM still paints its own
        /// min/max/close glyphs under ours. Dropping WS_SYSMENU stops that.
        /// </summary>
        public static void HideNativeCaptionButtons(Window w)
        {
            const int GWL_STYLE = -16, WS_SYSMENU = 0x00080000;
            var hwnd = new WindowInteropHelper(w).Handle;
            if (hwnd != IntPtr.Zero)
                SetWindowLong(hwnd, GWL_STYLE, GetWindowLong(hwnd, GWL_STYLE) & ~WS_SYSMENU);
        }

        public static void Apply(Window w, Settings s)
        {
            IsDark = s.Theme == "dark" || (s.Theme == "system" && SystemIsDark());
            bool glass = GlassSupported && s.Effect != "solid";
            double t = glass ? Math.Clamp(s.Transparency, 0, 1) : 0;
            var R = Application.Current.Resources;

            // Layers, bottom to top: backdrop → BgLayer (tint that guarantees contrast) → cards → controls.
            uint baseRgb = IsDark ? 0x17171Bu : 0xF3F3F6u;
            if (IsDark)
            {
                R["BgLayer"] = glass ? Alpha(0.86 - 0.56 * t, baseRgb) : B(0xFF000000 | baseRgb);
                R["Card"] = glass ? B(0x12FFFFFF) : B(0xFF232328);
                R["CardHover"] = glass ? B(0x1CFFFFFF) : B(0xFF2A2A30);
                R["CardStroke"] = Edge(0x17FFFFFF, 0x0AFFFFFF);
                R["ControlFill"] = glass ? B(0x0FFFFFFF) : B(0xFF2A2A2F);
                R["ControlStrong"] = glass ? B(0x29FFFFFF) : B(0xFF3A3A41);
                R["ControlStroke"] = Edge(0x17FFFFFF, 0x0DFFFFFF);
                R["Track"] = B(0x33000000);
                R["InputBg"] = glass ? B(0x0DFFFFFF) : B(0xFF202024);
                R["InputHover"] = glass ? B(0x14FFFFFF) : B(0xFF26262B);
                R["InputFocus"] = glass ? B(0x40000000) : B(0xFF1B1B1F);
                R["InputStroke"] = Edge(0x12FFFFFF, 0x45FFFFFF);
                R["Divider"] = B(0x14FFFFFF);
                R["Hover"] = B(0x0FFFFFFF);
                R["Pressed"] = B(0x08FFFFFF);
                R["Selected"] = B(0x17FFFFFF);
                R["Text"] = B(0xFFF4F4F6);
                R["SubText"] = B(0xFFBFC1CA);
                R["Faint"] = B(0xFF8B8E9A);
                R["Surface"] = B(0xFF26262B);
                R["Scrim"] = B(0x59000000);
                R["Danger"] = B(0xFFF87171);
                R["DangerSoft"] = B(0x26F87171);
                R["DangerStroke"] = B(0x47F87171);
                R["Warn"] = B(0xFFFBBF24);
                R["WarnSoft"] = B(0x24FBBF24);
                R["WarnStroke"] = B(0x47FBBF24);
            }
            else
            {
                R["BgLayer"] = glass ? Alpha(0.82 - 0.50 * t, baseRgb) : B(0xFF000000 | baseRgb);
                R["Card"] = glass ? B(0xB8FFFFFF) : B(0xFFFFFFFF);
                R["CardHover"] = glass ? B(0xE6FFFFFF) : B(0xFFFAFAFB);
                R["CardStroke"] = Edge(0x0F000000, 0x1F000000);
                R["ControlFill"] = glass ? B(0xB3FFFFFF) : B(0xFFFFFFFF);
                R["ControlStrong"] = B(0xFFFFFFFF);
                R["ControlStroke"] = Edge(0x0F000000, 0x29000000);
                R["Track"] = B(0x0D000000);
                R["InputBg"] = glass ? B(0xB3FFFFFF) : B(0xFFFFFFFF);
                R["InputHover"] = glass ? B(0xD9FFFFFF) : B(0xFFFAFAFB);
                R["InputFocus"] = B(0xFFFFFFFF);
                R["InputStroke"] = Edge(0x0F000000, 0x6B000000);
                R["Divider"] = B(0x14000000);
                R["Hover"] = B(0x0A000000);
                R["Pressed"] = B(0x06000000);
                R["Selected"] = B(0x0F000000);
                R["Text"] = B(0xFF1B1B1F);
                R["SubText"] = B(0xFF5C5F6B);
                R["Faint"] = B(0xFF8D909C);
                R["Surface"] = B(0xFFFCFCFD);
                R["Scrim"] = B(0x33000000);
                R["Danger"] = B(0xFFDC2626);
                R["DangerSoft"] = B(0x1FDC2626);
                R["DangerStroke"] = B(0x47DC2626);
                R["Warn"] = B(0xFFB45309);
                R["WarnSoft"] = B(0x1FF59E0B);
                R["WarnStroke"] = B(0x52D97706);
            }
            // Real shadows only for floating layers (menu, snackbar); cards rely on their edge.
            R["ShadowLg"] = Shadow(32, 8, IsDark ? 0.5 : 0.18);
            R["ShadowSm"] = Shadow(12, 2, IsDark ? 0.4 : 0.14);

            var accent = AdjustAccent(SystemAccent(), IsDark);
            uint a = (uint)(accent.R << 16 | accent.G << 8 | accent.B);
            R["Accent"] = B(0xFF000000 | a);
            R["AccentSoft"] = B(0x2E000000 | a);
            // Pick whichever text colour has the higher contrast ratio against the accent.
            double la = Luminance(accent);
            R["AccentText"] = (la + 0.05) / 0.05 > 1.05 / (la + 0.05) ? B(0xFF14161F) : B(0xFFFFFFFF);

            var hwnd = new WindowInteropHelper(w).Handle;
            if (hwnd != IntPtr.Zero)
            {
                int dark = IsDark ? 1 : 0;
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
                if (GlassSupported)
                {
                    int backdrop = !glass ? 1 : s.Effect == "mica" ? 2 : 3;
                    DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));
                }
                var src = HwndSource.FromHwnd(hwnd);
                if (src?.CompositionTarget != null)
                    src.CompositionTarget.BackgroundColor = glass ? Colors.Transparent : ToColor(0xFF000000 | baseRgb);
            }
            w.Background = glass ? Brushes.Transparent : B(0xFF000000 | baseRgb);
        }

        /// <summary>
        /// Hairline whose bottom edge uses a second tone (the Fluent elevation trick): a lit rim
        /// in dark mode, a soft drop in light mode. Depth without the cost of a blur.
        /// </summary>
        static Brush Edge(uint top, uint bottom)
        {
            var b = new LinearGradientBrush
            {
                MappingMode = BrushMappingMode.Absolute,
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0, 3),
                RelativeTransform = new ScaleTransform(1, -1, 0.5, 0.5),
            };
            b.GradientStops.Add(new GradientStop(ToColor(bottom), 0.33));
            b.GradientStops.Add(new GradientStop(ToColor(top), 1));
            b.Freeze();
            return b;
        }

        static Effect Shadow(double blur, double depth, double opacity)
        {
            var e = new DropShadowEffect
            {
                BlurRadius = blur, ShadowDepth = depth, Direction = 270, Opacity = opacity,
                Color = Colors.Black, RenderingBias = RenderingBias.Performance,
            };
            e.Freeze();
            return e;
        }

        static SolidColorBrush B(uint argb) => Palette.B(argb);
        static SolidColorBrush Alpha(double alpha, uint rgb) =>
            Palette.B((uint)Math.Round(Math.Clamp(alpha, 0, 1) * 255) << 24 | rgb);
        static Color ToColor(uint argb) =>
            Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

        public static bool SystemIsDark()
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                return k?.GetValue("AppsUseLightTheme") is int v ? v == 0 : true;
            }
            catch { return true; }
        }

        static Color SystemAccent()
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
                if (k?.GetValue("AccentColor") is int v) // stored as ABGR
                    return Color.FromRgb((byte)(v & 0xFF), (byte)((v >> 8) & 0xFF), (byte)((v >> 16) & 0xFF));
            }
            catch { }
            return Color.FromRgb(0x8B, 0x5C, 0xF6);
        }

        static double Lin(byte c) { double x = c / 255.0; return x <= 0.03928 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4); }
        static double Luminance(Color c) => 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);

        /// <summary>Keeps the Windows accent colour legible against the current theme.</summary>
        static Color AdjustAccent(Color c, bool dark)
        {
            double l = Luminance(c);
            if (dark && l < 0.28) return Mix(c, Colors.White, Math.Min(0.6, (0.28 - l) * 2.2 + 0.15));
            if (!dark && l > 0.35) return Mix(c, Colors.Black, Math.Min(0.6, (l - 0.35) * 1.6 + 0.1));
            return c;
        }

        static Color Mix(Color a, Color b, double k) => Color.FromRgb(
            (byte)(a.R + (b.R - a.R) * k), (byte)(a.G + (b.G - a.G) * k), (byte)(a.B + (b.B - a.B) * k));
    }

    public static class Native
    {
        [DllImport("psapi.dll")]
        static extern bool EmptyWorkingSet(IntPtr process);

        /// <summary>Releases unused memory while the app sits in the tray.</summary>
        public static void Trim()
        {
            try
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                using var p = Process.GetCurrentProcess();
                EmptyWorkingSet(p.Handle);
            }
            catch { }
        }
    }

    public static class AutoStart
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        public static bool IsEnabled()
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue("Hadaf") is string;
        }

        static string Command => $"\"{Environment.ProcessPath}\" --tray";

        public static void Set(bool on)
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            if (on) k.SetValue("Hadaf", Command);
            else k.DeleteValue("Hadaf", false);
        }

        /// <summary>If start-with-Windows is on but points at an old location of the app, point it here.</summary>
        public static void Repair()
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(RunKey, true);
                if (k?.GetValue("Hadaf") is string current && !string.Equals(current, Command, StringComparison.OrdinalIgnoreCase))
                    k.SetValue("Hadaf", Command);
            }
            catch { }
        }
    }
}
