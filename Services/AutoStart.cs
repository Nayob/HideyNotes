using System;
using Microsoft.Win32;

namespace HideyNotes.Services
{
    /// <summary>Autostart through HKCU\...\Run (no admin rights needed).</summary>
    public static class AutoStart
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "HideyNotes";

        public static bool IsEnabled()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(ValueName) is string value && value.Length > 0;
            }
            catch (Exception ex)
            {
                Log.Error("autostart read failed", ex);
                return false;
            }
        }

        /// <summary>(Re)writes the Run entry so it always points at the current exe location.</summary>
        public static void Apply(string exePath)
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(RunKey);
                key?.SetValue(ValueName, $"\"{exePath}\"");
            }
            catch (Exception ex)
            {
                Log.Error("autostart write failed", ex);
            }
        }

        public static void Remove()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
                key?.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            catch (Exception ex)
            {
                Log.Error("autostart remove failed", ex);
            }
        }
    }
}
