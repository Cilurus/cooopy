using System.Runtime.InteropServices;
using System.Windows.Threading;
using Cooopy.Models;

namespace Cooopy.Services
{
    /// <summary>
    /// Global low-level mouse hook that turns "modifier + right click" into a cooopy tag menu.
    /// The click is swallowed so the hosting application does not show its own context menu.
    /// </summary>
    public class RightClickService : IDisposable
    {
        private readonly Func<AppSettings> _settings;
        private readonly Func<bool> _popupVisible;
        private readonly Func<Native.RECT?> _popupRect;
        private readonly Action _closePopup;
        private readonly Action<string, bool, Native.POINT, string> _showPopup;

        private Native.HookProc _proc;
        private IntPtr _hook;
        private bool _suppressRight;
        private bool _replayRight;
        private bool _capturing;
        private bool _leftActivityDuringCapture;

        public bool IsInstalled => _hook != IntPtr.Zero;

        public event Action<string> Notice;

        public RightClickService(
            Func<AppSettings> settings,
            Func<bool> popupVisible,
            Func<Native.RECT?> popupRect,
            Action closePopup,
            Action<string, bool, Native.POINT, string> showPopup)
        {
            _settings = settings;
            _popupVisible = popupVisible;
            _popupRect = popupRect;
            _closePopup = closePopup;
            _showPopup = showPopup;
        }

        public void Install()
        {
            if (_hook != IntPtr.Zero) return;
            _proc = HookCallback;
            _hook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _proc,
                Native.GetModuleHandle(null), 0);
            if (_hook == IntPtr.Zero)
            {
                var error = Marshal.GetLastWin32Error();
                Notice?.Invoke("右键打标初始化失败，可能被安全软件拦截");
                App.Log($"SetWindowsHookEx failed, error={error}");
            }
        }

        public void Uninstall()
        {
            if (_hook != IntPtr.Zero)
            {
                Native.UnhookWindowsHookEx(_hook);
                _hook = IntPtr.Zero;
            }
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (nCode < 0) return Native.CallNextHookEx(_hook, nCode, wParam, lParam);

                var message = wParam.ToInt32();

                // 右键拦截后、判断有没有选中文字之前，如果用户又按了左键（去重新选择文字），
                // 说明这次右键的“选中状态”已经变了，取消本次取词，避免误弹窗。
                if (_capturing && (message == Native.WM_LBUTTONDOWN || message == Native.WM_LBUTTONDBLCLK))
                    _leftActivityDuringCapture = true;

                if (message == Native.WM_RBUTTONDOWN)
                {
                    if (_replayRight)
                    {
                        // 这是“没有选中文字”时我们补发的右键，放行给目标程序弹自己的菜单
                        _replayRight = false;
                        return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
                    }
                    if (_suppressRight) return 1;
                    if (_popupVisible())
                    {
                        var popupData = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);
                        var popupRect = _popupRect();
                        if (popupRect.HasValue && IsOutside(popupRect.Value, popupData.pt))
                            _closePopup();
                        return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
                    }

                    if (ShouldIntercept(out var point))
                    {
                        _suppressRight = true;
                        App.Log($"right click intercepted at {point.X},{point.Y}");
                        var dispatcher = Dispatcher.CurrentDispatcher;
                        dispatcher.BeginInvoke(new Action(async () => await CaptureAsync(point)));
                        return 1;
                    }
                }
                else if (message == Native.WM_RBUTTONUP)
                {
                    if (_suppressRight)
                    {
                        _suppressRight = false;
                        return 1;
                    }
                }
                else if (message == Native.WM_LBUTTONDOWN && _popupVisible())
                {
                    var data = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);
                    var rect = _popupRect();
                    if (rect.HasValue && IsOutside(rect.Value, data.pt))
                        _closePopup();
                }
            }
            catch (Exception ex)
            {
                App.Log("mouse hook error: " + ex);
            }

            return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        private static bool IsOutside(Native.RECT rect, Native.POINT point) =>
            point.X < rect.Left || point.X > rect.Right ||
            point.Y < rect.Top || point.Y > rect.Bottom;

        private bool ShouldIntercept(out Native.POINT point)
        {
            point = default;
            var settings = _settings();
            if (!settings.ContextMenuEnabled || !settings.AutoCopySelection || _capturing || _popupVisible())
                return false;

            var modifierDown = settings.ContextModifier switch
            {
                "Ctrl" => Native.IsKeyDown(Native.VK_CONTROL),
                "Alt" => Native.IsKeyDown(Native.VK_MENU),
                "无" => true,
                _ => Native.IsKeyDown(Native.VK_SHIFT)
            };
            if (!modifierDown) return false;

            var foreground = Native.GetForegroundWindow();
            if (foreground == IntPtr.Zero) return false;

            Native.GetWindowThreadProcessId(foreground, out var pid);
            if (pid == (uint)Environment.ProcessId) return false;
            if (Native.IsForegroundElevated(foreground, out _)) return false;
            if (Native.IsForegroundFullscreen(foreground)) return false;

            Native.GetCursorPos(out point);
            return true;
        }

        private async Task CaptureAsync(Native.POINT point)
        {
            _capturing = true;
            _leftActivityDuringCapture = false;
            try
            {
                var settings = _settings();
                var previous = ClipboardService.ReadClipboardText();
                var sequence = Native.GetClipboardSequenceNumber();
                var captured = false;

                // The user is still holding the trigger modifier; release the logical
                // modifier state before synthesizing Ctrl+C, otherwise apps see
                // Ctrl+Shift+C / Ctrl+Alt+C and do not copy.
                var modifierVk = settings.ContextModifier switch
                {
                    "Ctrl" => Native.VK_CONTROL,
                    "Alt" => Native.VK_MENU,
                    "Shift" => Native.VK_SHIFT,
                    _ => 0
                };
                for (var i = 0; i < 30 && modifierVk != 0 && Native.IsKeyDown(modifierVk); i++)
                    await Task.Delay(10);
                await Task.Delay(20);

                if (UserChangedSelection())
                {
                    App.Log("right click: left mouse activity after intercept, cancel capture");
                    return;
                }

                if (settings.AutoCopySelection)
                {
                    var fg = Native.GetForegroundWindow();
                    Native.GetWindowThreadProcessId(fg, out var fgPid);
                    var ctrlC = Native.SendCtrlC();
                    App.Log($"SendCtrlC -> {ctrlC} (size={Marshal.SizeOf<Native.INPUT>()}, fg={fg}, fgPid={fgPid}, " +
                            $"shiftDown={Native.IsKeyDown(Native.VK_SHIFT)})");
                    // 有选中文字时剪贴板通常很快变化；没选中时不要长等，否则右键反应会明显变慢。
                    for (var i = 0; i < 10; i++)
                    {
                        await Task.Delay(15);
                        if (Native.GetClipboardSequenceNumber() != sequence)
                        {
                            captured = true;
                            break;
                        }
                    }

                    if (!captured)
                    {
                        Native.SendCtrlInsert();
                        for (var i = 0; i < 5; i++)
                        {
                            await Task.Delay(15);
                            if (Native.GetClipboardSequenceNumber() != sequence)
                            {
                                captured = true;
                                break;
                            }
                        }
                    }
                }

                // 用户在取词过程中又用左键重新选择了文字：这次右键的选中状态已失效，
                // 既不要弹窗，也不要补发右键菜单。
                if (UserChangedSelection())
                {
                    App.Log("right click: selection changed during capture, cancel");
                    return;
                }

                var text = captured ? ClipboardService.ReadClipboardText() : null;
                if (!captured || string.IsNullOrWhiteSpace(text))
                {
                    // 光标处没有选中文字：不弹 cooopy 窗口，把这次右键补发给目标程序，
                    // 让它弹出自己的右键菜单。
                    App.Log("right click: no selected text, replay context menu");
                    Native.SetCursorPos(point.X, point.Y);
                    _replayRight = true;
                    Native.SendRightClick();
                    await Task.Delay(60);
                    _replayRight = false;
                    return;
                }

                App.Log($"right click capture: captured={captured}, length={text.Length}");
                _showPopup(text, true, point, previous);
            }
            catch (Exception ex)
            {
                Notice?.Invoke("右键取词失败：" + ex.Message);
            }
            finally
            {
                _capturing = false;
            }
        }

        private bool UserChangedSelection() =>
            _leftActivityDuringCapture || Native.IsKeyDown(0x01); // VK_LBUTTON

        public void Dispose()
        {
            Uninstall();
            GC.SuppressFinalize(this);
        }
    }
}
