using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using HideyNotes.Localization;
using Forms = System.Windows.Forms;

namespace HideyNotes.Services
{
    /// <summary>
    /// Tray icon built directly on Shell_NotifyIcon: unlike WinForms' NotifyIcon this reports
    /// whether the shell actually accepted the icon, which is logged for troubleshooting.
    /// </summary>
    public sealed class TrayIcon : IDisposable
    {
        private const int WM_TRAYICON = 0x8001; // WM_APP + 1

        private const int NIM_ADD = 0x00000000;
        private const int NIM_MODIFY = 0x00000001;
        private const int NIM_DELETE = 0x00000002;

        private const int NIF_MESSAGE = 0x00000001;
        private const int NIF_ICON = 0x00000002;
        private const int NIF_TIP = 0x00000004;
        private const int NIF_SHOWTIP = 0x00000080;

        private readonly App _app;
        private readonly Icon _icon;
        private readonly Forms.ContextMenuStrip _menu;
        private readonly HwndSource _source;
        private NOTIFYICONDATA _data;
        private bool _added;

        /// <summary>False when Windows refuses tray access (restricted/low-integrity session).</summary>
        public bool Available => _added;

        public TrayIcon(App app)
        {
            _app = app;
            _icon = LoadAppIcon();

            _menu = new Forms.ContextMenuStrip { ShowImageMargin = false };
            FillMenu();

            var parameters = new HwndSourceParameters("HideyNotesTray")
            {
                WindowStyle = 0,
                ParentWindow = new IntPtr(-3), // HWND_MESSAGE
                Width = 0,
                Height = 0
            };

            _source = new HwndSource(parameters);
            _source.AddHook(WndProc);

            Loc.Instance.Changed += FillMenu;
            Add();
        }

        /// <summary>Menu texts follow the interface language.</summary>
        public void FillMenu()
        {
            _menu.Items.Clear();
            _menu.Items.Add(Loc.T("tray.toggle"), null, (_, _) => _app.Controller.Toggle());
            _menu.Items.Add(Loc.T("tray.settings"), null, (_, _) => _app.Controller.OpenSettings());
            _menu.Items.Add(new Forms.ToolStripSeparator());
            _menu.Items.Add(Loc.T("tray.exit"), null, (_, _) => _app.ExitApp());
        }

        /// <summary>Menu and tooltip follow the interface language.</summary>
        public void RefreshTexts()
        {
            FillMenu();

            if (!_added) return;

            _data.szTip = Loc.T("tray.tooltip");
            Shell_NotifyIcon(NIM_MODIFY, ref _data);
        }

        private void Add()
        {
            _data = new NOTIFYICONDATA
            {
                cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
                hWnd = _source.Handle,
                uID = 1,
                uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP,
                uCallbackMessage = WM_TRAYICON,
                hIcon = _icon.Handle,
                szTip = Loc.T("tray.tooltip"),
                szInfo = string.Empty,
                szInfoTitle = string.Empty
            };

            _added = Shell_NotifyIcon(NIM_ADD, ref _data);
            var error = Marshal.GetLastWin32Error();

            Log.Info($"tray add: ok={_added} error={error} icon={_icon.Width}x{_icon.Height} hwnd={_source.Handle}");

            if (_added) return;

            // Retry once with a system icon: a broken HICON is the most likely reason.
            _data.hIcon = SystemIcons.Application.Handle;
            _added = Shell_NotifyIcon(NIM_ADD, ref _data);
            Log.Info($"tray add with system icon: ok={_added} error={Marshal.GetLastWin32Error()}");
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg != WM_TRAYICON) return IntPtr.Zero;

            switch ((int)lParam)
            {
                case 0x0202: // WM_LBUTTONUP
                    _app.Controller?.Toggle();
                    handled = true;
                    break;
                case 0x0205: // WM_RBUTTONUP
                    _menu.Show(Forms.Cursor.Position);
                    handled = true;
                    break;
            }

            return IntPtr.Zero;
        }

        private static Icon LoadAppIcon()
        {
            try
            {
                var resource = System.Windows.Application.GetResourceStream(
                    new Uri("Assets/HideyNotes.ico", UriKind.Relative));

                if (resource?.Stream is { } stream)
                {
                    using (stream)
                    {
                        return new Icon(stream);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("app icon load failed", ex);
            }

            return CreateIcon();
        }

        private static Icon CreateIcon()
        {
            const int size = 32;

            using var bitmap = new Bitmap(size, size);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.Clear(Color.Transparent);

                var rect = new RectangleF(0, 0, size, size);
                using var path = RoundedRect(rect, size * 0.24f);
                using var brush = new LinearGradientBrush(rect,
                    Color.FromArgb(255, 94, 157, 255),
                    Color.FromArgb(255, 47, 111, 235), 45f);
                graphics.FillPath(brush, path);

                using var font = new Font("Segoe UI", size * 0.56f, FontStyle.Bold, GraphicsUnit.Pixel);
                using var format = new StringFormat
                {
                    Alignment = StringAlignment.Center,
                    LineAlignment = StringAlignment.Center
                };
                graphics.DrawString("H", font, Brushes.White, rect, format);
            }

            var handle = bitmap.GetHicon();
            try
            {
                using var temp = Icon.FromHandle(handle);
                return (Icon)temp.Clone();
            }
            finally
            {
                Native.DestroyIcon(handle);
            }
        }

        private static GraphicsPath RoundedRect(RectangleF rect, float radius)
        {
            var path = new GraphicsPath();
            var diameter = radius * 2f;

            path.AddArc(rect.X, rect.Y, diameter, diameter, 180, 90);
            path.AddArc(rect.Right - diameter, rect.Y, diameter, diameter, 270, 90);
            path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(rect.X, rect.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();

            return path;
        }

        public void Dispose()
        {
            try
            {
                Loc.Instance.Changed -= FillMenu;

                if (_added) Shell_NotifyIcon(NIM_DELETE, ref _data);
                _source.Dispose();
                _menu.Dispose();
                _icon.Dispose();
            }
            catch { }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NOTIFYICONDATA
        {
            public int cbSize;
            public IntPtr hWnd;
            public int uID;
            public int uFlags;
            public int uCallbackMessage;
            public IntPtr hIcon;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szTip;

            public int dwState;
            public int dwStateMask;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string szInfo;

            public int uVersion;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string szInfoTitle;

            public int dwInfoFlags;
            public Guid guidItem;
            public IntPtr hBalloonIcon;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool Shell_NotifyIcon(int message, ref NOTIFYICONDATA data);
    }
}
