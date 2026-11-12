using System;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using HideyNotes.Localization;
using HideyNotes.Services;
using HideyNotes.Windows;

namespace HideyNotes
{
    public partial class App : Application
    {
        private Mutex? _singleInstance;
        private TrayIcon? _tray;
        private PanelWindow? _panel;
        private HandleWindow? _handle;

        public static new App Current => (App)Application.Current;

        public AppSettings Settings { get; private set; } = new AppSettings();
        public ThemeService Theme { get; private set; } = null!;
        public NotesStore Notes { get; private set; } = null!;
        public SlideController Controller { get; private set; } = null!;

        /// <summary>Windows may deny tray registration in restricted sessions.</summary>
        public bool TrayAvailable => _tray?.Available ?? false;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // The mutex name can be overridden so a test build can run next to the installed one.
            var mutexName = Environment.GetEnvironmentVariable("HIDEYNOTES_MUTEX");
            if (string.IsNullOrWhiteSpace(mutexName)) mutexName = "HideyNotes.SingleInstance.8F1C";

            _singleInstance = new Mutex(true, mutexName, out var isFirstInstance);
            if (!isFirstInstance)
            {
                Native.PostMessage(Native.HWND_BROADCAST,
                    (uint)Native.RegisterWindowMessage("HIDEYNOTES_Activate"), IntPtr.Zero, IntPtr.Zero);
                Shutdown();
                return;
            }

            DispatcherUnhandledException += (_, args) =>
            {
                // Keep the app alive on unexpected UI errors; the log keeps the trace.
                Log.Error("dispatcher exception", args.Exception);
                args.Handled = true;
            };

            try
            {
                AppPaths.EnsureCreated();
                Log.Init();

                // English by default; the stored choice is applied right after the settings are read.
                Loc.Instance.Initialize(AppSettings.EnglishCode);

                if (AppPaths.FallbackReason is not null)
                {
                    Log.Info($"data folder fallback: {AppPaths.FallbackReason}");
                }
            }
            catch (Exception ex)
            {
                CrashLog.Write(ex);

                // The data folder itself failed: load at least the built-in strings for the message.
                Loc.Instance.Reload();

                MessageBox.Show(
                    Loc.T("app.dataFolderError") + Environment.NewLine + Environment.NewLine +
                    AppPaths.DataDir + Environment.NewLine + Environment.NewLine + ex.Message,
                    "HideyNotes", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown();
                return;
            }

            Settings = SettingsStore.Load();
            Loc.Instance.SetLanguage(Settings.Language);

            // Keep the Run entry pointing at the current exe location.
            if (Settings.AutoStart) AutoStart.Apply(Environment.ProcessPath ?? string.Empty);

            Theme = new ThemeService();
            Theme.Start(Settings.Theme);
            Notes = new NotesStore(Dispatcher);
            Notes.Load();

            _panel = new PanelWindow();
            _handle = new HandleWindow();

            // The tray may be denied (restricted/low-integrity session): fall back to a taskbar button.
            _tray = new TrayIcon(this);
            if (!TrayAvailable)
            {
                _panel.ShowOnStartup();
                Log.Info("tray unavailable: taskbar fallback enabled");
            }

            Controller = new SlideController(_panel, _handle, Settings);
            MainWindow = _panel;

            Controller.Initialize();

            var openOnStart = Array.Exists(e.Args, a => string.Equals(a, "--open", StringComparison.OrdinalIgnoreCase));
            var settingsOnStart = Array.Exists(e.Args, a => string.Equals(a, "--settings", StringComparison.OrdinalIgnoreCase));
            var historyOnStart = Array.Exists(e.Args, a => string.Equals(a, "--history", StringComparison.OrdinalIgnoreCase));
            var searchArg = Array.Find(e.Args, a => a.StartsWith("--search=", StringComparison.OrdinalIgnoreCase));
            if (openOnStart || settingsOnStart || historyOnStart || searchArg is not null)
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    Controller.ShowPanel();
                    if (settingsOnStart) Controller.OpenSettings();

                    if (historyOnStart)
                    {
                        // Let the slide animation finish so the dialog keeps focus.
                        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
                        timer.Tick += (_, _) =>
                        {
                            timer.Stop();
                            _panel?.OpenHistory();
                        };
                        timer.Start();
                    }

                    if (searchArg is not null) _panel?.OpenSearchWith(searchArg["--search=".Length..]);
                }), DispatcherPriority.ApplicationIdle);
            }

            Log.Info($"started, openOnStart={openOnStart}");
        }

        public void RefreshTrayMenu() => _tray?.RefreshTexts();

        public void ExitApp()
        {
            try
            {
                Notes.Flush();
                SettingsStore.Save(Settings);
            }
            catch (Exception ex)
            {
                Log.Error("exit save", ex);
            }

            Shutdown();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try
            {
                Notes?.Flush();
                SettingsStore.Save(Settings);
            }
            catch { }

            _tray?.Dispose();
            Controller?.Dispose();
            Theme?.Dispose();
            _singleInstance?.Dispose();

            Log.Info("exit");
            base.OnExit(e);
        }
    }
}