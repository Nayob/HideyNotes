using System;
using System.IO;

namespace HideyNotes.Services
{
    /// <summary>Filesystem locations used by the app.</summary>
    public static class AppPaths
    {
        public static string DataDir { get; private set; } = ResolveDataDir();

        /// <summary>Why the primary data folder was not used (null when it was).</summary>
        public static string? FallbackReason { get; private set; }

        private static string ResolveDataDir()
        {
            var overridden = Environment.GetEnvironmentVariable("HIDEYNOTES_DATA_DIR");
            if (!string.IsNullOrWhiteSpace(overridden)) return overridden;

            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HideyNotes");
        }

        public static string SettingsFile => Path.Combine(DataDir, "settings.json");
        public static string IndexFile => Path.Combine(DataDir, "index.json");
        public static string ScratchFile => Path.Combine(DataDir, "scratch.txt");
        public static string LogFile => Path.Combine(DataDir, "log.txt");
        public static string NotesDir => Path.Combine(DataDir, "notes");
        public static string TrashDir => Path.Combine(DataDir, "trash");

        /// <summary>Extra interface languages: any *.json dropped here appears in the settings.</summary>
        public static string LangDir => Path.Combine(DataDir, "lang");

        /// <summary>
        /// Picks the first writable data folder. Some environments (locked-down profiles,
        /// sandboxes) deny %LOCALAPPDATA% and even %TEMP%, so the exe folder is tried too.
        /// </summary>
        public static void EnsureCreated()
        {
            MigrateLegacyFolders();

            var primary = DataDir;
            var primaryFailure = TryCreate(primary);
            if (primaryFailure is null) return;

            foreach (var candidate in new[]
                     {
                         Path.Combine(AppContext.BaseDirectory, "HideyNotesData"),
                         Path.Combine(Path.GetTempPath(), "HideyNotes")
                     })
            {
                if (string.Equals(candidate, primary, StringComparison.OrdinalIgnoreCase)) continue;

                var failure = TryCreate(candidate);
                if (failure is not null) continue;

                DataDir = candidate;
                FallbackReason = $"cannot use {primary}: {primaryFailure.Message}";
                CrashLog.Write(new InvalidOperationException(
                    $"data folder {candidate} selected, {FallbackReason}", primaryFailure));
                return;
            }

            throw new IOException($"Не удалось создать папку для данных: {primary}", primaryFailure);
        }

        /// <summary>The app used to be called SlideNotes; take the old folders along.</summary>
        private static void MigrateLegacyFolders()
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            TryMove(Path.Combine(localAppData, "SlideNotes"), Path.Combine(localAppData, "HideyNotes"));
            TryMove(Path.Combine(AppContext.BaseDirectory, "SlideNotesData"), Path.Combine(AppContext.BaseDirectory, "HideyNotesData"));
        }

        private static void TryMove(string from, string to)
        {
            try
            {
                if (Directory.Exists(from) && !Directory.Exists(to)) Directory.Move(from, to);
            }
            catch
            {
                // best effort: a failed migration just means a fresh start
            }
        }

        private static Exception? TryCreate(string root)
        {
            try
            {
                Directory.CreateDirectory(root);
                Directory.CreateDirectory(Path.Combine(root, "notes"));
                Directory.CreateDirectory(Path.Combine(root, "trash"));

                // The folders may exist and still be read-only for us: prove we can write.
                var probe = Path.Combine(root, ".write-test");
                File.WriteAllText(probe, "ok");
                File.Delete(probe);

                Directory.CreateDirectory(Path.Combine(root, "lang"));
                return null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        }

        /// <summary>Writes the file atomically: temp file + replace, so a crash never leaves a half-written file.</summary>
        public static void WriteAtomic(string path, string content)
        {
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, content);
            File.Move(tmp, path, true);
        }
    }
}
