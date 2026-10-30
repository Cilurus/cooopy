using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Cooopy.Services
{
    /// <summary>
    /// Makes a TextBox insert "SHIFT" / "CTRL" / "ALT" when the user taps that modifier key
    /// on its own. Any key pressed while the modifier is held (Ctrl+C, Shift+A, Alt+Tab,
    /// IME composition keys ...) cancels the insertion, so combinations are never affected.
    /// </summary>
    public static class ModifierTextBehavior
    {
        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        public static readonly DependencyProperty EnabledProperty =
            DependencyProperty.RegisterAttached(
                "Enabled",
                typeof(bool),
                typeof(ModifierTextBehavior),
                new PropertyMetadata(false, OnEnabledChanged));

        private static readonly DependencyProperty WindowStateProperty =
            DependencyProperty.RegisterAttached(
                "WindowState",
                typeof(WindowState),
                typeof(ModifierTextBehavior),
                new PropertyMetadata(null));

        private sealed class WindowState
        {
            public Key PendingModifier = Key.None;
            public bool OtherKeyPressed;
            public bool Hooked;
        }

        public static bool GetEnabled(DependencyObject element) => (bool)element.GetValue(EnabledProperty);

        public static void SetEnabled(DependencyObject element, bool value) => element.SetValue(EnabledProperty, value);

        private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not TextBox box) return;

            if ((bool)e.NewValue)
            {
                box.Loaded += Box_Loaded;
                if (box.IsLoaded) HookWindow(box);
            }
            else
            {
                box.Loaded -= Box_Loaded;
            }
        }

        private static void Box_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox box) HookWindow(box);
        }

        private static void HookWindow(TextBox box)
        {
            var window = Window.GetWindow(box);
            if (window == null) return;

            var state = GetState(window);
            if (state.Hooked) return;
            state.Hooked = true;

            window.PreviewKeyDown += Window_PreviewKeyDown;
            window.PreviewKeyUp += Window_PreviewKeyUp;
            window.PreviewMouseDown += Window_PreviewMouseDown;
            window.Deactivated += Window_Deactivated;
        }

        private static WindowState GetState(Window window)
        {
            var state = (WindowState)window.GetValue(WindowStateProperty);
            if (state == null)
            {
                state = new WindowState();
                window.SetValue(WindowStateProperty, state);
            }

            return state;
        }

        private static Key Normalize(KeyEventArgs e) => e.Key == Key.System ? e.SystemKey : e.Key;

        private static bool IsModifier(Key key) => key is Key.LeftShift or Key.RightShift
            or Key.LeftCtrl or Key.RightCtrl
            or Key.LeftAlt or Key.RightAlt;

        private static string ModifierText(Key key) => key switch
        {
            Key.LeftShift or Key.RightShift => "SHIFT",
            Key.LeftCtrl or Key.RightCtrl => "CTRL",
            Key.LeftAlt or Key.RightAlt => "ALT",
            _ => ""
        };

        private static bool IsEligibleBox()
        {
            return Keyboard.FocusedElement is TextBox box
                   && GetEnabled(box)
                   && !box.IsReadOnly;
        }

        private static void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (sender is not Window window) return;
            var state = GetState(window);
            var key = Normalize(e);

            if (IsModifier(key))
            {
                if (state.PendingModifier == Key.None)
                {
                    if (IsEligibleBox()) state.PendingModifier = key;
                }
                else if (state.PendingModifier != key)
                {
                    // Two modifiers held together: that is a combination, not a tap.
                    state.OtherKeyPressed = true;
                }

                return;
            }

            if (state.PendingModifier != Key.None)
                state.OtherKeyPressed = true; // any other key means it was used in a shortcut
        }

        private static void Window_PreviewKeyUp(object sender, KeyEventArgs e)
        {
            if (sender is not Window window) return;
            var state = GetState(window);
            var key = Normalize(e);
            if (!IsModifier(key) || state.PendingModifier != key) return;

            // Combination guard: other keys pressed during the hold, another modifier still
            // down, or any non-modifier key physically down right now => never insert.
            var isCombination = state.OtherKeyPressed
                                || Keyboard.Modifiers != ModifierKeys.None
                                || AnyNonModifierKeyDown();

            state.PendingModifier = Key.None;
            state.OtherKeyPressed = false;

            if (isCombination || !IsEligibleBox()) return;

            var insert = ModifierText(key);
            if (insert.Length == 0) return;

            var box = (TextBox)Keyboard.FocusedElement;
            var start = box.SelectionStart;
            box.SelectedText = insert;
            box.CaretIndex = start + insert.Length;
            e.Handled = true;
        }

        private static void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is Window window)
            {
                var state = GetState(window);
                state.PendingModifier = Key.None;
                state.OtherKeyPressed = false;
            }
        }

        private static void Window_Deactivated(object sender, EventArgs e)
        {
            if (sender is Window window)
            {
                var state = GetState(window);
                state.PendingModifier = Key.None;
                state.OtherKeyPressed = false;
            }
        }

        /// <summary>True when some key other than Shift/Ctrl/Alt is physically held down.</summary>
        private static bool AnyNonModifierKeyDown()
        {
            for (var vk = 0x08; vk <= 0xFE; vk++)
            {
                if (vk is 0x10 or 0x11 or 0x12) continue; // generic Shift / Ctrl / Alt
                if (vk is >= 0xA0 and <= 0xA5) continue;   // left/right Shift / Ctrl / Alt
                if ((GetAsyncKeyState(vk) & 0x8000) != 0) return true;
            }

            return false;
        }
    }
}
