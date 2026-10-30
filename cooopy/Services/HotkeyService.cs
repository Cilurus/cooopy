using System.Windows.Input;
using Cooopy.Services;

namespace Cooopy.Services
{
    public class HotkeyService
    {
        public const int IdMain = 0x3001;
        public const int IdSave = 0x3002;

        private IntPtr _hwnd;
        private readonly Dictionary<int, string> _active = new();

        public void Attach(IntPtr hwnd) => _hwnd = hwnd;

        public void Detach()
        {
            if (_hwnd != IntPtr.Zero)
            {
                foreach (var id in _active.Keys.ToList())
                    Native.UnregisterHotKey(_hwnd, id);
            }
            _active.Clear();
        }

        public (bool ok, string error) Apply(int id, string combo)
        {
            if (_hwnd == IntPtr.Zero) return (false, "窗口尚未就绪");
            var parsed = Parse(combo);
            if (parsed == null) return (false, $"快捷键格式无效：{combo}");

            string previous = _active.TryGetValue(id, out var old) ? old : null;
            if (previous != null) Native.UnregisterHotKey(_hwnd, id);
            _active.Remove(id);

            var (mods, vk) = parsed.Value;
            if (Native.RegisterHotKey(_hwnd, id, mods | Native.MOD_NOREPEAT, vk))
            {
                _active[id] = combo;
                return (true, null);
            }

            if (previous != null)
            {
                var restore = Parse(previous);
                if (restore != null &&
                    Native.RegisterHotKey(_hwnd, id, restore.Value.mods | Native.MOD_NOREPEAT, restore.Value.vk))
                    _active[id] = previous;
            }
            return (false, $"快捷键 {combo} 已被其他程序占用，请换一个组合");
        }

        public static (uint mods, uint vk)? Parse(string combo)
        {
            if (string.IsNullOrWhiteSpace(combo)) return null;
            uint mods = 0;
            string key = null;

            foreach (var raw in combo.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                switch (raw.ToLowerInvariant())
                {
                    case "ctrl":
                    case "control":
                        mods |= Native.MOD_CONTROL;
                        break;
                    case "alt":
                        mods |= Native.MOD_ALT;
                        break;
                    case "shift":
                        mods |= Native.MOD_SHIFT;
                        break;
                    // Win 键已不再作为快捷键修饰键：出现即判为无效，
                    // 不能落到 default 分支被当成普通按键解析。
                    case "win":
                    case "windows":
                    case "meta":
                        return null;
                    default:
                        key = raw;
                        break;
                }
            }

            // 组合键必须以 Ctrl / Shift / Alt 开头（单独一个键不能注册为全局热键）。
            if (key == null || mods == 0) return null;
            var vk = KeyToVirtualKey(key);
            return vk == 0 ? null : (mods, vk);
        }

        private static uint KeyToVirtualKey(string key)
        {
            if (key.Length == 1)
            {
                var c = char.ToUpperInvariant(key[0]);
                if (c >= 'A' && c <= 'Z') return (uint)c;
                if (c >= '0' && c <= '9') return (uint)c;
                if (c == '`') return 0xC0;
                if (c == '-') return 0xBD;
                if (c == '=') return 0xBB;
                if (c == '[') return 0xDB;
                if (c == ']') return 0xDD;
                if (c == ';') return 0xBA;
                if (c == '\'') return 0xDE;
                if (c == ',') return 0xBC;
                if (c == '.') return 0xBE;
                if (c == '/') return 0xBF;
            }

            if (Enum.TryParse<Key>(key, true, out var parsed))
            {
                var vk = (uint)KeyInterop.VirtualKeyFromKey(parsed);
                if (vk != 0) return vk;
            }

            return key.ToUpperInvariant() switch
            {
                "SPACE" => 0x20,
                "TAB" => 0x09,
                "ENTER" => 0x0D,
                "ESC" or "ESCAPE" => 0x1B,
                "INSERT" => 0x2D,
                "DELETE" => 0x2E,
                "HOME" => 0x24,
                "END" => 0x23,
                "PAGEUP" => 0x21,
                "PAGEDOWN" => 0x22,
                "LEFT" => 0x25,
                "UP" => 0x26,
                "RIGHT" => 0x27,
                "DOWN" => 0x28,
                "PRINTSCREEN" => 0x2C,
                "PAUSE" => 0x13,
                "CAPSLOCK" => 0x14,
                _ => 0
            };
        }

        /// <summary>Builds a canonical "Ctrl+Alt+C" style string from a WPF key event.</summary>
        public static string FromKeyEvent(Key key, ModifierKeys modifiers)
        {
            if (key == Key.None) return null;
            if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
                or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System)
                return null;

            return Join(modifiers, KeyName(key));
        }

        /// <summary>Builds a combo string from a raw virtual-key code (low-level keyboard hook).</summary>
        public static string FromVirtualKey(int vk, ModifierKeys modifiers)
        {
            // Ignore pure modifier keys; they only participate as modifiers.
            switch (vk)
            {
                case 0x10: // VK_SHIFT
                case 0x11: // VK_CONTROL
                case 0x12: // VK_MENU
                case 0x5B: // VK_LWIN
                case 0x5C: // VK_RWIN
                case 0xA0:
                case 0xA1:
                case 0xA2:
                case 0xA3:
                case 0xA4:
                case 0xA5:
                    return null;
            }

            var key = KeyInterop.KeyFromVirtualKey(vk);
            if (key == Key.None) return null;
            return Join(modifiers, KeyName(key));
        }

        private static string Join(ModifierKeys modifiers, string keyName)
        {
            if (string.IsNullOrEmpty(keyName)) return null;

            var parts = new List<string>();
            if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
            if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
            if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
            parts.Add(keyName);
            return string.Join("+", parts);
        }

        private static string KeyName(Key key) => key switch
        {
            Key.Oem3 => "`",
            Key.OemMinus => "-",
            Key.OemPlus => "=",
            Key.OemOpenBrackets => "[",
            Key.Oem6 => "]",
            Key.OemSemicolon => ";",
            Key.OemQuotes => "'",
            Key.OemComma => ",",
            Key.OemPeriod => ".",
            Key.OemQuestion => "/",
            Key.D0 => "0",
            Key.D1 => "1",
            Key.D2 => "2",
            Key.D3 => "3",
            Key.D4 => "4",
            Key.D5 => "5",
            Key.D6 => "6",
            Key.D7 => "7",
            Key.D8 => "8",
            Key.D9 => "9",
            _ => key.ToString()
        };
    }
}
