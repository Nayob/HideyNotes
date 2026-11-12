using System;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace HideyNotes.Services
{
    public sealed class ThemeService : IDisposable
    {
        private string _mode = "system";
        private bool _subscribed;

        public bool IsDark { get; private set; }

        /// <summary>Text colour of the active theme (used when the user picks "auto").</summary>
        public Color TextColor { get; private set; } = Color.FromRgb(0xEA, 0xEB, 0xEF);

        /// <summary>Set by the window before <see cref="Apply"/> is called.</summary>
        public bool MicaEnabled { get; set; }

        public event Action? Changed;

        public void Start(string mode)
        {
            _mode = Normalize(mode);
            if (!_subscribed)
            {
                SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
                _subscribed = true;
            }

            Apply();
        }

        public void SetMode(string mode)
        {
            _mode = Normalize(mode);
            Apply();
        }

        public static string Normalize(string mode) =>
            mode switch
            {
                "light" => "light",
                "dark" => "dark",
                _ => "system"
            };

        public static bool SystemIsDark()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
            }
            catch
            {
                return false;
            }
        }

        public void Apply()
        {
            IsDark = _mode switch
            {
                "dark" => true,
                "light" => false,
                _ => SystemIsDark()
            };

            WriteResources();
            Changed?.Invoke();
        }

        private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            if (e.Category != UserPreferenceCategory.General &&
                e.Category != UserPreferenceCategory.Color &&
                e.Category != UserPreferenceCategory.VisualStyle)
            {
                return;
            }

            var app = Application.Current;
            app?.Dispatcher.BeginInvoke(new Action(Apply));
        }

        private void WriteResources()
        {
            var resources = Application.Current?.Resources;
            if (resources is null) return;

            var mica = MicaEnabled;
            var dark = IsDark;

            void Set(string key, Color color)
            {
                // Elements look these brushes up by DynamicResource; the app-level dictionary
                // must hold the brush itself, otherwise the merged dictionary wins.
                // Never write raw colour keys: names like "Segment" are also style keys.
                var brush = new SolidColorBrush(color);
                brush.Freeze();
                resources[key + "Brush"] = brush;
            }

            Color Hex(string hex) => (Color)ColorConverter.ConvertFromString(hex);

            var panel = dark
                ? (mica ? Color.FromArgb(0xD8, 0x15, 0x16, 0x1A) : Hex("#FF15161A"))
                : (mica ? Color.FromArgb(0xE6, 0xF9, 0xF9, 0xFB) : Hex("#FFFBFBFD"));

            Set("Panel", panel);
            Set("PanelBorder", dark ? Hex("#2EFFFFFF") : Hex("#26000000"));
            Set("Card", dark ? Hex("#FF1E2026") : Hex("#FFFFFFFF"));
            Set("Scrim", dark ? Hex("#66000000") : Hex("#4D000000"));
            TextColor = dark ? Hex("#FFEAEBEF") : Hex("#FF191B1F");
            Set("Text", TextColor);

            // Muted is the shared tone for inactive tab titles, icon buttons and hints: on a light
            // background a translucent grey reads too washed out, so it is opaque and darker there.
            Set("Muted", dark ? Hex("#9FB0B4BE") : Hex("#FF4C5261"));
            Set("Accent", dark ? Hex("#FF5E9DFF") : Hex("#FF2F6FEB"));
            Set("AccentSoft", dark ? Hex("#332F6FEB") : Hex("#FFDCE6FB"));
            Set("Segment", dark ? Hex("#14FFFFFF") : Hex("#14000000"));
            Set("SegmentBorder", dark ? Hex("#22FFFFFF") : Hex("#26000000"));
            Set("Hover", dark ? Hex("#1AFFFFFF") : Hex("#1A000000"));
            Set("Pressed", dark ? Hex("#26FFFFFF") : Hex("#26000000"));
            Set("ScrollThumb", dark ? Hex("#33FFFFFF") : Hex("#33000000"));
            Set("ScrollThumbHover", dark ? Hex("#59FFFFFF") : Hex("#4D000000"));
            Set("Handle", dark ? Hex("#F216171B") : Hex("#F2FFFFFF"));
            Set("HandleHover", dark ? Hex("#FF2A2D35") : Hex("#FFEDEFF4"));
            Set("HandleBorder", dark ? Hex("#40FFFFFF") : Hex("#26000000"));
            Set("HandleIcon", dark ? Hex("#FFEAEBEF") : Hex("#FF3A3F4A"));
        }

        public void Dispose()
        {
            if (_subscribed)
            {
                SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
                _subscribed = false;
            }
        }
    }
}
