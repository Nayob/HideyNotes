using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using HideyNotes.Windows;
using Forms = System.Windows.Forms;

namespace HideyNotes.Services
{
    /// <summary>Owns the panel + handle windows: layout, slide animation, show/hide policy.</summary>
    public sealed class SlideController : IDisposable
    {
        private const double SlideDurationMs = 220;

        // Ratios are relative to the screen, so on a big monitor the panel would swallow half of
        // it; these caps are in DIP (the same units the font and the UI use), so the panel keeps
        // about the same amount of text on every monitor regardless of its DPI.
        private const double MaxPanelWidthDip = 900;
        private const double MaxPanelHeightDip = 1000;

        private readonly PanelWindow _panel;
        private readonly HandleWindow _handle;
        private readonly AppSettings _settings;
        private readonly DispatcherTimer _hideTimer;
        private readonly DispatcherTimer _focusWatch;
        private readonly Stopwatch _clock = new Stopwatch();
        private readonly HotkeyService _hotkey = new HotkeyService();

        private IntPtr _panelHwnd;
        private IntPtr _handleHwnd;

        private int _workRight;
        private int _panelTop;
        private int _panelWidth;
        private int _panelHeight;
        private int _handleWidth = 48;
        private int _handleHeight = 140;
        private int _handleTop;

        private double _progress;
        private double _from;
        private double _to;
        private bool _animating;
        private bool _handleVisible = true;
        private bool _disposed;
        private bool _inLayout;
        private string _layoutKey = "";
        private double _panelScale = 1.0;
        private double _handleScale = 1.0;

        public SlideController(PanelWindow panel, HandleWindow handle, AppSettings settings)
        {
            _panel = panel;
            _handle = handle;
            _settings = settings;

            _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _hideTimer.Tick += OnHideTimerTick;

            _focusWatch = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _focusWatch.Tick += OnFocusWatchTick;
            _focusWatch.Start();
        }

        public HotkeyService Hotkey => _hotkey;

        public bool IsOpen => _progress >= 0.999;

        public bool IsAnimating => _animating;

        public void Initialize()
        {
            // Create the windows without showing them. The layout below parks both windows and
            // then moves them off-screen; a visible window would be painted at those in-between
            // positions, which is exactly the rectangle that flickers in the middle of the screen.
            _panelHwnd = new WindowInteropHelper(_panel).EnsureHandle();
            _handleHwnd = new WindowInteropHelper(_handle).EnsureHandle();

            _panel.DpiChanged += OnDpiChanged;
            _handle.DpiChanged += OnDpiChanged;

            // Size everything for the target monitor, then slide the panel out of sight.
            _progress = 1;
            RefreshLayout();
            _progress = 0;
            ApplyFrame(0);

            _panel.Show();
            _handle.Show();

            ApplyAlwaysOnTop();
            _handle.SetOpen(false);
            ShowHandle();
            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
            LogRect("init");
        }

        private void LogRect(string tag)
        {
            if (_panelHwnd == IntPtr.Zero) return;
            Native.GetWindowRect(_panelHwnd, out var r);
            Log.Info($"{tag}: rect=({r.Left},{r.Top})-({r.Right},{r.Bottom}) size={r.Right - r.Left}x{r.Bottom - r.Top} " +
                     $"wpf={_panel.Left:0},{_panel.Top:0} {_panel.Width:0}x{_panel.Height:0} actual={_panel.ActualWidth:0}x{_panel.ActualHeight:0} progress={_progress:0.00}");
        }

        public void AttachPanelHandle(IntPtr hwnd)
        {
            if (_panelHwnd == IntPtr.Zero) _panelHwnd = hwnd;
            ApplyHotkey();
        }

        // ---------- geometry ----------

        /// <summary>
        /// Recomputes the geometry for the monitor the panel is meant to live on: the one chosen
        /// in the settings, or the one under the cursor when the choice is "auto".
        /// The windows are moved onto that monitor first, because the DPI (and therefore the
        /// window size in WPF units) is a property of the monitor, not of the app.
        /// </summary>
        public void RefreshLayout()
        {
            if (_panelHwnd == IntPtr.Zero || _inLayout) return;

            _inLayout = true;
            try
            {
                var screen = PickScreen();
                var work = screen.WorkingArea;

                // The scale belongs to the target monitor, so ask the monitor itself: the window
                // reports its old DPI until it has actually landed on the new screen.
                var centreX = work.Left + work.Width / 2;
                var centreY = work.Top + work.Height / 2;
                var targetDpi = Math.Max(96u, Native.MonitorDpiAt(centreX, centreY));
                var scale = targetDpi / 96.0;
                _panelScale = scale;
                _handleScale = scale;

                // Windows only has to settle the DPI of the target monitor when the window is not
                // there yet. On a single monitor (or on any monitor with the same scale) the window
                // already has the right DPI, and moving it to the middle of the screen for nothing
                // would be visible: the panel is not hidden while it is parked off-screen.
                var windowDpi = Native.GetDpiForWindow(_panelHwnd);
                var park = windowDpi != targetDpi;

                // Park with both windows invisible: a hidden window is not composed by the desktop,
                // so the jump across the screen cannot be painted, not even for a single frame.
                var hidePanel = park && Native.IsWindowVisible(_panelHwnd) && !_panel.IsActive;
                var hideHandle = park && _handleVisible && Native.IsWindowVisible(_handleHwnd) && !_panel.IsActive;

                try
                {
                    if (hidePanel) Native.ShowWindow(_panelHwnd, Native.SW_HIDE);
                    if (hideHandle) Native.ShowWindow(_handleHwnd, Native.SW_HIDE);

                    if (park)
                    {
                        Log.Info($"park for dpi {windowDpi}->{targetDpi} hidden={hidePanel}/{hideHandle}");

                        Native.SetWindowPos(_panelHwnd, Native.HWND_TOPMOST,
                            centreX - _panelWidth / 2, centreY - _panelHeight / 2, 0, 0,
                            Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
                        Native.SetWindowPos(_handleHwnd, Native.HWND_TOPMOST,
                            centreX, centreY - _handleHeight / 2, 0, 0,
                            Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
                    }

                    _panelWidth = Math.Max(320, (int)Math.Round(Math.Min(
                        work.Width * _settings.PanelWidthRatio, MaxPanelWidthDip * scale)));
                    _panelHeight = Math.Max(240, (int)Math.Round(Math.Min(
                        work.Height * _settings.PanelHeightRatio, MaxPanelHeightDip * scale)));
                    _panelTop = work.Top + (int)Math.Round((work.Height - _panelHeight) / 2.0);
                    _workRight = work.Right;

                    _handleWidth = (int)Math.Round(48 * scale);
                    _handleHeight = (int)Math.Round(118 * scale);
                    _handleTop = _panelTop + (_panelHeight - _handleHeight) / 2;

                    _panel.Width = _panelWidth / scale;
                    _panel.Height = _panelHeight / scale;
                    _handle.Width = _handleWidth / _handleScale;
                    _handle.Height = _handleHeight / _handleScale;

                    ApplyFrame(_progress);
                }
                finally
                {
                    // Bring the windows back even if the layout above failed half-way through.
                    if (hidePanel) Native.ShowWindow(_panelHwnd, Native.SW_SHOWNOACTIVATE);
                    if (hideHandle) Native.ShowWindow(_handleHwnd, Native.SW_SHOWNOACTIVATE);
                }

                var key = $"{screen.DeviceName}|{work.Width}x{work.Height}|{_panelWidth}x{_panelHeight}|{_panelScale:0.##}";
                if (key != _layoutKey)
                {
                    _layoutKey = key;
                    Log.Info($"layout: screen={screen.DeviceName} work={work.Width}x{work.Height} " +
                             $"panel={_panelWidth}x{_panelHeight} top={_panelTop} " +
                             $"dpi={Native.GetDpiForWindow(_panelHwnd)} " +
                             $"mode={(string.IsNullOrEmpty(_settings.MonitorDevice) ? "auto" : "fixed")}");
                }
            }
            finally
            {
                _inLayout = false;
            }
        }

        private Forms.Screen PickScreen()
        {
            var device = _settings.MonitorDevice;

            if (!string.IsNullOrEmpty(device))
            {
                foreach (var screen in Forms.Screen.AllScreens)
                {
                    if (string.Equals(screen.DeviceName, device, StringComparison.OrdinalIgnoreCase)) return screen;
                }

                // The chosen monitor is gone right now (unplugged, dock): use the primary one,
                // but keep the name so it comes back when the monitor returns.
                Log.Info($"monitor {device} not attached, falling back to the primary one");
                return Forms.Screen.PrimaryScreen ?? Forms.Screen.AllScreens[0];
            }

            return Forms.Screen.FromPoint(Forms.Cursor.Position);
        }

        /// <summary>All monitors, for the settings picker.</summary>
        public static IReadOnlyList<Forms.Screen> Screens() => Forms.Screen.AllScreens;

        /// <summary>A monitor was added, removed or rescaled: follow it.</summary>
        private void OnDpiChanged(object? sender, System.Windows.DpiChangedEventArgs e)
        {
            RefreshLayout();
        }

        private void ApplyFrame(double progress)
        {
            if (_panelHwnd == IntPtr.Zero) return;

            progress = Math.Clamp(progress, 0, 1);
            var x = _workRight - (int)Math.Round(_panelWidth * progress);
            var handleX = x - _handleWidth;

            var insertAfter = Native.HWND_TOPMOST;

            // The size is applied here as well: a window that just moved to a monitor with
            // another DPI keeps its old pixel size until it is laid out again, and the panel
            // has to be exactly half of the screen it lives on.
            Native.SetWindowPos(_panelHwnd, insertAfter, x, _panelTop, _panelWidth, _panelHeight,
                Native.SWP_NOACTIVATE);

            if (!_handleVisible) return;

            Native.SetWindowPos(_handleHwnd, insertAfter, handleX, _handleTop, _handleWidth, _handleHeight,
                Native.SWP_NOACTIVATE);
        }

        // ---------- animation ----------

        private void AnimateTo(double target)
        {
            _from = _progress;
            _to = target;

            if (Math.Abs(_from - _to) < 0.0001)
            {
                _progress = _to;
                ApplyFrame(_progress);
                OnSlideCompleted();
                return;
            }

            _clock.Restart();
            if (!_animating)
            {
                _animating = true;
                CompositionTarget.Rendering += OnRendering;
            }
        }

        private void OnRendering(object? sender, EventArgs e)
        {
            var t = Math.Clamp(_clock.Elapsed.TotalMilliseconds / SlideDurationMs, 0, 1);
            var eased = 1 - Math.Pow(1 - t, 3);
            _progress = _from + (_to - _from) * eased;
            ApplyFrame(_progress);

            if (t < 1) return;

            CompositionTarget.Rendering -= OnRendering;
            _animating = false;
            _progress = _to;
            ApplyFrame(_progress);
            OnSlideCompleted();
        }

        private void OnSlideCompleted()
        {
            if (_progress >= 0.999)
            {
                _handle.SetOpen(true);

                // Never steal focus from a dialog that is already open on top of the panel.
                if (_panel.IsHistoryOpen) return;

                _panel.Activate();
                _panel.FocusEditor();
                LogRect("opened");
            }
            else
            {
                _handle.SetOpen(false);
                _panel.CommitDraft();
                ShowHandle(); // the tab belongs to the closed state only
            }
        }

        // ---------- public api ----------

        /// <summary>The handle is only needed while the panel is closed, so it follows the edge out.</summary>
        private void ShowHandle()
        {
            if (_handleVisible)
            {
                ApplyFrame(_progress);
                return;
            }

            _handleVisible = true;
            _handle.Show();
            ApplyFrame(_progress);
        }

        private void HideHandle()
        {
            if (!_handleVisible) return;
            _handleVisible = false;
            _handle.Hide();
        }

        public void ShowPanel()
        {
            if (_disposed) return;

            // "Auto" mode means the monitor under the cursor, so pick it up on every open.
            RefreshLayout();
            HideHandle();
            _panel.Show();

            if (_animating)
            {
                if (_to >= 0.999) return;
                AnimateTo(1);
                _panel.Activate();
                return;
            }

            if (_progress >= 0.999)
            {
                _panel.Activate();
                _panel.FocusEditor();
                return;
            }

            AnimateTo(1);
            _panel.Activate();
            _panel.FocusEditor();
        }

        public void HidePanel()
        {
            if (_disposed) return;

            _hideTimer.Stop();
            _panel.CloseSettings();
            _panel.CloseHistory();
            _panel.CommitDraft();

            if (_progress <= 0.001 && !_animating)
            {
                ShowHandle();
                return;
            }

            AnimateTo(0);
        }

        public void Toggle()
        {
            var isOpening = _animating ? _to >= 0.5 : _progress >= 0.5;
            if (isOpening) HidePanel();
            else ShowPanel();
        }

        public void OpenSettings()
        {
            ShowPanel();
            _panel.OpenSettings();
        }

        public void NotifyPanelActivated()
        {
            _hideTimer.Stop();
        }

        public void NotifyPanelDeactivated()
        {
            if (_disposed) return;
            if (_panel.IsSettingsOpen || _panel.IsHistoryOpen) return;
            if (_progress < 0.5) return;

            ScheduleHideIfFocusLost();
        }

        private void ScheduleHideIfFocusLost()
        {
            var foreground = Native.GetForegroundWindow();
            Native.GetWindowThreadProcessId(foreground, out var processId);
            if (processId == (uint)Environment.ProcessId) return; // our own popup / the handle window

            var delay = _settings.HideDelayMs;
            if (delay < 0) return;
            Log.Info($"hide scheduled: fg={Native.Describe(foreground)} delay={delay}");

            if (delay == 0)
            {
                HidePanel();
                return;
            }

            if (_hideTimer.IsEnabled) return;
            _hideTimer.Interval = TimeSpan.FromMilliseconds(delay);
            _hideTimer.Start();
        }

        /// <summary>Safety net: catches focus losses the Deactivated event misses (dialogs, owner changes).</summary>
        private void OnFocusWatchTick(object? sender, EventArgs e)
        {
            if (_disposed || _progress < 0.999) return;
            if (_panel.IsSettingsOpen || _panel.IsHistoryOpen) return;
            if (_hideTimer.IsEnabled) return;

            ScheduleHideIfFocusLost();
        }

        private void OnHideTimerTick(object? sender, EventArgs e)
        {
            _hideTimer.Stop();

            var foreground = Native.GetForegroundWindow();
            Native.GetWindowThreadProcessId(foreground, out var processId);
            Log.Info($"hide timer: fg={Native.Describe(foreground)} own={processId == (uint)Environment.ProcessId}");
            if (processId == (uint)Environment.ProcessId) return; // focus came back

            HidePanel();
        }

        /// <summary>Always on top by design: the panel is a floating overlay that hides on blur.</summary>
        public void ApplyAlwaysOnTop()
        {
            _panel.Topmost = true;
            _handle.Topmost = true;
            ApplyFrame(_progress);
        }

        public void ApplyHotkey()
        {
            _hotkey.Unregister();
            if (!_settings.HotkeyEnabled || _panelHwnd == IntPtr.Zero) return;
            if (_hotkey.TryRegister(_panelHwnd, _settings.Hotkey)) Log.Info($"hotkey ok: {_settings.Hotkey}");
        }

        private void OnDisplaySettingsChanged(object? sender, EventArgs e)
        {
            _panel.Dispatcher.BeginInvoke(new Action(RefreshLayout), DispatcherPriority.Background);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _panel.DpiChanged -= OnDpiChanged;
            _handle.DpiChanged -= OnDpiChanged;
            _hideTimer.Stop();
            _focusWatch.Stop();
            if (_animating) CompositionTarget.Rendering -= OnRendering;
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            _hotkey.Unregister();
        }
    }
}
