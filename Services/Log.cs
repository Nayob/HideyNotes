using System;
using System.IO;

namespace HideyNotes.Services
{
    /// <summary>
    /// Errors only, by design: a log nobody asked for is noise, and the app is not under
    /// development. Set <c>HIDEYNOTES_LOG=1</c> to get the full diagnostic log back.
    /// </summary>
    public static class Log
    {
        private static readonly object Gate = new object();

        private static readonly bool Verbose =
            Environment.GetEnvironmentVariable("HIDEYNOTES_LOG") == "1";

        public static void Init()
        {
            if (Verbose)
            {
                try
                {
                    File.WriteAllText(AppPaths.LogFile,
                        $"[{DateTime.Now:O}] start, os={Environment.OSVersion.Version}, arch={Environment.ProcessPath}{Environment.NewLine}");
                }
                catch { /* logging must never break the app */ }

                return;
            }

            // Keep the previous session's errors for one restart, then start clean.
            try
            {
                var file = AppPaths.LogFile;
                if (!File.Exists(file)) return;

                var previous = Path.Combine(AppPaths.DataDir, "log.previous.txt");
                File.Delete(previous);
                File.Move(file, previous);
            }
            catch { /* logging must never break the app */ }
        }

        public static void Info(string message)
        {
            if (!Verbose) return;
            Write("INFO", message, null);
        }

        public static void Error(string message, Exception? ex = null) => Write("ERR ", message, ex);

        private static void Write(string level, string message, Exception? ex)
        {
            try
            {
                lock (Gate)
                {
                    File.AppendAllText(AppPaths.LogFile,
                        $"[{DateTime.Now:HH:mm:ss.fff}] {level} {message}{(ex is null ? "" : " :: " + ex.GetType().Name + ": " + ex.Message + Environment.NewLine + ex.StackTrace)}{Environment.NewLine}");
                }
            }
            catch { }
        }
    }
}
