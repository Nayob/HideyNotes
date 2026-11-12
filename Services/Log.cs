using System;
using System.IO;

namespace HideyNotes.Services
{
    public static class Log
    {
        private static readonly object Gate = new object();

        public static void Init()
        {
            try
            {
                File.WriteAllText(AppPaths.LogFile,
                    $"[{DateTime.Now:O}] start, os={Environment.OSVersion.Version}, arch={Environment.ProcessPath}{Environment.NewLine}");
            }
            catch { /* logging must never break the app */ }
        }

        public static void Info(string message) => Write("INFO", message, null);

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
