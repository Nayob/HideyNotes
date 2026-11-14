using System;
using System.IO;
using System.Windows;

namespace HideyNotes
{
    /// <summary>Entry point with a crash net: startup failures are logged and shown, never silent.</summary>
    public static class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            AppDomain.CurrentDomain.UnhandledException += (_, e) => CrashLog.Write(e.ExceptionObject as Exception);

            // The probe writes a file next to the exe, so it is opt-in: the diagnostic log switch
            // turns it on, while a normal start leaves the install folder untouched.
            if (Environment.GetEnvironmentVariable("HIDEYNOTES_LOG") == "1") LaunchProbe.Run(args);

            try
            {
                var app = new App();
                app.InitializeComponent();
                app.Run();
            }
            catch (Exception ex)
            {
                CrashLog.Write(ex);

                try
                {
                    MessageBox.Show(ex.ToString(), HideyNotes.Localization.Loc.T("app.startupErrorTitle"),
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
                catch { }
            }
        }
    }

    internal static class CrashLog
    {
        public static void Write(Exception? ex)
        {
            if (ex is null) return;

            var text = $"[{DateTime.Now:O}] {ex}{Environment.NewLine}";
            var candidates = new[]
            {
                Path.Combine(Path.GetTempPath(), "HideyNotes-crash.txt"),
                Path.Combine(AppContext.BaseDirectory, "crash.txt"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HideyNotes", "crash.txt")
            };

            foreach (var path in candidates)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.AppendAllText(path, text);
                }
                catch { }
            }
        }
    }
}
