using System;
using System.Windows.Input;
using Forms = System.Windows.Forms;

namespace HideyNotes.Services
{
    public sealed class HotkeyService
    {
        private const uint MOD_ALT = 0x0001;
        private const uint MOD_CONTROL = 0x0002;
        private const uint MOD_SHIFT = 0x0004;
        private const uint MOD_WIN = 0x0008;
        private const uint MOD_NOREPEAT = 0x4000;

        private IntPtr _hwnd;
        private bool _registered;

        public const int Id = 0x4E21;

        public bool TryRegister(IntPtr hwnd, string combo)
        {
            Unregister();
            _hwnd = hwnd;

            if (!TryParse(combo, out var modifiers, out var virtualKey))
            {
                Log.Error($"hotkey parse failed: {combo}");
                return false;
            }

            _registered = Native.RegisterHotKey(hwnd, Id, modifiers | MOD_NOREPEAT, virtualKey);
            if (!_registered)
            {
                Log.Error($"RegisterHotKey failed for {combo}");
            }

            return _registered;
        }

        public void Unregister()
        {
            if (!_registered) return;

            try
            {
                Native.UnregisterHotKey(_hwnd, Id);
            }
            catch (Exception ex)
            {
                Log.Error("UnregisterHotKey", ex);
            }

            _registered = false;
        }

        public static bool TryParse(string combo, out uint modifiers, out uint virtualKey)
        {
            modifiers = 0;
            virtualKey = 0;

            if (string.IsNullOrWhiteSpace(combo)) return false;

            var parts = combo.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 2) return false;

            for (var i = 0; i < parts.Length - 1; i++)
            {
                switch (parts[i].ToLowerInvariant())
                {
                    case "ctrl":
                    case "control":
                        modifiers |= MOD_CONTROL;
                        break;
                    case "alt":
                        modifiers |= MOD_ALT;
                        break;
                    case "shift":
                        modifiers |= MOD_SHIFT;
                        break;
                    case "win":
                    case "windows":
                        modifiers |= MOD_WIN;
                        break;
                    default:
                        return false;
                }
            }

            var keyName = parts[^1];
            if (!Enum.TryParse<Key>(keyName, true, out var key)) return false;

            var vk = KeyInterop.VirtualKeyFromKey(key);
            if (vk == 0) return false;

            virtualKey = (uint)vk;
            return true;
        }
    }
}
