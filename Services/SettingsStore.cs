using System;
using System.IO;
using System.Text.Json;

namespace HideyNotes.Services
{
    public static class SettingsStore
    {
        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions { WriteIndented = true };

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(AppPaths.SettingsFile))
                {
                    var json = File.ReadAllText(AppPaths.SettingsFile);
                    var loaded = JsonSerializer.Deserialize<AppSettings>(json, Options);
                    if (loaded is not null)
                    {
                        Migrate(loaded);
                        return loaded;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("settings load failed", ex);
            }

            return new AppSettings();
        }

        /// <summary>Fixes values written by older versions.</summary>
        private static void Migrate(AppSettings settings)
        {
            // Ctrl+Alt+Space was the old default; the panel shortcut is Ctrl+Shift+Space now.
            if (string.Equals(settings.Hotkey, "Ctrl+Alt+Space", StringComparison.OrdinalIgnoreCase))
            {
                settings.Hotkey = AppSettings.DefaultHotkey;
            }

            // Older versions stored "the monitor it happened to start on" instead of a choice:
            // go back to auto so the panel is not pinned to a stale screen.
            if (settings.Schema < 2)
            {
                settings.MonitorDevice = "";
                settings.Schema = 2;
            }

            // Schema 3 added the interface language; schema 4 made English the default one.
            if (settings.Schema < 4)
            {
                if (string.IsNullOrWhiteSpace(settings.Language)) settings.Language = AppSettings.EnglishCode;
                settings.Schema = 4;
            }

            // Schema 5 dropped "never hide": the pin in the header does the same per session, so a
            // file written by an older version would otherwise keep a delay the settings cannot show.
            if (settings.Schema < 5)
            {
                if (settings.HideDelayMs < 0) settings.HideDelayMs = 300;
                settings.Schema = 5;
            }
        }

        public static void Save(AppSettings settings)
        {
            try
            {
                AppPaths.WriteAtomic(AppPaths.SettingsFile, JsonSerializer.Serialize(settings, Options));
            }
            catch (Exception ex)
            {
                Log.Error("settings save failed", ex);
            }
        }
    }
}
