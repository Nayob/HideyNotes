namespace HideyNotes.Services
{
    public sealed class AppSettings
    {
        public const string DefaultHotkey = "Ctrl+Shift+Space";

        /// <summary>Language used on a fresh install.</summary>
        public const string EnglishCode = "en";

        public int Schema { get; set; } = 5;

        /// <summary>Interface language code; empty = follow Windows. English is the default.</summary>
        public string Language { get; set; } = EnglishCode;

        // Geometry
        public double PanelWidthRatio { get; set; } = 0.5;
        public double PanelHeightRatio { get; set; } = 0.7;

        /// <summary>Device name of the monitor to live on; empty = the monitor under the cursor.</summary>
        public string MonitorDevice { get; set; } = "";

        // Behaviour
        public int HideDelayMs { get; set; } = 300;   // 0 = hide at once
        public bool HotkeyEnabled { get; set; } = true;

        /// <summary>How the panel reveals itself: "slide" (220 ms, the default) or "instant".</summary>
        public string Transition { get; set; } = "slide";
        public string Hotkey { get; set; } = DefaultHotkey;
        public bool AutoStart { get; set; }

        // Search
        public bool RememberSearch { get; set; } = true;
        public string LastSearch { get; set; } = "";

        // Appearance
        public string Theme { get; set; } = "system"; // system | light | dark
        public bool UseMica { get; set; } = true;

        // Typography (global for all notes)
        public string FontFamily { get; set; } = "Segoe UI";
        public double FontSize { get; set; } = 15;
        public double LineHeight { get; set; } = 1.5;
        public double EditorPadding { get; set; } = 20;
        public string TextColorDark { get; set; } = "";
        public string TextColorLight { get; set; } = "";
    }
}
