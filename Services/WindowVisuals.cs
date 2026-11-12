using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace HideyNotes.Services
{
    internal static class WindowVisuals
    {
        /// <summary>Removes the window from Alt+Tab and the taskbar.</summary>
        public static void MakeToolWindow(IntPtr hwnd)
        {
            try
            {
                var style = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
                Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, new IntPtr(style | Native.WS_EX_TOOLWINDOW));
            }
            catch (Exception ex)
            {
                Log.Error("MakeToolWindow", ex);
            }
        }

        /// <summary>Rounded corners on Windows 11 (ignored on Windows 10).</summary>
        public static void SetRoundedCorners(IntPtr hwnd)
        {
            if (Environment.OSVersion.Version.Build < 22000) return;
            try
            {
                var preference = Native.DWMWCP_ROUND;
                Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
            }
            catch (Exception ex)
            {
                Log.Error("SetRoundedCorners", ex);
            }
        }

        public static void SetDarkMode(IntPtr hwnd, bool dark)
        {
            if (hwnd == IntPtr.Zero) return;
            try
            {
                var value = dark ? 1 : 0;
                Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int));
            }
            catch (Exception ex)
            {
                Log.Error("SetDarkMode", ex);
            }
        }

        /// <summary>Windows 11 Mica backdrop. Returns false on Windows 10 / when disabled.</summary>
        public static bool TryEnableBackdrop(IntPtr hwnd, bool dark)
        {
            if (hwnd == IntPtr.Zero) return false;
            if (Environment.GetEnvironmentVariable("HIDEYNOTES_NO_MICA") == "1") return false;
            if (Environment.OSVersion.Version.Build < 22621) return false;

            try
            {
                var margins = new Native.MARGINS
                {
                    cxLeftWidth = -1,
                    cxRightWidth = -1,
                    cyTopHeight = -1,
                    cyBottomHeight = -1
                };
                var hrExtend = Native.DwmExtendFrameIntoClientArea(hwnd, ref margins);

                var backdrop = Native.DWMSBT_MAINWINDOW;
                var hrBackdrop = Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));

                if (hrBackdrop != 0)
                {
                    Log.Info($"Mica unavailable: hrBackdrop=0x{hrBackdrop:X8}, hrExtend=0x{hrExtend:X8}");
                    return false;
                }

                var source = HwndSource.FromHwnd(hwnd);
                if (source?.CompositionTarget is not null)
                {
                    source.CompositionTarget.BackgroundColor = Colors.Transparent;
                }

                SetDarkMode(hwnd, dark);
                Log.Info("Mica enabled");
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("TryEnableBackdrop", ex);
                return false;
            }
        }
    }
}
