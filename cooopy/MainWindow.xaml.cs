using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Cooopy.Models;
using Cooopy.Services;
using Cooopy.Windows;
using Microsoft.Win32;

namespace Cooopy
{
    public partial class MainWindow : Window
    {
        private readonly DataStore _store;
        private readonly ClipboardService _clipboard;
        private readonly HotkeyService _hotkeys = new();
        private readonly HashSet<string> _filterTagIds = new();
        private readonly DispatcherTimer _toastTimer;
        private List<TagFilterOption> _filterOptions = new();

        private TrayService _tray;
        private TagPopupWindow _popup;
        private HwndSource _source;
        private IntPtr _hwnd;
        private IntPtr _hotkeyHook;
        private Native.HookProc _hotkeyHookProc;
        private bool _hotkeyApplyPending;
        private bool _initialized;
        private bool _exiting;
        private bool _sessionEnding;
        private int _taskbarCreatedMsg;
        private bool _loadingSettings;
        private sealed class UndoEntry
        {
            public RecordItem Record;
            public TagItem Tag;
            public List<string> TagRecordIds;
            public bool TagWasFiltered;
        }

        private readonly List<UndoEntry> _undoStack = new();
        private RecordEditorWindow _recordEditor;
        private TagEditorWindow _tagEditor;
        private readonly DeepSeekService _deepSeek = new();
        private readonly List<DeepSeekService.ChatMessage> _chatHistory = new();
        private DispatcherTimer _searchTimer;
        private DispatcherTimer _pauseTimer;
        private bool _chatBusy;
        private DispatcherTimer _aiCheckTimer;
        private bool _aiChecking;
        private bool _aiLineOk;
        private string _aiCheckReason = "请先填写 API 地址和 API Key";
        private bool _suppressAdvancedToggle;
        private string _aiCheckedSignature;
        private double _noteWindowWidth = 1000;
        private string _appliedUiMode;
        private string _inlineEditId;
        private RecordRow _inlineEditRow;
        private bool _clampingBubbleScroll;
        private bool _recordsRefreshPending;

        /// <summary>私密记录未解锁时显示的占位文本（固定 8 个星号，不泄露原文长度）。</summary>
        private const string LockedPlaceholder = "🔒 ********";

        /// <summary>最近一次取词时的目标窗口（连续取词时把焦点还给它的凭据）。</summary>
        private IntPtr _lastTargetWindow;

        private DispatcherTimer _lockTimer;
        private bool _wasUnlocked;

        public MainWindow(DataStore store)
        {
            _store = store;
            InitializeComponent();

            // 恢复上次的窗口位置/尺寸；没有有效记录（首次运行/分辨率变化）则居中。
            var settings0 = store.Data.Settings;
            var work = SystemParameters.WorkArea;
            WindowStartupLocation = WindowStartupLocation.Manual;
            if (settings0.WinWidth >= MinWidth && settings0.WinHeight >= MinHeight)
            {
                Width = settings0.WinWidth;
                Height = Math.Max(MinHeight, Math.Min(settings0.WinHeight, work.Height + 40));
                Left = settings0.WinLeft;
                Top = settings0.WinTop;
                // 防止分辨率变化后窗口落在屏幕外：越界时拉回并回退为居中。
                var vsLeft = SystemParameters.VirtualScreenLeft;
                var vsTop = SystemParameters.VirtualScreenTop;
                var vsRight = vsLeft + SystemParameters.VirtualScreenWidth;
                var vsBottom = vsTop + SystemParameters.VirtualScreenHeight;
                if (Left < vsLeft || Top < vsTop || Left + Width > vsRight || Top + Height > vsBottom)
                {
                    Left = work.Left + Math.Max(0, (work.Width - Width) / 2);
                    Top = work.Top + Math.Max(0, (work.Height - Height) / 2);
                }
                if (settings0.WinWidth > 520) _noteWindowWidth = settings0.WinWidth;
            }
            else
            {
                Width = Math.Max(MinWidth, Math.Min(Width, work.Width - 60));
                Height = Math.Max(MinHeight, Math.Min(Height, work.Height - 48));
                Left = work.Left + (work.Width - Width) / 2;
                Top = work.Top + (work.Height - Height) / 2;
            }

            _clipboard = new ClipboardService(store);
            _clipboard.Captured += _ =>
            {
                if (RecordsPanel != null && RecordsPanel.Visibility == Visibility.Visible)
                    RefreshRecords();
                else if (TagsPanel != null && TagsPanel.Visibility == Visibility.Visible)
                    RefreshTags();
                UpdateHeader();
                UpdateDataStat();
            };

            _store.SaveFailed += OnSaveFailed;

            // 剪贴板服务的提示（复制失败、身份证号被打码）统一转成 toast。
            _clipboard.Suppressed += message =>
                Dispatcher.BeginInvoke(new Action(() => ShowToast(message)));

            _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.2) };
            _toastTimer.Tick += (_, _) =>
            {
                _toastTimer.Stop();
                var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(180));
                ToastBorder.BeginAnimation(OpacityProperty, fade);
            };

            // 输入搜索词时不必每按一键就重建整张列表；轻微延迟让输入更顺滑。
            _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
            _searchTimer.Tick += (_, _) =>
            {
                _searchTimer.Stop();
                if (_initialized) RefreshRecords();
            };

            // 私密记录的解锁会话到期后，把列表重新打回掩码状态。
            _lockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            _lockTimer.Tick += (_, _) =>
            {
                var unlockedNow = PrivacyGate.IsUnlocked;
                if (unlockedNow == _wasUnlocked) return;
                _wasUnlocked = unlockedNow;
                if (_initialized) RefreshRecords();
            };
            _lockTimer.Start();

            // 暂停 30 分钟到期后自动恢复界面/托盘状态。
            _pauseTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            _pauseTimer.Tick += (_, _) =>
            {
                if (_store.Data.Settings.Paused) return;
                _pauseTimer.Stop();
                UpdatePauseUi();
            };

            _aiCheckTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
            _aiCheckTimer.Tick += async (_, _) =>
            {
                _aiCheckTimer.Stop();
                await CheckAiLineAsync();
            };

            PreviewKeyDown += Window_PreviewKeyDown;
            PreviewMouseDown += Window_PreviewMouseDown;
            Deactivated += (_, _) =>
            {
                if (FilterPopup == null || !FilterPopup.IsOpen) return;
                // Clicking inside the popup can briefly deactivate the main window; keep it open then.
                if (FilterPopup.Child is UIElement popupChild && popupChild.IsKeyboardFocusWithin) return;
                FilterPopup.IsOpen = false;
            };
            IsVisibleChanged += (_, _) => { if (!IsVisible && FilterPopup != null) FilterPopup.IsOpen = false; };
            // 笔记模式下用户手动调宽窗口时同步记忆值，避免后续 ApplyUiMode 把宽度拽回去。
            SizeChanged += (_, _) =>
            {
                if (_appliedUiMode == "note" && WindowState == WindowState.Normal)
                    _noteWindowWidth = Width;
            };
            Loaded += (_, _) => { if (!_initialized) InitServices(); };
        }

        public void InitializeApp(bool show)
        {
            new WindowInteropHelper(this).EnsureHandle();
            InitServices();
            if (show) ShowMain();
        }

        // ------------------------------------------------------------- custom window chrome

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                ToggleMaximize();
                e.Handled = true;
                return;
            }

            if (e.ButtonState == MouseButtonState.Pressed)
            {
                try
                {
                    DragMove();
                }
                catch
                {
                    // ignored (mouse released mid-drag)
                }
            }
        }

        private void MinButton_Click(object sender, RoutedEventArgs e) =>
            WindowState = WindowState.Minimized;

        private void MaxButton_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

        private void TopMostButton_Click(object sender, RoutedEventArgs e) =>
            ApplyAlwaysOnTop(!Topmost, persist: true);

        /// <summary>切换主界面窗口置顶，并同步标题栏按钮的激活外观。</summary>
        private void ApplyAlwaysOnTop(bool on, bool persist)
        {
            Topmost = on;
            if (TopMostButton != null)
            {
                TopMostButton.Foreground = on
                    ? TryFindResource("Accent") as Brush ?? Brushes.DodgerBlue
                    : TryFindResource("TextSecondary") as Brush ?? Brushes.Gray;
                TopMostButton.ToolTip = on ? "取消窗口置顶" : "窗口置顶";
            }
            if (persist)
                _store.UpdateSettings(s => s.AlwaysOnTop = on);
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

        private void ToggleMaximize() =>
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;

        private void Window_StateChanged(object sender, EventArgs e)
        {
            if (MaxIcon == null) return;
            MaxIcon.Data = WindowState == WindowState.Maximized
                ? Geometry.Parse("M 2 4 H 10 V 12 H 2 Z M 4 4 V 2 H 12 V 10 H 10")
                : Geometry.Parse("M 2 2 H 12 V 12 H 2 Z");
            if (MaxButton != null)
                MaxButton.ToolTip = WindowState == WindowState.Maximized ? "还原" : "最大化";

            // WindowChrome needs the content inset while maximized so nothing hides under the screen edge.
            if (RootBorder != null)
            {
                var inset = WindowState == WindowState.Maximized ? 7 : 0;
                RootBorder.Margin = new Thickness(inset);
                RootBorder.BorderThickness = WindowState == WindowState.Maximized
                    ? new Thickness(0)
                    : new Thickness(1);
            }
        }

        private void InitServices()
        {
            if (_initialized) return;
            _initialized = true;

            try
            {
                _hwnd = new WindowInteropHelper(this).Handle;
                App.Log("InitServices: hwnd=" + _hwnd);
                _source = HwndSource.FromHwnd(_hwnd);
                _source?.AddHook(WndProc);
                // Explorer 重启后任务栏重建，托盘图标会被一并清掉，需重新显示。
                _taskbarCreatedMsg = (int)Native.RegisterWindowMessage("TaskbarCreated");
                // 系统注销/关机：直接走真退出，确保托盘图标被清理。
                Application.Current.SessionEnding += (_, _) => _sessionEnding = true;

                _clipboard.Attach(_hwnd);

                _hotkeys.Attach(_hwnd);
                ApplyHotkeys();

                _tray = new TrayService();
                _tray.ShowMainRequested += ShowMain;
                _tray.QuickSaveRequested += QuickSaveFromClipboard;
                _tray.PauseToggleRequested += TogglePause;
                _tray.SettingsRequested += () =>
                {
                    NavSettings.IsChecked = true;
                    ShowMain();
                };
                _tray.ExitRequested += ExitApp;
                App.Log("tray created");

                // 低完整性级别运行时（例如被某些启动器拉起）会写不进数据目录：
                // 记录照常能加进内存，但一关窗口就全丢。启动时就说清楚，别让用户以为已经存下来了。
                if (!_store.CanWriteDataDir())
                {
                    App.Log("data dir NOT writable: " + _store.DataDir);
                    ShowToast("数据目录不可写，新增记录关闭后会丢");
                    _tray.ShowBalloon("cooopy 无法保存数据",
                        $"数据目录不可写：{_store.DataDir}\n请关闭 cooopy，然后从资源管理器双击 cooopy.exe 重新启动。",
                        8000);
                }

                LoadSettingsToUi();
                RefreshAll();
                App.Log("InitServices done");
            }
            catch (Exception ex)
            {
                App.Log("InitServices failed: " + ex);
                MessageBox.Show(ex.Message, "cooopy 初始化失败", MessageBoxButton.OK, MessageBoxImage.Error);
                throw;
            }
        }

        // ------------------------------------------------------------- window plumbing

        private void Window_SourceInitialized(object sender, EventArgs e) => InitServices();

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            switch (msg)
            {
                case Native.WM_CLIPBOARDUPDATE:
                    try
                    {
                        _clipboard.OnClipboardUpdate();
                    }
                    catch (Exception ex)
                    {
                        App.Log("clipboard update failed: " + ex);
                    }
                    break;
                case Native.WM_HOTKEY:
                    var id = wParam.ToInt32();
                    if (id == HotkeyService.IdMain) ToggleMainWindow();
                    else if (id == HotkeyService.IdSave) ShowTagPopupFromClipboard();
                    break;
                default:
                    if (_taskbarCreatedMsg != 0 && msg == _taskbarCreatedMsg)
                        _tray?.EnsureVisible();
                    break;
            }

            return IntPtr.Zero;
        }

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            if (_exiting) return;
            // 系统注销/关机时必须真退出并清理托盘，否则被系统硬杀会留下幽灵图标。
            if (_sessionEnding) return;
            if (_store.Data.Settings.CloseToTray)
            {
                e.Cancel = true;
                Hide();
            }
            else
            {
                _exiting = true;
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            Cleanup();
            base.OnClosed(e);
            Application.Current?.Shutdown();
        }

        private void Cleanup()
        {
            // 先落盘，避免某个窗口/托盘释放异常导致数据没保存。
            try
            {
                _store.FlushPending();
            }
            catch
            {
                // ignored
            }

            // 记住窗口位置/尺寸（卡片模式 360 宽是固定值，同样记录）。
            try
            {
                if (WindowState == WindowState.Normal && !double.IsNaN(Left) && Width > 0)
                {
                    _store.UpdateSettings(s0 =>
                    {
                        s0.WinLeft = Left;
                        s0.WinTop = Top;
                        s0.WinWidth = Width;
                        s0.WinHeight = Height;
                    });
                }
            }
            catch
            {
                // ignored
            }

            try
            {
                if (_hotkeyHook != IntPtr.Zero)
                {
                    Native.UnhookWindowsHookEx(_hotkeyHook);
                    _hotkeyHook = IntPtr.Zero;
                }
                _hotkeys.Detach();
                if (_hwnd != IntPtr.Zero) _clipboard.Detach(_hwnd);
                _toastTimer?.Stop();
                _searchTimer?.Stop();
                _pauseTimer?.Stop();
                _tray?.Dispose();
                _popup?.Close();
                _recordEditor?.Close();
                _tagEditor?.Close();
                _deepSeek?.Dispose();
            }
            catch
            {
                // ignored
            }
        }

        public void ShowFromExternal()
        {
            if (!_initialized) InitServices();
            ShowMain();
        }

        private void ShowMain()
        {
            if (!IsVisible) Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
            Topmost = true;
            Topmost = false;
        }

        private void ToggleMainWindow()
        {
            if (IsVisible && IsActive) Hide();
            else ShowMain();
        }

        private void ExitApp()
        {
            _exiting = true;
            Close();
        }

        // ------------------------------------------------------------- hotkeys

        private void ApplyHotkeys()
        {
            var settings = _store.Data.Settings;

            // Win 键已不再支持：历史配置里存过 Win 组合的，直接回落默认，
            // 否则会注册失败，用户只会看到一句“格式无效”。
            if (!IsHotkeyUsable(settings.HotkeyMain) || !IsHotkeyUsable(settings.HotkeySave))
            {
                _store.UpdateSettings(s =>
                {
                    if (!IsHotkeyUsable(s.HotkeyMain)) s.HotkeyMain = "Ctrl+Alt+C";
                    if (!IsHotkeyUsable(s.HotkeySave)) s.HotkeySave = "Ctrl+Alt+S";
                });

                if (HotkeyMainBox != null) HotkeyMainBox.Text = settings.HotkeyMain;
                if (HotkeySaveBox != null) HotkeySaveBox.Text = settings.HotkeySave;
                App.Log($"hotkeys migrated to defaults: main={settings.HotkeyMain} save={settings.HotkeySave}");
                ShowToast("Win 键组合已不再支持，已恢复默认快捷键");
            }

            (bool ok, string error) main = (true, null);
            if (settings.HotkeyMainEnabled) main = _hotkeys.Apply(HotkeyService.IdMain, settings.HotkeyMain);
            else _hotkeys.Disable(HotkeyService.IdMain);

            (bool ok, string error) save = (true, null);
            if (settings.HotkeySaveEnabled) save = _hotkeys.Apply(HotkeyService.IdSave, settings.HotkeySave);
            else _hotkeys.Disable(HotkeyService.IdSave);

            App.Log($"hotkeys: main={settings.HotkeyMain}/{main.ok}/enabled={settings.HotkeyMainEnabled} " +
                    $"save={settings.HotkeySave}/{save.ok}/enabled={settings.HotkeySaveEnabled}");

            if (settings.HotkeyMainEnabled && !main.ok) ShowToast(main.error);
            else if (settings.HotkeySaveEnabled && !save.ok) ShowToast(save.error);
        }

        private void HotkeyEnabled_Changed(object sender, RoutedEventArgs e)
        {
            if (_loadingSettings) return;
            UpdateHotkeyEnableUi();
            PersistSettings();
            ApplyHotkeys();
        }

        private void UpdateHotkeyEnableUi()
        {
            var mainEnabled = ChkHotkeyMainEnabled?.IsChecked == true;
            var saveEnabled = ChkHotkeySaveEnabled?.IsChecked == true;

            if (HotkeyMainBox != null) HotkeyMainBox.IsEnabled = mainEnabled;
            if (HotkeyMainResetButton != null) HotkeyMainResetButton.IsEnabled = mainEnabled;
            if (HotkeySaveBox != null) HotkeySaveBox.IsEnabled = saveEnabled;
            if (HotkeySaveResetButton != null) HotkeySaveResetButton.IsEnabled = saveEnabled;

            // 关掉开关时，如果输入框正在录制，结束录制状态。
            if (!mainEnabled && HotkeyMainBox != null && HotkeyMainBox.IsKeyboardFocusWithin)
                Keyboard.ClearFocus();
            if (!saveEnabled && HotkeySaveBox != null && HotkeySaveBox.IsKeyboardFocusWithin)
                Keyboard.ClearFocus();
            UpdateHotkeyHook();
        }

        /// <summary>组合键是否还能被注册：必须带 Ctrl / Shift / Alt，且不含 Win。</summary>
        private static bool IsHotkeyUsable(string combo) => HotkeyService.Parse(combo) != null;

        private void HotkeyBox_GotFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox box) box.SelectAll();
            UpdateHotkeyHook();
        }

        private void HotkeyBox_LostFocus(object sender, RoutedEventArgs e) => UpdateHotkeyHook();

        /// <summary>
        /// 录制快捷键时使用低级键盘钩子：这样组合键不会被输入框/系统先吃掉，
        /// 并且在录制期间屏蔽系统的默认处理。Win 键不再是合法修饰键，按下会被吞掉并提示。
        /// </summary>
        private void UpdateHotkeyHook()
        {
            var recordingMain = HotkeyMainBox != null && HotkeyMainBox.IsKeyboardFocusWithin;
            var recordingSave = HotkeySaveBox != null && HotkeySaveBox.IsKeyboardFocusWithin;
            var recording = recordingMain || recordingSave;

            if (recording)
            {
                // 暂时注销正在录制的那个热键：否则按“当前组合”想重新录制时，
                // 全局热键会先触发（打开主窗口/取词弹窗），把录制打断。
                if (recordingMain) _hotkeys.Disable(HotkeyService.IdMain);
                if (recordingSave) _hotkeys.Disable(HotkeyService.IdSave);

                if (_hotkeyHook != IntPtr.Zero) return;
                ResetHotkeyModifierState();
                _hotkeyHookProc = HotkeyHookCallback;
                _hotkeyHook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _hotkeyHookProc,
                    Native.GetModuleHandle(null), 0);
                App.Log("hotkey recording hook installed: " + (_hotkeyHook != IntPtr.Zero));
            }
            else
            {
                ResetHotkeyModifierState();
                if (_hotkeyHook == IntPtr.Zero) return;
                Native.UnhookWindowsHookEx(_hotkeyHook);
                _hotkeyHook = IntPtr.Zero;
                _hotkeyHookProc = null;
                // 离开录制：按当前设置恢复注册（录了新组合就生效，没录就恢复原组合）。
                ApplyHotkeys();
            }
        }

        private IntPtr HotkeyHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (nCode < 0) return Native.CallNextHookEx(_hotkeyHook, nCode, wParam, lParam);

                var message = wParam.ToInt32();
                var isDown = message == Native.WM_KEYDOWN || message == Native.WM_SYSKEYDOWN;
                var isUp = message == Native.WM_KEYUP || message == Native.WM_SYSKEYUP;
                if (!isDown && !isUp) return Native.CallNextHookEx(_hotkeyHook, nCode, wParam, lParam);

                var data = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
                var vk = (int)data.vkCode;

                // 自己维护修饰键状态：录制期间按键被屏蔽，GetAsyncKeyState 不一定可靠。
                if (TrackHotkeyModifier(vk, isDown)) return new IntPtr(1);
                if (!isDown) return new IntPtr(1);

                var modifiers = TrackedHotkeyModifiers();
                if (modifiers == ModifierKeys.None) return new IntPtr(1);

                var combo = HotkeyService.FromVirtualKey(vk, modifiers);
                if (string.IsNullOrEmpty(combo)) return new IntPtr(1);

                if (!_hotkeyApplyPending)
                {
                    _hotkeyApplyPending = true;
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        _hotkeyApplyPending = false;
                        var box = HotkeyMainBox.IsKeyboardFocusWithin ? HotkeyMainBox
                            : HotkeySaveBox.IsKeyboardFocusWithin ? HotkeySaveBox
                            : null;
                        if (box != null) ApplyRecordedHotkey(box, combo);
                    }));
                }

                return new IntPtr(1);
            }
            catch (Exception ex)
            {
                App.Log("hotkey hook error: " + ex.Message);
                return Native.CallNextHookEx(_hotkeyHook, nCode, wParam, lParam);
            }
        }

        private bool _hkCtrl;
        private bool _hkAlt;
        private bool _hkShift;
        private bool _winKeyNotified;

        private bool TrackHotkeyModifier(int vk, bool down)
        {
            switch (vk)
            {
                case 0x11:
                case 0xA2:
                case 0xA3:
                    _hkCtrl = down;
                    return true;
                case 0x10:
                case 0xA0:
                case 0xA1:
                    _hkShift = down;
                    return true;
                case 0x12:
                case 0xA4:
                case 0xA5:
                    _hkAlt = down;
                    return true;
                // Win 键不再是合法修饰键：按键本身吞掉（不让开始菜单弹出来），
                // 但不计入修饰键，并提示一次，否则用户会以为录制没反应。
                case 0x5B:
                case 0x5C:
                    if (down && !_winKeyNotified)
                    {
                        _winKeyNotified = true;
                        Dispatcher.BeginInvoke(new Action(() =>
                            ShowToast("不支持 Win 键，请用 Ctrl / Shift / Alt 开头")));
                    }
                    return true;
                default:
                    return false;
            }
        }

        private ModifierKeys TrackedHotkeyModifiers()
        {
            var modifiers = ModifierKeys.None;
            if (_hkCtrl) modifiers |= ModifierKeys.Control;
            if (_hkAlt) modifiers |= ModifierKeys.Alt;
            if (_hkShift) modifiers |= ModifierKeys.Shift;
            return modifiers;
        }

        private void ResetHotkeyModifierState()
        {
            _hkCtrl = false;
            _hkAlt = false;
            _hkShift = false;
            _winKeyNotified = false;
        }

        private void ApplyRecordedHotkey(TextBox box, string combo)
        {
            var isMain = ReferenceEquals(box, HotkeyMainBox);
            var id = isMain ? HotkeyService.IdMain : HotkeyService.IdSave;
            var result = _hotkeys.Apply(id, combo);
            if (!result.ok)
            {
                App.Log($"hotkey record failed: {id} {combo} - {result.error}");
                ShowToast(result.error);
                return;
            }

            box.Text = combo;
            App.Log($"hotkey recorded: {id} = {combo}");
            _store.UpdateSettings(s =>
            {
                if (isMain) s.HotkeyMain = combo;
                else s.HotkeySave = combo;
            });
            ShowToast("快捷键已更新为 " + combo);

            // 录制成功后立刻把焦点移出输入框：录制钩子还挂着时会吞掉所有按键，
            // 用户紧接着按新快捷键会被当成“再次录制”而不是触发热键，
            // 看起来就是“要在外面点一下才生效”。移出焦点 = 钩子卸载 = 立即可用。
            box.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
        }

        // 低级键盘钩子不可用时的兜底：普通 WPF 按键事件（不含 Win 键）。
        private void HotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (_hotkeyHook != IntPtr.Zero) return;
            e.Handled = true;
            var box = sender as TextBox;
            if (box == null) return;

            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            var modifiers = Keyboard.Modifiers;

            var combo = HotkeyService.FromKeyEvent(key, modifiers);
            if (combo == null || !combo.Contains('+')) return;
            ApplyRecordedHotkey(box, combo);
        }

        // ------------------------------------------------------------- clipboard / quick save

        private void QuickSaveFromClipboard()
        {
            var text = ClipboardService.ReadClipboardText();
            if (string.IsNullOrWhiteSpace(text))
            {
                ShowToast("剪贴板里没有文本");
                return;
            }

            Native.GetCursorPos(out var point);
            ShowTagPopupInternal(text, point);
        }

        /// <summary>
        /// “cooopy 到剪贴板”快捷键：效果等同帮用户按了一次 Ctrl+C，然后弹出打标签窗口。
        ///   有选中文字 → 选中内容进剪贴板，弹窗带入这段文字；
        ///   没选中 → 剪贴板不变，弹窗带入剪贴板现有文本；
        ///   剪贴板也没有文本 → 照样弹窗，文本框空置，可直接输入。
        /// 两种情况不合成按键：
        ///   传统控制台（cmd/conhost）——那里 Ctrl+C 是“中断”，会打断正在跑的命令；
        ///   管理员窗口——UIPI 会静默丢弃普通进程合成的按键，发了也白发。
        /// 两种情况都退化为只读剪贴板。
        /// </summary>
        private bool _captureInFlight;

        private async void ShowTagPopupFromClipboard()
        {
            if (_captureInFlight) return; // 连按快捷键时忽略上一轮还没完成的取词
            _captureInFlight = true;
            var started = Stopwatch.StartNew();
            try
            {
                var fgStart = WindowClassOf(Native.GetForegroundWindow());
                var previous = ClipboardService.ReadClipboardText() ?? string.Empty;
                Native.GetCursorPos(out var point);

                // 弹窗不抢焦点（WS_EX_NOACTIVATE），目标程序的焦点和选中状态都保持原样，
                // 所以先立即弹窗（填当前剪贴板内容），复制选中在后台进行，拿到后更新弹窗。
                ShowTagPopupInternal(previous, point);

                var foreground = Native.GetForegroundWindow();
                var ownForeground = IsOwnWindow(foreground);
                if (!ownForeground) _lastTargetWindow = foreground;

                var hwnd = ownForeground && _lastTargetWindow != IntPtr.Zero && Native.IsWindow(_lastTargetWindow)
                    ? _lastTargetWindow
                    : foreground;
                var console = IsLegacyConsole(hwnd);
                var elevated = Native.IsForegroundElevated(hwnd, out _);
                var fgClass = WindowClassOf(hwnd);

                if (console || elevated)
                {
                    // 传统控制台（Ctrl+C 是中断）/ 管理员窗口（UIPI 拦截按键与消息）：
                    // 不做任何复制尝试，弹窗保留剪贴板现有内容。
                    if (elevated && !console)
                        ShowToast("该窗口以管理员运行，无法自动复制选中文字");
                    App.Log($"hotkey popup: copied=False, skipped, console={console}, elevated={elevated}, " +
                            $"fg='{fgClass}', elapsed={started.ElapsedMilliseconds}ms");
                    return;
                }

                // 连续取词：弹窗开着时若用户点过弹窗内部，焦点在 cooopy 自己这里，
                // 复制要发给上次的目标窗口，先把焦点还回去。
                if (ownForeground && _lastTargetWindow != IntPtr.Zero && Native.IsWindow(_lastTargetWindow))
                {
                    Native.SetForegroundWindow(_lastTargetWindow);
                    for (var i = 0; i < 10; i++)
                    {
                        await Task.Delay(30);
                        if (Native.GetForegroundWindow() == _lastTargetWindow) break;
                    }
                }

                var sequence = Native.GetClipboardSequenceNumber();
                var copied = false;

                // 快路径：WM_COPY 是窗口消息不是按键，不受按着的修饰键影响，立即生效。
                Native.CopyFromFocusedWindow();
                for (var i = 0; i < 8; i++)
                {
                    await Task.Delay(10);
                    if (Native.GetClipboardSequenceNumber() != sequence)
                    {
                        copied = true;
                        break;
                    }
                }

                // 兜底：等修饰键松开（最多 300ms）后合成按键（覆盖不响应 WM_COPY 的程序）。
                // 修饰键没松开就绝不合成：用户按着 Alt 时合成 Ctrl+C 会变成 Ctrl+Alt+C，
                // 正好命中“显示主界面”的全局热键，导致主界面被意外召唤。
                if (!copied)
                {
                    var modifiersReleased = false;
                    for (var i = 0; i < 12; i++)
                    {
                        if (!Native.IsKeyDown(Native.VK_CONTROL) &&
                            !Native.IsKeyDown(Native.VK_MENU) &&
                            !Native.IsKeyDown(Native.VK_SHIFT) &&
                            !Native.IsKeyDown(Native.VK_LWIN) &&
                            !Native.IsKeyDown(Native.VK_RWIN))
                        {
                            modifiersReleased = true;
                            break;
                        }
                        await Task.Delay(25);
                    }
                    if (modifiersReleased)
                    {
                        // SendCtrlC 内部有按键间隔 Sleep，放到线程池执行，避免阻塞 UI 线程。
                        await Task.Run(Native.SendCtrlC);
                        for (var i = 0; i < 10; i++)
                        {
                            await Task.Delay(10);
                            if (Native.GetClipboardSequenceNumber() != sequence)
                            {
                                copied = true;
                                break;
                            }
                        }
                    }
                }

                if (copied)
                {
                    var text = ClipboardService.ReadClipboardText() ?? string.Empty;
                    _popup?.UpdateTextIfUntouched(text);
                }
                App.Log($"hotkey popup: copied={copied}, console={console}, elevated={elevated}, " +
                        $"fg='{fgClass}', elapsed={started.ElapsedMilliseconds}ms");
            }
            catch (Exception ex)
            {
                App.Log("hotkey capture failed: " + ex);
                ShowToast("取词失败：" + ex.Message);
            }
            finally
            {
                _captureInFlight = false;
            }
        }

        private static bool IsOwnWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return false;
            Native.GetWindowThreadProcessId(hwnd, out var pid);
            return pid == (uint)Environment.ProcessId;
        }

        /// <summary>
        /// 前台窗口是否为传统控制台（conhost）。Windows Terminal（CASCADIA_HOSTING_WINDOW_CLASS）
        /// 在有选中时 Ctrl+C 本来就是复制，不在此列。
        /// </summary>
        private static bool IsLegacyConsole(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return false;
            var sb = new System.Text.StringBuilder(64);
            _ = Native.GetClassName(hwnd, sb, sb.Capacity);
            return string.Equals(sb.ToString(), "ConsoleWindowClass", StringComparison.Ordinal);
        }

        private static string WindowClassOf(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return "<null>";
            var sb = new System.Text.StringBuilder(64);
            _ = Native.GetClassName(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }

        private void ShowTagPopupInternal(string text, Native.POINT point)
        {
            var counts = CountActiveRecordsByTag();
            var tags = _store.Data.Tags
                .OrderByDescending(t => counts.TryGetValue(t.Id, out var c) ? c : 0)
                .ToList();

            // 超长文本会把弹窗文本框卡住数秒（实测 170 万字符 ≈ 7 秒），按设置上限截断显示。
            var max = Math.Max(1000, _store.Data.Settings.MaxLength);
            if (text != null && text.Length > max)
            {
                text = text[..max];
                ShowToast("内容过长，弹窗内已截断显示");
            }

            // 弹窗里展示剪贴板原文：是否打码取决于保存时有没有勾“私密”，
            // 所以这里不预先打码，交给 HandleQuickSave 决定。

            // 复用同一个弹窗实例：窗口和模板只创建一次，后续取词弹窗会快很多。
            if (_popup == null)
            {
                _popup = new TagPopupWindow();
                _popup.Saved += (editedText, tagIds) => HandleQuickSave(editedText, tagIds);
                _popup.Hidden += StopPopupMouseHook;
                _popup.Closed += (_, _) => StopPopupMouseHook();
            }

            _popup.Reset(text, tags, _store.Data.Settings.QuickTagAutoSave);
            _popup.ShowAtScreenPoint(point);
            StartPopupMouseHook();
        }

        // ---- 弹窗外部点击监视：弹窗不抢焦点后，Deactivated 不再触发，
        // ---- 改用低级鼠标钩子判断“点在弹窗外面”，驱动自动保存/关闭。

        private IntPtr _popupMouseHook;
        private Native.HookProc _popupMouseHookProc;

        private void StartPopupMouseHook()
        {
            if (_popupMouseHook != IntPtr.Zero) return;
            _popupMouseHookProc = PopupMouseHookCallback;
            _popupMouseHook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _popupMouseHookProc,
                Native.GetModuleHandle(null), 0);
        }

        private void StopPopupMouseHook()
        {
            if (_popupMouseHook == IntPtr.Zero) return;
            Native.UnhookWindowsHookEx(_popupMouseHook);
            _popupMouseHook = IntPtr.Zero;
            _popupMouseHookProc = null;
        }

        private IntPtr PopupMouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                var message = wParam.ToInt32();
                if (message == Native.WM_LBUTTONDOWN || message == Native.WM_RBUTTONDOWN)
                {
                    var point = new Native.POINT();
                    Native.GetCursorPos(out point);
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (_popup == null || !_popup.IsVisible) { StopPopupMouseHook(); return; }
                        if (_popup.IsOutside(point)) _popup.OutsideClick();
                    }));
                }
            }
            return Native.CallNextHookEx(_popupMouseHook, nCode, wParam, lParam);
        }

        private void HandleQuickSave(string text, IList<string> tagIds)
        {
            // 勾了“私密”就按用户意愿原样保存（私密记录本身就是靠验证保护全文）；
            // 否则照常给身份证/银行卡打码，保证任何入口都不落明文。
            var keepVerbatim = tagIds != null && tagIds.Any(BuiltinTags.IsPrivate);
            if (!keepVerbatim && _store.Data.Settings.MaskIdCards)
            {
                text = SensitiveText.MaskAll(text, out var idCards, out var bankCards);
                var notice = SensitiveText.DescribeMask(idCards, bankCards);
                if (notice != null) ShowToast(notice);
            }

            // AddRecord 对空文本 / 超长文本返回 null 且不产生记录，
            // 这里必须区分“没存进去”和“存进去了”，否则空弹窗点保存会假报“已添加”。
            if (string.IsNullOrWhiteSpace(text))
            {
                ShowToast("内容为空，未保存");
                return;
            }
            if (text.Length > Math.Max(1000, _store.Data.Settings.MaxLength))
            {
                ShowToast("内容超长，未保存");
                return;
            }

            var record = _store.AddRecord(text, tagIds);
            if (record != null && tagIds != null && tagIds.Count > 0)
            {
                var changed = false;
                foreach (var id in tagIds)
                {
                    if (!record.TagIds.Contains(id))
                    {
                        record.TagIds.Add(id);
                        changed = true;
                    }
                }
                if (changed)
                {
                    _store.NormalizeRecordTags(record);
                    _store.SaveNow();
                }
            }

            var names = tagIds?
                .Select(id => _store.FindTag(id)?.Name)
                .Where(name => !string.IsNullOrEmpty(name))
                .ToList() ?? new List<string>();
            var summary = names.Count == 0 ? "未打标签" : string.Join("、", names);
            if (IsVisible) ShowToast("已添加 · " + summary);
            else _tray?.ShowBalloon("已添加到 cooopy", summary);
            RefreshAll();
        }

        // ------------------------------------------------------------- navigation

        private void Nav_Checked(object sender, RoutedEventArgs e)
        {
            if (RecordsPanel == null) return;

            RecordsPanel.Visibility = Visibility.Collapsed;
            TagsPanel.Visibility = Visibility.Collapsed;
            AdvancedPanel.Visibility = Visibility.Collapsed;
            SettingsPanel.Visibility = Visibility.Collapsed;

            if (ReferenceEquals(sender, NavTags)) TagsPanel.Visibility = Visibility.Visible;
            else if (ReferenceEquals(sender, NavAdvanced))
            {
                AdvancedPanel.Visibility = Visibility.Visible;
                UpdateAdvancedState();
            }
            else if (ReferenceEquals(sender, NavSettings)) SettingsPanel.Visibility = Visibility.Visible;
            else RecordsPanel.Visibility = Visibility.Visible;

            if (TagsPanel.Visibility == Visibility.Visible) RefreshTags();
            else if (RecordsPanel.Visibility == Visibility.Visible && _initialized) RefreshRecords();
            if (SettingsPanel.Visibility == Visibility.Visible) LoadSettingsToUi();
        }

        private void ApplyUiMode()
        {
            if (ModeNoteRadio == null || RecordsBubbleList == null) return;

            var bubble = string.Equals(_store.Data.Settings.UiMode, "bubble", StringComparison.OrdinalIgnoreCase);
            var modeKey = bubble ? "bubble" : "note";
            // 只在模式真正切换时才调整窗口尺寸：ApplyUiMode 在每次设置变更时都会执行，
            // 若无条件重设宽度，用户手动调过的窗口会被强行拉回，表现为内容反复“抽搐”。
            var switching = _appliedUiMode != modeKey;
            _appliedUiMode = modeKey;

            if (bubble)
            {
                if (switching)
                {
                    if (WindowState == WindowState.Maximized) WindowState = WindowState.Normal;
                    if (Width > 520) _noteWindowWidth = Width;
                }
                // Bubble mode uses one fixed width (the current minimum width).
                MinWidth = 360;
                MaxWidth = 360;
                if (switching && Math.Abs(Width - 360) > 0.5) Width = 360;
            }
            else
            {
                if (switching)
                {
                    // 先放宽 MaxWidth、再设 Width、最后收紧 MinWidth：
                    // 若先设 MinWidth=780，窗口会被先钳到 780 再跳到目标宽度，出现两次回流。
                    MaxWidth = double.PositiveInfinity;
                    var target = Math.Max(_noteWindowWidth, 780);
                    if (Math.Abs(Width - target) > 0.5) Width = target;
                    MinWidth = 780;
                }
            }


            RecordsList.Visibility = bubble ? Visibility.Collapsed : Visibility.Visible;
            RecordsBubbleList.Visibility = bubble ? Visibility.Visible : Visibility.Collapsed;
            if (SettingsContent != null)
            {
                SettingsContent.Margin = bubble
                    ? new Thickness(14, 18, 14, 30)
                    : new Thickness(22, 18, 22, 30);
                // 笔记模式下钉死内容列宽：AI 线路检查（切换后 900ms）会触发全局重排，
                // 卡片里的换行提示文本可能被按旧约束重新测量，把整列内容拖窄一瞬；
                // 固定宽度后子元素的期望宽度变化不再影响整列。
                SettingsContent.Width = bubble ? double.NaN : 740;
            }

            // 卡片模式窗口窄，标题文字整体隐藏；笔记模式显示完整标题。
            if (AppLogo != null)
            {
                AppLogo.Text = "cooopy";
                AppLogo.Visibility = bubble ? Visibility.Collapsed : Visibility.Visible;
            }

            // Bubble mode uses compact icon-only top tabs.
            var tabStyle = (Style)FindResource(bubble ? "TopTabIcon" : "TopTab");
            if (NavRecords != null) NavRecords.Style = tabStyle;
            if (NavTags != null) NavTags.Style = tabStyle;
            if (NavSettings != null) NavSettings.Style = tabStyle;
            // AppLogo keeps the same X position in both modes (margin is set once in XAML).

            // Bubble mode uses icon-only top tabs: note / tag / gear.
            var iconVisibility = bubble ? Visibility.Visible : Visibility.Collapsed;
            var textVisibility = bubble ? Visibility.Collapsed : Visibility.Visible;
            foreach (var icon in new[] { NavRecordsIcon, NavTagsIcon, NavSettingsIcon })
                if (icon != null) icon.Visibility = iconVisibility;
            foreach (var text in new[] { NavRecordsText, NavTagsText, NavSettingsText })
                if (text != null) text.Visibility = textVisibility;
        }

        // ------------------------------------------------------------- records

        private void RefreshAll()
        {
            // 页面隐藏时不必重建列表；切回对应页面时会再刷新。
            if (RecordsPanel != null && RecordsPanel.Visibility == Visibility.Visible)
                RefreshRecords();
            if (TagsPanel != null && TagsPanel.Visibility == Visibility.Visible)
                RefreshTags();
            UpdateHeader();
            UpdateHideUntaggedButton();
            UpdateDataStat();
        }

        private void RefreshRecords()
        {
            var settings = _store.Data.Settings;
            var bubble = string.Equals(settings.UiMode, "bubble", StringComparison.OrdinalIgnoreCase);

            // 气泡内正在编辑时不要重建列表，否则刚输入的内容会被剪贴板/搜索刷新冲掉。
            if (_inlineEditRow != null && bubble &&
                RecordsPanel != null && RecordsPanel.Visibility == Visibility.Visible)
            {
                _recordsRefreshPending = true;
                return;
            }

            // 切换模式或编辑行已经不在当前页面时，放弃未保存的行内编辑状态。
            if (_inlineEditRow != null)
            {
                _inlineEditRow.IsEditing = false;
                _inlineEditRow = null;
                _inlineEditId = null;
            }

            _recordsRefreshPending = false;
            var tagMap = _store.Data.Tags.ToDictionary(t => t.Id, StringComparer.Ordinal);
            IEnumerable<RecordItem> query = _store.Data.Records.Where(r => !r.Deleted);

            var keyword = (SearchBox.Text ?? "").Trim();
            if (keyword.Length > 0)
            {
                var parts = keyword.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                query = query.Where(record => parts.All(part =>
                    record.Text.Contains(part, StringComparison.OrdinalIgnoreCase) ||
                    record.TagIds.Any(tagId => tagMap.TryGetValue(tagId, out var tag) &&
                        tag.Name.Contains(part, StringComparison.OrdinalIgnoreCase))));
            }

            if (_filterTagIds.Count > 0)
                query = query.Where(record => _filterTagIds.All(id => record.TagIds.Contains(id)));

            if (settings.HideUntagged)
                query = query.Where(record => record.TagIds.Count > 0);

            // 置顶的记录永远排在最前面，其余按时间排序
            var ordered = settings.SortMode == "time_asc"
                ? query.OrderByDescending(r => r.Pinned).ThenBy(r => r.CreatedAt)
                : query.OrderByDescending(r => r.Pinned).ThenByDescending(r => r.CreatedAt);

            var rows = new List<RecordRow>();
            foreach (var record in ordered)
            {
                var tagItems = new List<TagItem>(record.TagIds.Count);
                foreach (var id in record.TagIds)
                    if (tagMap.TryGetValue(id, out var tag)) tagItems.Add(tag);

                var urgency = tagItems.FirstOrDefault(t =>
                    string.Equals(t.Kind, BuiltinTags.KindUrgency, StringComparison.Ordinal));

                // 私密记录未解锁时只显示掩码：列表、气泡两种模式都走这里。
                var locked = BuiltinTags.IsPrivate(record) && !PrivacyGate.IsUnlocked;

                rows.Add(new RecordRow
                {
                    Record = record,
                    // 默认标签不显示成文字胶囊，只显示成小球 / 置顶图标
                    Tags = tagItems.Where(t => !t.Builtin).ToList(),
                    UrgencyBrush = urgency?.DotBrush,
                    UrgencyVisibility = urgency != null ? Visibility.Visible : Visibility.Collapsed,
                    TimeText = FormatTime(record.CreatedAt),
                    // 气泡模式用不到笔记模式的单行预览，不必为每条记录都压平文本
                    Preview = bubble ? "" : locked ? LockedPlaceholder : FlattenPreview(record.Text),
                    DisplayText = locked ? LockedPlaceholder : record.Text,
                    IsPrivateLocked = locked,
                    EditText = record.Text,
                });
            }

            // 只给当前模式正在显示的列表赋值，避免隐藏的另一种列表也生成整棵控件树。
            if (bubble)
            {
                RecordsList.ItemsSource = null;
                if (RecordsBubbleList != null) RecordsBubbleList.ItemsSource = rows;
            }
            else
            {
                if (RecordsBubbleList != null) RecordsBubbleList.ItemsSource = null;
                RecordsList.ItemsSource = rows;
            }
            RecordsEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private static string FlattenPreview(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";

            // 预览最多只显示 400 字符，没必要把几万字都压平；
            // 同时用一次遍历合并空白，避免原来 while-Replace 在大文本上越来越慢。
            var length = Math.Min(text.Length, 600);
            var sb = new StringBuilder(length);
            var lastWasSpace = false;
            for (var i = 0; i < length; i++)
            {
                var ch = text[i];
                if (ch is '\r' or '\n' or '\t' or ' ') ch = ' ';
                if (ch == ' ')
                {
                    if (lastWasSpace) continue;
                    lastWasSpace = true;
                }
                else
                {
                    lastWasSpace = false;
                }

                sb.Append(ch);
            }

            var flat = sb.ToString().Trim();
            return flat.Length > 400 ? flat[..400] + "…" : flat;
        }

        private static string FormatTime(long unixMs)
        {
            if (unixMs <= 0) return "";
            var time = DateTimeOffset.FromUnixTimeMilliseconds(unixMs).LocalDateTime;
            var today = DateTime.Today;
            if (time.Date == today) return "今天 " + time.ToString("HH:mm");
            if (time.Date == today.AddDays(-1)) return "昨天 " + time.ToString("HH:mm");
            if (time.Year == today.Year) return time.ToString("MM-dd HH:mm");
            return time.ToString("yyyy-MM-dd HH:mm");
        }

        private static RecordItem RowRecord(object sender) =>
            (sender as FrameworkElement)?.DataContext is RecordRow row ? row.Record : null;

        private void RecordEdit_Click(object sender, RoutedEventArgs e)
        {
            var record = RowRecord(sender);
            if (record == null) return;

            if (string.Equals(_store.Data.Settings.UiMode, "bubble", StringComparison.OrdinalIgnoreCase))
            {
                var row = (sender as FrameworkElement)?.DataContext as RecordRow;
                if (row != null && row.IsEditing)
                {
                    row.IsEditing = false;
                    _inlineEditId = null;
                    _inlineEditRow = null;
                    FlushPendingRecordsRefresh();
                    return;
                }

                _ = BeginEditWithGateAsync(record);
                return;
            }

            _ = OpenEditorWithGateAsync(record);
        }

        /// <summary>私密记录在未解锁时先过 Windows Hello，验证通过才允许查看/编辑/复制。</summary>
        private async Task<bool> EnsurePrivateAccessAsync(RecordItem record)
        {
            if (record == null || !BuiltinTags.IsPrivate(record) || PrivacyGate.IsUnlocked) return true;

            var (ok, error) = await PrivacyGate.VerifyAsync("查看 cooopy 中的私密记录");
            if (!ok)
            {
                if (!string.IsNullOrEmpty(error)) ShowToast(error);
                return false;
            }

            // 解锁后立刻重建列表，让掩码变回正文。
            _wasUnlocked = true;
            if (_initialized) RefreshRecords();
            return true;
        }

        private async Task BeginEditWithGateAsync(RecordItem record)
        {
            if (!await EnsurePrivateAccessAsync(record)) return;
            BeginInlineEdit(record);
        }

        private async Task OpenEditorWithGateAsync(RecordItem record)
        {
            if (!await EnsurePrivateAccessAsync(record)) return;
            OpenRecordEditor(record);
        }

        // 展开编辑时，文本框内部滚动到顶/到底后，不再把滚轮交给外层列表，
        // 避免文本框已经滚到头、气泡卡片却被列表带着一起位移。
        private void EditTextBox_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (sender is not TextBox box) return;

            var scrollable = box.ExtentHeight - box.ViewportHeight;
            if (scrollable <= 0.5) return;

            var atTop = box.VerticalOffset <= 0.5;
            var atBottom = box.VerticalOffset >= scrollable - 0.5;
            if ((e.Delta > 0 && atTop) || (e.Delta < 0 && atBottom))
                e.Handled = true;
        }

        private static T FindVisualChild<T>(DependencyObject root) where T : DependencyObject
        {
            if (root == null) return null;
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is T match) return match;
                var nested = FindVisualChild<T>(child);
                if (nested != null) return nested;
            }

            return null;
        }

        private static T FindVisualParent<T>(DependencyObject child) where T : DependencyObject
        {
            var current = child;
            while (current != null)
            {
                if (current is T match) return match;
                current = VisualTreeHelper.GetParent(current);
            }

            return null;
        }

        private void RevealEditingBubble()
        {
            if (_inlineEditRow == null || RecordsBubbleList == null) return;

            var container = RecordsBubbleList.ItemContainerGenerator.ContainerFromItem(_inlineEditRow) as FrameworkElement;
            var scroll = FindVisualChild<ScrollViewer>(RecordsBubbleList);
            if (container == null || scroll == null) return;

            var top = container.TransformToAncestor(scroll).Transform(new Point(0, 0)).Y;
            var bottom = top + container.ActualHeight;
            var overflow = bottom - scroll.ViewportHeight;
            if (overflow > 0)
                scroll.ScrollToVerticalOffset(scroll.VerticalOffset + overflow);
        }

        // 展开编辑的泡泡在收起前始终留在可视区域内：
        // 列表照常可以滚，但一旦会把这个泡泡滚出窗口，就把偏移量拉回来。
        private void BubbleScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (_clampingBubbleScroll || _inlineEditRow == null) return;
            if (e.VerticalChange == 0 && e.ViewportHeightChange == 0) return;
            if (sender is not ScrollViewer scroll) return;
            if (RecordsBubbleList == null) return;

            var container = RecordsBubbleList.ItemContainerGenerator.ContainerFromItem(_inlineEditRow) as FrameworkElement;
            if (container == null) return;

            double top;
            try
            {
                top = container.TransformToAncestor(scroll).Transform(new Point(0, 0)).Y;
            }
            catch (InvalidOperationException)
            {
                return;
            }

            var height = container.ActualHeight;
            var viewport = scroll.ViewportHeight;
            if (height <= 0 || viewport <= 0) return;

            double delta = 0;
            if (top < 0)
            {
                // 顶部被滚出：往下拉回来
                delta = top;
            }
            else if (height < viewport - 0.5 && top + height > viewport)
            {
                // 底部被滚出：往上推回来
                delta = top + height - viewport;
            }

            if (Math.Abs(delta) < 0.5) return;

            _clampingBubbleScroll = true;
            try
            {
                scroll.ScrollToVerticalOffset(scroll.VerticalOffset + delta);
            }
            finally
            {
                _clampingBubbleScroll = false;
            }
        }

        private void BeginInlineEdit(RecordItem record)
        {
            if (_inlineEditRow != null && !ReferenceEquals(_inlineEditRow.Record, record))
                _inlineEditRow.IsEditing = false;

            var row = (RecordsBubbleList.ItemsSource as IEnumerable<RecordRow>)?
                .FirstOrDefault(r => string.Equals(r.Record.Id, record.Id, StringComparison.Ordinal));
            if (row == null) return;

            row.EditText = record.Text;
            row.EditTags = _store.Data.Tags
                .OrderBy(t => t.Builtin ? BuiltinTags.OrderOf(t) : 1000)
                .ThenBy(t => t.Name)
                .Select(t => new TagEditOption { Tag = t, IsSelected = record.TagIds.Contains(t.Id) })
                .ToList();
            _inlineEditId = record.Id;
            _inlineEditRow = row;
            row.IsEditing = true;
            Dispatcher.BeginInvoke(new Action(RevealEditingBubble), DispatcherPriority.Background);
        }

        private void CancelInlineEdit_Click(object sender, RoutedEventArgs e)
        {
            if (_inlineEditRow != null)
            {
                _inlineEditRow.IsEditing = false;
            }

            _inlineEditId = null;
            _inlineEditRow = null;
            FlushPendingRecordsRefresh();
        }

        private void FlushPendingRecordsRefresh()
        {
            if (!_recordsRefreshPending) return;
            _recordsRefreshPending = false;
            RefreshRecords();
        }

        private void ToggleEditTag_Click(object sender, MouseButtonEventArgs e)
        {
            var option = (sender as FrameworkElement)?.DataContext as TagEditOption;
            if (option == null) return;
            option.IsSelected = !option.IsSelected;

            // 紧急程度（红/黄/绿）只能选一个：选中一个时自动取消另外两个
            if (option.IsSelected &&
                string.Equals(option.Tag.Kind, BuiltinTags.KindUrgency, StringComparison.Ordinal))
            {
                var list = FindVisualParent<ItemsControl>(sender as DependencyObject);
                if (list?.ItemsSource is IEnumerable<TagEditOption> options)
                {
                    foreach (var other in options)
                    {
                        if (!ReferenceEquals(other, option) &&
                            string.Equals(other.Tag.Kind, BuiltinTags.KindUrgency, StringComparison.Ordinal))
                            other.IsSelected = false;
                    }
                }
            }

            e.Handled = true;
        }

        private void ConfirmInlineEdit_Click(object sender, RoutedEventArgs e)
        {
            var row = _inlineEditRow ?? (sender as FrameworkElement)?.DataContext as RecordRow;
            if (row == null) return;

            var text = (row.EditText ?? "").Trim();
            if (text.Length == 0)
            {
                ShowToast("内容不能为空");
                return;
            }

            row.Record.Text = text;
            row.Record.TagIds = (row.EditTags ?? new List<TagEditOption>())
                .Where(o => o.IsSelected)
                .Select(o => o.Tag.Id)
                .ToList();
            _store.NormalizeRecordTags(row.Record);
            row.Record.UpdatedAt = DateTimeOffset.Now.ToUnixTimeMilliseconds();
            _store.SaveNow();
            _inlineEditId = null;
            _inlineEditRow = null;
            RefreshAll();
            ShowToast("已保存");
        }

        private void OpenRecordEditor(RecordItem record)
        {
            try
            {
                _recordEditor?.Close();
            }
            catch
            {
                // ignored
            }

            var editor = new RecordEditorWindow(_store, record) { Owner = this };
            _recordEditor = editor;
            editor.Saved += (text, tagIds) =>
            {
                _store.UpdateRecord(record, text, tagIds);
                RefreshAll();
                ShowToast(BuiltinTags.IsPrivate(record) ? "已保存修改（私密）" : "已保存修改");
            };
            editor.Closed += (_, _) =>
            {
                if (ReferenceEquals(_recordEditor, editor)) _recordEditor = null;
            };
            editor.Show();
            editor.Activate();
        }

        private async void RecordRow_RightClick(object sender, MouseButtonEventArgs e)
        {
            var record = RowRecord(sender);
            if (record == null) return;
            e.Handled = true;
            if (!await EnsurePrivateAccessAsync(record)) return;

            _clipboard.SetClipboardText(record.Text);
            ShowToast("已复制到剪贴板");
        }

        private void RecordDelete_Click(object sender, RoutedEventArgs e)
        {
            var record = RowRecord(sender);
            if (record == null) return;

            if (string.Equals(_store.Data.Settings.UiMode, "bubble", StringComparison.OrdinalIgnoreCase))
            {
                var editingRow = (sender as FrameworkElement)?.DataContext as RecordRow;
                if (editingRow != null && editingRow.IsEditing)
                {
                    ConfirmInlineEdit_Click(sender, e);
                    return;
                }
            }

            if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
            {
                DeleteRecordNow(record);
                return;
            }

            var preview = FlattenPreview(record.Text);
            if (preview.Length > 60) preview = preview[..60] + "…";
            ShowConfirm(
                "删除记录",
                $"确定删除这条记录吗？\n「{preview}」\n删除后可按 Ctrl+Z 逐条撤销。",
                "删除",
                () => DeleteRecordNow(record));
        }

        private void DeleteRecordNow(RecordItem record)
        {
            if (string.Equals(record.Id, _inlineEditId, StringComparison.Ordinal))
            {
                _inlineEditId = null;
                _inlineEditRow = null;
            }
            _undoStack.Add(new UndoEntry { Record = record });
            _store.DeleteRecord(record);
            RefreshAll();
            ShowToast("已删除（按 Ctrl+Z 可逐条撤销）");
        }

        private void ShowConfirm(string title, string message, string confirmText, Action onConfirm)
        {
            var dialog = new ConfirmDialogWindow(title, message, confirmText, danger: true)
            {
                Owner = this
            };
            dialog.Confirmed += onConfirm;
            dialog.Show();
            dialog.Activate();
        }

        private void RecordTags_Click(object sender, MouseButtonEventArgs e)
        {
            if (!(e.OriginalSource is DependencyObject source)) return;
            var tag = FindDataContext<TagItem>(source);
            if (tag == null) return;

            if (!_filterTagIds.Remove(tag.Id)) _filterTagIds.Add(tag.Id);
            RebuildFilterPanel();
            RefreshRecords();
            e.Handled = true;
        }

        private static T FindDataContext<T>(DependencyObject source) where T : class
        {
            var current = source;
            while (current != null)
            {
                if (current is FrameworkElement element && element.DataContext is T match) return match;
                current = VisualTreeHelper.GetParent(current);
            }
            return null;
        }

        // ------------------------------------------------------------- filters / search / sort

        /// <summary>退出搜索：清空关键字、把键盘焦点还给窗口，列表恢复未过滤状态。</summary>
        private void ExitSearch()
        {
            if (SearchBox == null) return;
            var hadText = !string.IsNullOrEmpty(SearchBox.Text);
            _searchTimer?.Stop();
            SearchBox.Text = "";
            // 把键盘焦点从搜索框移走：否则框仍处于激活态（占位符不显示），看起来"没退出"。
            Keyboard.ClearFocus();
            Focus();
            UpdateSearchPlaceholder();
            if (hadText && _initialized)
            {
                // 延迟到当前输入处理完成后刷新：重建列表不能发生在点击半路上，
                // 否则被点的视觉元素被替换、本次点击会落空。
                Dispatcher.BeginInvoke(new Action(RefreshRecords),
                    System.Windows.Threading.DispatcherPriority.Input);
            }
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            UpdateSearchPlaceholder();
            if (!_initialized) return;
            _searchTimer.Stop();
            _searchTimer.Start();
        }

        private void SearchBox_GotFocus(object sender, KeyboardFocusChangedEventArgs e) => UpdateSearchPlaceholder();

        private void SearchBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            UpdateSearchPlaceholder();
            // 失焦即退出搜索：覆盖真实鼠标点击任何可聚焦元素、Tab 切换等所有路径
            //（窗口级 PreviewMouseDown 对"点击不可聚焦元素"才可靠，两者互补）。
            if (_initialized && !string.IsNullOrEmpty(SearchBox.Text))
                ExitSearch();
        }

        private void UpdateSearchPlaceholder()
        {
            if (SearchPlaceholder == null) return;
            SearchPlaceholder.Visibility =
                string.IsNullOrEmpty(SearchBox.Text) && !SearchBox.IsKeyboardFocused
                    ? Visibility.Visible
                    : Visibility.Collapsed;
        }

        private void SortButton_Click(object sender, RoutedEventArgs e)
        {
            var next = _store.Data.Settings.SortMode == "time_asc" ? "time_desc" : "time_asc";
            _store.UpdateSettings(s => s.SortMode = next);
            UpdateSortButton();
            RefreshRecords();
        }

        private void UpdateSortButton()
        {
            if (SortButton == null || SortAscIcon == null || SortDescIcon == null) return;

            var ascending = _store.Data.Settings.SortMode == "time_asc";
            SortAscIcon.Visibility = ascending ? Visibility.Visible : Visibility.Collapsed;
            SortDescIcon.Visibility = ascending ? Visibility.Collapsed : Visibility.Visible;
            SortButton.ToolTip = ascending
                ? "时间正序（点击切换为倒序）"
                : "时间倒序（点击切换为正序）";
        }

        private void RebuildFilterPanel()
        {
            if (FilterPanel == null) return;
            FilterPanel.Children.Clear();


            _filterOptions = _store.Data.Tags
                .Where(t => !t.Builtin)
                .OrderBy(t => t.Name)
                .Select(t => new TagFilterOption { Tag = t, Selected = _filterTagIds.Contains(t.Id) })
                .ToList();

            if (_filterOptions.Count == 0)
            {
                FilterPanel.Children.Add(new TextBlock
                {
                    Text = "还没有自定义标签，先到「标签」页创建一个吧",
                    Foreground = (Brush)FindResource("TextMuted"),
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap
                });
                return;
            }

            var selectedRowBrush = new SolidColorBrush(Color.FromRgb(0xF1, 0xF2, 0xF4));
            selectedRowBrush.Freeze();

            foreach (var option in _filterOptions)
            {
                // Theme-coloured background with white text for each tag in the filter list.
                var chip = new Border
                {
                    Background = option.Tag.DotBrush,
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(10, 3, 10, 3),
                    Child = new TextBlock
                    {
                        Text = option.Tag.Name,
                        Foreground = option.Tag.ChipForeground,
                        FontSize = 13.5,
                        VerticalAlignment = VerticalAlignment.Center
                    }
                };

                var checkBox = new CheckBox
                {
                    Content = chip,
                    Tag = option,
                    IsChecked = option.Selected,
                    Margin = new Thickness(0, 3, 0, 3),
                    VerticalContentAlignment = VerticalAlignment.Center
                };

                // Light grey fill marks the selected tag row.
                var row = new Border
                {
                    Background = option.Selected ? selectedRowBrush : Brushes.Transparent,
                    CornerRadius = new CornerRadius(7),
                    Padding = new Thickness(8, 0, 8, 0),
                    Margin = new Thickness(0, 1, 0, 1),
                    Child = checkBox
                };

                checkBox.Click += (s, _) =>
                {
                    var box = (CheckBox)s;
                    var opt = (TagFilterOption)box.Tag;
                    opt.Selected = box.IsChecked == true;
                    if (opt.Selected) _filterTagIds.Add(opt.Tag.Id);
                    else _filterTagIds.Remove(opt.Tag.Id);
                    row.Background = opt.Selected ? selectedRowBrush : Brushes.Transparent;
                    RefreshRecords();
                };

                FilterPanel.Children.Add(row);
            }
        }

        private void FilterButton_Click(object sender, RoutedEventArgs e)
        {
            if (FilterPopup.IsOpen)
            {
                FilterPopup.IsOpen = false;
                return;
            }

            RebuildFilterPanel();
            FilterPopup.IsOpen = true;
        }

        private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            // 点击搜索框外：退出搜索（清空并失焦），不吞掉点击本身。
            if ((SearchBox?.IsKeyboardFocused == true || !string.IsNullOrEmpty(SearchBox?.Text)) &&
                !(e.OriginalSource is DependencyObject src && IsInside(src, SearchGrid)))
            {
                ExitSearch();
            }

            if (!FilterPopup.IsOpen) return;
            // Clicks inside the popup itself must not close it.
            if (FilterPopup.Child is DependencyObject popupContent && IsInside(e.OriginalSource as DependencyObject, popupContent))
                return;
            // Let the filter button's own Click handler toggle the popup.
            if (FilterButton != null && FilterButton.IsMouseOver) return;
            FilterPopup.IsOpen = false;
        }

        private static bool IsInside(DependencyObject node, DependencyObject root)
        {
            while (node != null)
            {
                if (ReferenceEquals(node, root)) return true;
                node = node is Visual || node is System.Windows.Media.Media3D.Visual3D
                    ? VisualTreeHelper.GetParent(node)
                    : LogicalTreeHelper.GetParent(node);
            }
            return false;
        }

        private void ToggleHideUntagged_Click(object sender, RoutedEventArgs e)
        {
            var hide = !_store.Data.Settings.HideUntagged;
            _store.UpdateSettings(s => s.HideUntagged = hide);
            UpdateHideUntaggedButton();
            RefreshRecords();
        }

        private void UpdateHideUntaggedButton()
        {
            if (EyeOpenIcon == null || EyeClosedIcon == null) return;
            var hide = _store.Data.Settings.HideUntagged;
            EyeOpenIcon.Visibility = hide ? Visibility.Collapsed : Visibility.Visible;
            EyeClosedIcon.Visibility = hide ? Visibility.Visible : Visibility.Collapsed;
            if (HideUntaggedButton != null)
                HideUntaggedButton.ToolTip = hide
                    ? "当前隐藏无标签记录（点击显示）"
                    : "显示全部记录（点击隐藏无标签）";
        }

        private void ClearUntagged_Click(object sender, RoutedEventArgs e)
        {
            var untagged = _store.Data.Records
                .Where(r => !r.Deleted && r.TagIds.Count == 0)
                .ToList();
            if (untagged.Count == 0)
            {
                ShowToast("没有无标签记录");
                return;
            }

            ShowConfirm(
                "清除无标签记录",
                $"确定清除所有无标签记录吗？共 {untagged.Count} 条。\n清除后可按 Ctrl+Z 逐条恢复。",
                "清除",
                () =>
                {
                    foreach (var record in untagged)
                    {
                        _undoStack.Add(new UndoEntry { Record = record });
                        record.Deleted = true;
                    }

                    _store.SaveNow();
                    RefreshAll();
                    ShowToast($"已清除 {untagged.Count} 条无标签记录");
                });
        }



        private void ClearFilter_Click(object sender, RoutedEventArgs e)
        {
            _filterTagIds.Clear();
            RebuildFilterPanel();
            RefreshRecords();
        }

        private void FilterNewTag_Click(object sender, RoutedEventArgs e)
        {
            FilterPopup.IsOpen = false;
            OpenTagEditor(null);
        }

        // ------------------------------------------------------------- tags page

        /// <summary>一次遍历统计每个标签的使用次数，避免每个标签都扫描一遍全部记录。</summary>
        private Dictionary<string, int> CountActiveRecordsByTag()
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var record in _store.Data.Records)
            {
                if (record.Deleted) continue;
                foreach (var tagId in record.TagIds)
                {
                    counts[tagId] = counts.TryGetValue(tagId, out var count) ? count + 1 : 1;
                }
            }

            return counts;
        }

        private void RefreshTags()
        {
            var counts = CountActiveRecordsByTag();

            // 默认标签（内置）：置顶 + 红/黄/绿紧急程度，固定顺序，不可编辑/删除
            var defaultRows = _store.Data.Tags
                .Where(t => t.Builtin)
                .OrderBy(BuiltinTags.OrderOf)
                .Select(t => new TagRow { Tag = t, Count = counts.TryGetValue(t.Id, out var c) ? c : 0 })
                .ToList();
            DefaultTagsList.ItemsSource = defaultRows;

            // 自定义标签
            var rows = _store.Data.Tags
                .Where(t => !t.Builtin)
                .Select(t => new TagRow { Tag = t, Count = counts.TryGetValue(t.Id, out var c) ? c : 0 })
                .OrderByDescending(r => r.Count)
                .ThenBy(r => r.Tag.Name)
                .ToList();
            TagsList.ItemsSource = rows;
            TagsEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void TagCreate_Click(object sender, RoutedEventArgs e) => OpenTagEditor(null);

        private void TagEdit_Click(object sender, RoutedEventArgs e)
        {
            var row = (sender as FrameworkElement)?.DataContext as TagRow;
            if (row == null || row.Tag.Builtin) return;
            OpenTagEditor(row.Tag);
        }

        private void OpenTagEditor(TagItem existing)
        {
            try
            {
                _tagEditor?.Close();
            }
            catch
            {
                // ignored
            }

            var dialog = new TagEditorWindow(existing) { Owner = this };
            _tagEditor = dialog;
            dialog.Saved += (name, color) =>
            {
                var ok = existing == null
                    ? _store.CreateTag(name, color) != null
                    : _store.RenameOrRecolorTag(existing, name, color);
                if (!ok)
                {
                    MessageBox.Show(this, "标签名重复或无效", "cooopy", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                RefreshAll();
            };
            dialog.Closed += (_, _) =>
            {
                if (ReferenceEquals(_tagEditor, dialog)) _tagEditor = null;
            };
            dialog.Show();
            dialog.Activate();
        }

        private void TagDelete_Click(object sender, RoutedEventArgs e)
        {
            var row = (sender as FrameworkElement)?.DataContext as TagRow;
            if (row == null || row.Tag.Builtin) return;

            if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
            {
                DeleteTagNow(row.Tag);
                return;
            }

            var count = _store.CountRecordsForTag(row.Tag.Id);
            ShowConfirm(
                "删除标签",
                count > 0
                    ? $"确定删除标签「{row.Tag.Name}」吗？\n该标签已用于 {count} 条记录，删除后记录会保留。"
                    : $"确定删除标签「{row.Tag.Name}」吗？",
                "删除",
                () => DeleteTagNow(row.Tag));
        }

        private void DeleteTagNow(TagItem tag)
        {
            var affectedRecordIds = _store.Data.Records
                .Where(r => r.TagIds.Contains(tag.Id))
                .Select(r => r.Id)
                .ToList();
            var wasFiltered = _filterTagIds.Contains(tag.Id);

            _store.DeleteTag(tag);
            _filterTagIds.Remove(tag.Id);
            _undoStack.Add(new UndoEntry
            {
                Tag = tag,
                TagRecordIds = affectedRecordIds,
                TagWasFiltered = wasFiltered
            });
            RefreshAll();
            ShowToast("已删除标签「" + tag.Name + "」（Ctrl+Z 可恢复）");
        }

        // ------------------------------------------------------------- advanced (AI import)

        private void AdvancedGoSettings_Click(object sender, RoutedEventArgs e) => NavSettings.IsChecked = true;

        private void UpdateAdvancedTabVisibility()
        {
            var settings = _store.Data.Settings;
            var show = settings.AdvancedEnabled
                       && DeepSeekService.IsConfigured(settings.DeepSeekApiUrl, settings.DeepSeekApiKey);

            if (NavAdvanced != null)
                NavAdvanced.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

            if (!show && AdvancedPanel != null && AdvancedPanel.Visibility == Visibility.Visible && NavRecords != null)
                NavRecords.IsChecked = true;
        }

        private void UpdateAdvancedState()
        {
            if (AdvancedEmpty == null || AdvancedChat == null) return;

            var settings = _store.Data.Settings;
            var configured = DeepSeekService.IsConfigured(settings.DeepSeekApiUrl, settings.DeepSeekApiKey);
            AdvancedEmpty.Visibility = configured ? Visibility.Collapsed : Visibility.Visible;
            AdvancedChat.Visibility = configured ? Visibility.Visible : Visibility.Collapsed;
        }

        private void FillClipboard_Click(object sender, RoutedEventArgs e)
        {
            var text = ClipboardService.ReadClipboardText();
            if (string.IsNullOrWhiteSpace(text))
            {
                ShowToast("剪贴板里没有文本");
                return;
            }

            ChatInput.Text = text;
            ChatInput.CaretIndex = ChatInput.Text.Length;
        }

        private void ChatInput_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                _ = SendChatAsync();
                e.Handled = true;
            }
        }

        private void ClearChat_Click(object sender, RoutedEventArgs e)
        {
            _chatHistory.Clear();
            ChatMessages.Children.Clear();
        }

        private async void SendChat_Click(object sender, RoutedEventArgs e) => await SendChatAsync();

        private async Task SendChatAsync()
        {
            if (_chatBusy) return;

            var text = (ChatInput.Text ?? "").Trim();
            if (text.Length == 0) return;

            var settings = _store.Data.Settings;
            if (!DeepSeekService.IsConfigured(settings.DeepSeekApiUrl, settings.DeepSeekApiKey))
            {
                UpdateAdvancedState();
                return;
            }

            _chatBusy = true;
            ChatSendButton.IsEnabled = false;
            ChatInput.Clear();

            try
            {
                AddChatBubble(true, text);
                _chatHistory.Add(new DeepSeekService.ChatMessage("user", text));

                AddChatBubble(false, "思考中…", tag: "thinking");
                var messages = new List<DeepSeekService.ChatMessage>
                {
                    new("system", BuildAiSystemPrompt())
                };
                messages.AddRange(_chatHistory);

                var reply = await _deepSeek.CompleteAsync(
                    settings.DeepSeekApiUrl, settings.DeepSeekApiKey, settings.DeepSeekModel, messages);

                RemoveThinkingBubble();
                AddChatBubble(false, reply);
                _chatHistory.Add(new DeepSeekService.ChatMessage("assistant", reply));
                ImportAiReply(reply);
            }
            catch (Exception ex)
            {
                RemoveThinkingBubble();
                AddChatBubble(false, "请求失败：" + ex.Message, system: true);
            }
            finally
            {
                _chatBusy = false;
                ChatSendButton.IsEnabled = true;
            }
        }

        private void RemoveThinkingBubble()
        {
            for (var i = ChatMessages.Children.Count - 1; i >= 0; i--)
            {
                if (ChatMessages.Children[i] is FrameworkElement element &&
                    string.Equals(element.Tag as string, "thinking", StringComparison.Ordinal))
                {
                    ChatMessages.Children.RemoveAt(i);
                    return;
                }
            }
        }

        private Border AddChatBubble(bool isUser, string text, bool system = false, string tag = null)
        {
            var bubble = new Border
            {
                Child = new TextBlock
                {
                    Text = text ?? "",
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 14,
                    Foreground = isUser
                        ? Brushes.White
                        : (Brush)FindResource(system ? "TextMuted" : "TextPrimary")
                },
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(12, 8, 12, 8),
                Margin = new Thickness(0, 0, 0, 8),
                MaxWidth = 560,
                Tag = tag,
                HorizontalAlignment = isUser ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                Background = isUser
                    ? (Brush)FindResource("Accent")
                    : (Brush)FindResource(system ? "SurfaceAlt" : "Surface"),
                BorderBrush = (Brush)FindResource("Line"),
                BorderThickness = isUser ? new Thickness(0) : new Thickness(1)
            };

            ChatMessages.Children.Add(bubble);
            Dispatcher.BeginInvoke(new Action(() => ChatScroll.ScrollToEnd()), DispatcherPriority.Background);
            return bubble;
        }

        private string BuildAiSystemPrompt()
        {
            var existingTags = string.Join("、", _store.Data.Tags.Select(t => t.Name).Take(40));
            return "你是 cooopy 的快捷键整理助手。用户会发来网页复制的软件快捷键列表、说明文字或大段内容。"
                   + "请把它们整理成干净、可直接导入 cooopy 的记录。只输出一个 JSON 对象，不要 Markdown 代码块，不要任何解释。格式："
                   + "{\"tags\":[{\"name\":\"软件名\",\"color\":\"#2F6FEB\"}],\"records\":[{\"text\":\"快捷键 - 作用\",\"tags\":[\"软件名\"]}]}。"
                   + "必须遵守："
                   + "1) 一个快捷键一条记录，text 固定为「快捷键 - 作用」，例如：\"Ctrl+C - 复制\"、\"Alt+F4 - 关闭窗口\"；"
                   + "2) 快捷键保持英文原样（Ctrl / Alt / Shift / Win / ⌘ 等，不要翻译成中文键名），作用用简洁中文描述（不超过 30 字）；"
                   + "3) 软件名称绝不能出现在 text 里，只能作为标签；每条记录都要带上表示软件名的标签（例如 Photoshop、VS Code）；"
                   + "4) 去掉导航、广告、页脚、教程说明、版本号等噪声，没有快捷键的句子不要导入；"
                   + "5) 只要记录内容包含快捷键（如 Ctrl+S、Alt+F4、F5、V 等单键快捷键），就必须同时带「快捷键」标签和软件名标签，标签名不超过 10 个字；"
                   + "6) 不要编造原文没有的快捷键，优先复用已有标签。"
                   + (existingTags.Length > 0 ? "现有标签：" + existingTags + "。" : "");
        }

        /// <summary>Normalizes an AI-produced record into "快捷键 - 作用" and strips the software name.</summary>
        private static string NormalizeImportedText(string text, IEnumerable<string> tagNames)
        {
            text = Regex.Replace(text ?? "", @"\s+", " ").Trim();
            if (text.Length == 0) return text;

            // Software name belongs to the tag, never to the record body.
            foreach (var tagName in tagNames.Where(n => !string.IsNullOrWhiteSpace(n)).OrderByDescending(n => n.Length))
            {
                var name = tagName.Trim();
                if (!text.StartsWith(name, StringComparison.OrdinalIgnoreCase)) continue;
                var rest = text[name.Length..].TrimStart(' ', ':', '：', '-', '—', '|', '（', '(');
                if (rest.Length > 0)
                {
                    text = rest;
                    break;
                }
            }

            // Keep "+" tight inside shortcut chords: "Ctrl + C" -> "Ctrl+C".
            text = Regex.Replace(text, @"\s*\+\s*", "+");

            // "快捷键 - 作用" where the chord really looks like a shortcut.
            var separated = Regex.Match(text, @"^(\S+)\s*[-—–:：|]\s*(.+)$");
            if (separated.Success && IsShortcutChord(separated.Groups[1].Value))
                return separated.Groups[1].Value + " - " + separated.Groups[2].Value.Trim();

            // "快捷键 作用" where the leading token looks like a shortcut.
            var joined = Regex.Match(text, @"^(\S{1,24})\s+(.+)$");
            if (joined.Success && IsShortcutChord(joined.Groups[1].Value))
                return joined.Groups[1].Value + " - " + joined.Groups[2].Value.Trim();

            return text;
        }

        private static bool IsShortcutChord(string chord)
        {
            chord = (chord ?? "").Trim();
            if (chord.Length == 0 || chord.Length > 24) return false;
            if (Regex.IsMatch(chord, @"\s")) return false;
            if (chord.StartsWith("http", StringComparison.OrdinalIgnoreCase) || chord.Contains("://")) return false;
            if (Regex.IsMatch(chord, @"^F\d{1,2}$", RegexOptions.IgnoreCase)) return true;
            if (Regex.IsMatch(chord, @"(?:Ctrl|Control|Alt|Shift|Win|Meta|Cmd|Command|Option|⌘|⌥|⌃|⇧)", RegexOptions.IgnoreCase))
                return true;
            return Regex.IsMatch(chord, @"^[A-Za-z0-9]$"); // Photoshop-style single-key shortcut
        }

        /// <summary>True when the record text contains any keyboard shortcut (chord or single-key form).</summary>
        private static bool LooksLikeShortcut(string text)
        {
            text = text ?? "";
            if (Regex.IsMatch(text,
                    @"(?:Ctrl|Control|Alt|Shift|Win|Meta|Cmd|Command|Option|⌘|⌥|⌃|⇧)\s*\+",
                    RegexOptions.IgnoreCase))
                return true;
            if (Regex.IsMatch(text, @"\bF(?:[1-9]|1\d|2[0-4])\b")) return true;

            var singleKey = Regex.Match(text, @"^([A-Za-z0-9])\s*[-—–:：|]\s*.+$");
            return singleKey.Success;
        }


        private string EnsureShortcutTag()
        {
            var existing = _store.Data.Tags.FirstOrDefault(t =>
                string.Equals(t.Name, "快捷键", StringComparison.OrdinalIgnoreCase));
            if (existing != null) return existing.Id;

            var created = _store.CreateTag("快捷键", ColorUtil.Palette[1]);
            return created?.Id;
        }

        private void ImportAiReply(string reply)
        {
            try
            {
                var start = reply.IndexOf('{');
                var end = reply.LastIndexOf('}');
                if (start < 0 || end <= start)
                {
                    AddChatBubble(false, "没有找到可导入的 JSON，可以让 AI 重新整理一次。", system: true);
                    return;
                }

                using var document = JsonDocument.Parse(reply[start..(end + 1)]);
                var root = document.RootElement;

                var tagIdsByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var tag in _store.Data.Tags.Where(t => !t.Builtin))
                    tagIdsByName[tag.Name] = tag.Id;

                if (root.TryGetProperty("tags", out var tagsElement) &&
                    tagsElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var tagElement in tagsElement.EnumerateArray())
                    {
                        var name = tagElement.TryGetProperty("name", out var nameElement)
                            ? (nameElement.GetString() ?? "").Trim()
                            : "";
                        if (name.Length == 0 || tagIdsByName.ContainsKey(name)) continue;

                        var color = tagElement.TryGetProperty("color", out var colorElement)
                            ? (colorElement.GetString() ?? "")
                            : "";
                        if (!Regex.IsMatch(color, "^#[0-9a-fA-F]{6}$"))
                            color = ColorUtil.Palette[_store.Data.Tags.Count % ColorUtil.Palette.Length];

                        var created = _store.CreateTag(name, color);
                        if (created != null) tagIdsByName[name] = created.Id;
                    }
                }

                var added = 0;
                var merged = 0;
                if (root.TryGetProperty("records", out var recordsElement) &&
                    recordsElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var recordElement in recordsElement.EnumerateArray())
                    {
                        var text = recordElement.TryGetProperty("text", out var textElement)
                            ? (textElement.GetString() ?? "").Trim()
                            : "";
                        if (text.Length == 0) continue;
                        text = NormalizeImportedText(text, tagIdsByName.Keys);
                        if (text.Length == 0) continue;

                        var ids = new List<string>();
                        if (recordElement.TryGetProperty("tags", out var tagArray) &&
                            tagArray.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var tagNameElement in tagArray.EnumerateArray())
                            {
                                var tagName = (tagNameElement.GetString() ?? "").Trim();
                                if (tagName.Length == 0) continue;

                                if (!tagIdsByName.TryGetValue(tagName, out var tagId))
                                {
                                    var created = _store.CreateTag(tagName,
                                        ColorUtil.Palette[_store.Data.Tags.Count % ColorUtil.Palette.Length]);
                                    if (created == null) continue;
                                    tagName = created.Name;
                                    tagIdsByName[tagName] = created.Id;
                                    tagId = created.Id;
                                }

                                if (!ids.Contains(tagId)) ids.Add(tagId);
                            }
                        }

                        if (LooksLikeShortcut(text))
                        {
                            var shortcutTagId = EnsureShortcutTag();
                            if (!string.IsNullOrEmpty(shortcutTagId) && !ids.Contains(shortcutTagId))
                                ids.Add(shortcutTagId);
                        }

                        var record = _store.AddRecord(text, ids);
                        if (record == null) continue;

                        added++;
                        var changed = false;
                        foreach (var tagId in ids)
                        {
                            if (!record.TagIds.Contains(tagId))
                            {
                                record.TagIds.Add(tagId);
                                changed = true;
                            }
                        }

                        if (changed) merged++;
                    }
                }

                _store.SaveNow();
                RefreshAll();

                if (added == 0)
                {
                    AddChatBubble(false, "AI 没有返回可导入的记录。", system: true);
                    return;
                }

                var message = $"已导入 {added} 条记录";
                if (merged > 0) message += $"（其中 {merged} 条为合并已有记录并补充标签）";
                AddChatBubble(false, message + "。", system: true);
                ShowToast(message);
            }
            catch (Exception ex)
            {
                AddChatBubble(false, "JSON 解析或导入失败：" + ex.Message, system: true);
            }
        }

        // ------------------------------------------------------------- settings

        private void LoadSettingsToUi()
        {
            if (ChkAutoStart == null) return;
            _loadingSettings = true;
            try
            {
                var settings = _store.Data.Settings;
                if (settings.SortMode == "recent") settings.SortMode = "time_desc";
                ChkAutoStart.IsChecked = StartupService.IsEnabled();
                ChkCloseToTray.IsChecked = settings.CloseToTray;
                ChkStartMinimized.IsChecked = settings.StartMinimized;
                ApplyAlwaysOnTop(settings.AlwaysOnTop, persist: false);
                ChkQuickAutoSave.IsChecked = settings.QuickTagAutoSave;
                ApiUrlBox.Text = settings.DeepSeekApiUrl ?? "";
                ApiKeyBox.Text = settings.DeepSeekApiKey ?? "";
                ModelBox.Text = string.IsNullOrWhiteSpace(settings.DeepSeekModel) ? "deepseek-chat" : settings.DeepSeekModel;
                ChkAdvancedEnabled.IsChecked = settings.AdvancedEnabled;
                var bubbleMode = string.Equals(settings.UiMode, "bubble", StringComparison.OrdinalIgnoreCase);
                ModeBubbleRadio.IsChecked = bubbleMode;
                ModeNoteRadio.IsChecked = !bubbleMode;
                ChkAutoCapture.IsChecked = settings.AutoCapture;
                ChkMaskIdCards.IsChecked = settings.MaskIdCards;
                MaxLengthBox.Text = settings.MaxLength.ToString();
                HotkeyMainBox.Text = settings.HotkeyMain;
                HotkeySaveBox.Text = settings.HotkeySave;
                ChkHotkeyMainEnabled.IsChecked = settings.HotkeyMainEnabled;
                ChkHotkeySaveEnabled.IsChecked = settings.HotkeySaveEnabled;
                UpdateHotkeyEnableUi();

                DedupeCombo.SelectedIndex = settings.DedupeMinutes switch
                {
                    <= 0 => 0,
                    <= 5 => 1,
                    <= 60 => 2,
                    _ => 3
                };

                DataDirBox.Text = _store.DataDir;
                var version = Assembly.GetExecutingAssembly().GetName().Version;
                AboutText.Text = $"版本 {version?.ToString(3)}\n开源项目";
            }
            finally
            {
                _loadingSettings = false;
            }

            UpdateSortButton();
            UpdateHideUntaggedButton();
            UpdatePauseUi();
            UpdateDataStat();
            UpdateAdvancedTabVisibility();
            ApplyUiMode();
            UpdateAiEnableUi();
            ScheduleAiLineCheck();
        }

        private void ScheduleAiLineCheck()
        {
            if (_aiCheckTimer == null) return;
            _aiCheckTimer.Stop();
            _aiCheckTimer.Start();
        }

        private async Task CheckAiLineAsync()
        {
            if (_aiChecking) return;

            var settings = _store.Data.Settings;
            var signature = (settings.DeepSeekApiUrl ?? "") + "|" + (settings.DeepSeekApiKey ?? "") + "|" + (settings.DeepSeekModel ?? "");
            if (signature == _aiCheckedSignature)
            {
                UpdateAiEnableUi();
                return;
            }

            if (!DeepSeekService.IsConfigured(settings.DeepSeekApiUrl, settings.DeepSeekApiKey))
            {
                _aiLineOk = false;
                _aiCheckReason = "请先填写 API 地址和 API Key";
                UpdateAiEnableUi();
                return;
            }

            _aiChecking = true;
            _aiLineOk = false;
            _aiCheckReason = "正在检测线路…";
            UpdateAiEnableUi();

            try
            {
                await _deepSeek.CheckConnectionAsync(settings.DeepSeekApiUrl, settings.DeepSeekApiKey);
                _aiLineOk = true;
                _aiCheckedSignature = signature;
                _aiCheckReason = "线路正常";
            }
            catch (Exception ex)
            {
                _aiLineOk = false;
                _aiCheckedSignature = null;
                var message = ex.Message ?? "";
                if (message.Length > 120) message = message[..120] + "…";
                _aiCheckReason = "线路未跑通：" + message;
            }
            finally
            {
                _aiChecking = false;
                UpdateAiEnableUi();
            }
        }

        private void UpdateAiEnableUi()
        {
            if (ChkAdvancedEnabled == null || AdvancedEnableBox == null || AdvancedReasonText == null) return;

            var ready = _aiLineOk && !_aiChecking;
            _suppressAdvancedToggle = true;
            if (!ready && ChkAdvancedEnabled.IsChecked == true)
            {
                ChkAdvancedEnabled.IsChecked = false;
                _store.UpdateSettings(s => s.AdvancedEnabled = false);
                UpdateAdvancedTabVisibility();
            }
            ChkAdvancedEnabled.IsEnabled = ready;
            _suppressAdvancedToggle = false;

            ChkAdvancedEnabled.ToolTip = ready ? null : _aiCheckReason;
            ChkAdvancedEnabled.Foreground = ready
                ? (Brush)FindResource("TextPrimary")
                : ColorUtil.Solid("#8A5B00");

            AdvancedEnableBox.Background = ready ? Brushes.Transparent : ColorUtil.Solid("#FFF4D6");
            AdvancedEnableBox.BorderBrush = ready ? Brushes.Transparent : ColorUtil.Solid("#F0C36D");
            AdvancedEnableBox.BorderThickness = ready ? new Thickness(0) : new Thickness(1);

            AdvancedReasonText.Text = _aiCheckReason;
            AdvancedReasonText.Visibility = ready ? Visibility.Collapsed : Visibility.Visible;
        }

        private void ModeRadio_Checked(object sender, RoutedEventArgs e)
        {
            if (_loadingSettings) return;
            PersistSettings();
        }

        private void HotkeyMainReset_Click(object sender, RoutedEventArgs e) => ResetHotkey(true);

        private void HotkeySaveReset_Click(object sender, RoutedEventArgs e) => ResetHotkey(false);

        private void ResetHotkey(bool main)
        {
            var defaults = main ? "Ctrl+Alt+C" : "Ctrl+Alt+S";
            _store.UpdateSettings(s =>
            {
                if (main) s.HotkeyMain = defaults;
                else s.HotkeySave = defaults;
            });

            if (main) HotkeyMainBox.Text = defaults;
            else HotkeySaveBox.Text = defaults;

            ApplyHotkeys();
            ShowToast("已恢复默认快捷键 " + defaults);
        }

        private void SettingsChanged_Click(object sender, RoutedEventArgs e)
        {
            if (_loadingSettings || _suppressAdvancedToggle) return;
            PersistSettings();
        }

        private void SettingsChanged_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loadingSettings) return;
            PersistSettings();
        }

        private void PersistSettings()
        {
            var autoStart = ChkAutoStart.IsChecked == true;
            var autoStartChanged = autoStart != StartupService.IsEnabled();

            int maxLength = 20000;
            int.TryParse(MaxLengthBox.Text, out maxLength);
            maxLength = Math.Clamp(maxLength, 1000, 200000);

            var dedupe = DedupeCombo.SelectedIndex switch
            {
                0 => 0,
                1 => 5,
                2 => 60,
                _ => 1440
            };

            _store.UpdateSettings(settings =>
            {
                settings.CloseToTray = ChkCloseToTray.IsChecked == true;
                settings.StartMinimized = ChkStartMinimized.IsChecked == true;
                settings.QuickTagAutoSave = ChkQuickAutoSave.IsChecked == true;
                settings.DeepSeekApiUrl = (ApiUrlBox.Text ?? "").Trim();
                settings.DeepSeekApiKey = (ApiKeyBox.Text ?? "").Trim();
                settings.DeepSeekModel = string.IsNullOrWhiteSpace(ModelBox.Text) ? "deepseek-chat" : ModelBox.Text.Trim();
                settings.AdvancedEnabled = ChkAdvancedEnabled.IsChecked == true;
                settings.UiMode = ModeBubbleRadio.IsChecked == true ? "bubble" : "note";
                settings.AutoCapture = ChkAutoCapture.IsChecked == true;
                settings.MaskIdCards = ChkMaskIdCards.IsChecked == true;
                settings.HotkeyMainEnabled = ChkHotkeyMainEnabled.IsChecked == true;
                settings.HotkeySaveEnabled = ChkHotkeySaveEnabled.IsChecked == true;
                settings.MaxLength = maxLength;
                settings.DedupeMinutes = dedupe;
            });

            if (autoStartChanged) StartupService.SetEnabled(autoStart);
            if (MaxLengthBox.Text != maxLength.ToString() && !MaxLengthBox.IsKeyboardFocusWithin)
                MaxLengthBox.Text = maxLength.ToString();

            UpdateHeader();
            UpdateAdvancedTabVisibility();
            ApplyUiMode();
            ScheduleAiLineCheck();
            if (AdvancedPanel != null && AdvancedPanel.Visibility == Visibility.Visible)
                UpdateAdvancedState();
        }

        private void MaxLengthBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_loadingSettings) return;
            if (!int.TryParse(MaxLengthBox.Text, out var value)) return;
            if (value < 1000 || value > 200000) return;
            _store.UpdateSettings(s => s.MaxLength = value);
        }

        private void PauseButton_Click(object sender, RoutedEventArgs e) => TogglePause();

        private void TogglePause()
        {
            var settings = _store.Data.Settings;
            if (settings.Paused)
            {
                _store.UpdateSettings(s => s.PauseUntil = 0);
                ShowToast("已恢复记录");
            }
            else
            {
                var until = DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeMilliseconds();
                _store.UpdateSettings(s => s.PauseUntil = until);
                ShowToast("已暂停记录 30 分钟");
            }

            UpdatePauseUi();
            UpdateHeader();
        }

        private void UpdatePauseUi()
        {
            if (PauseBadge == null) return;
            var settings = _store.Data.Settings;
            var paused = settings.Paused;
            PauseBadge.Visibility = paused ? Visibility.Visible : Visibility.Collapsed;
            PauseText.Text = paused
                ? "记录已暂停至 " + DateTimeOffset.FromUnixTimeMilliseconds(settings.PauseUntil).LocalDateTime.ToString("HH:mm")
                : "";
            PauseButton.Content = paused ? "恢复记录" : "暂停记录 30 分钟";
            PauseInfo.Text = paused ? "暂停期间复制的内容不会入库" : "";
            _tray?.SetPaused(paused);
            SyncPauseTimer();
        }

        private void SyncPauseTimer()
        {
            if (_pauseTimer == null) return;
            if (_store.Data.Settings.Paused)
            {
                if (!_pauseTimer.IsEnabled) _pauseTimer.Start();
            }
            else if (_pauseTimer.IsEnabled)
            {
                _pauseTimer.Stop();
            }
        }

        private void UpdateHeader()
        {
            if (PauseBadge != null && _store.Data.Settings.Paused) UpdatePauseUi();
        }

        private void UpdateDataStat()
        {
            if (DataStatText == null || !DataStatText.IsVisible) return;
            var total = _store.Data.Records.Count(r => !r.Deleted);
            long size = 0;
            try
            {
                if (File.Exists(_store.DataFile)) size = new FileInfo(_store.DataFile).Length;
            }
            catch
            {
                // ignored
            }

            DataStatText.Text = $"{total} 条记录 · {_store.Data.Tags.Count} 个标签 · 数据文件 {size / 1024.0:0.#} KB";
        }

        // ------------------------------------------------------------- data actions

        private void OpenDataDir_Click(object sender, RoutedEventArgs e)
        {
            var dir = _store.DataDir;
            try { Directory.CreateDirectory(dir); } catch { /* 已存在或不可写：仍然尝试打开 */ }

            // 优先用 shell 打开“文件夹本身”，这样由已经在运行的资源管理器接单。
            // 直接 Process.Start("explorer.exe", dir) 在低完整性级别下启动的 explorer 会立刻退出，
            // 表现就是“点了没反应/打不开”。
            try
            {
                Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
                return;
            }
            catch (Exception ex)
            {
                App.Log("open data dir (shell) failed: " + ex.Message);
            }

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"\"{dir}\"",
                    UseShellExecute = true
                });
                return;
            }
            catch (Exception ex)
            {
                App.Log("open data dir (explorer) failed: " + ex.Message);
                _clipboard.SetClipboardText(dir);
                ShowToast("打不开目录，路径已复制到剪贴板");
            }
        }

        private void ExportJson_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new SaveFileDialog
            {
                Filter = "JSON 文件 (*.json)|*.json",
                FileName = $"cooopy-export-{DateTime.Now:yyyyMMdd-HHmm}.json",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
            };
            if (dialog.ShowDialog(this) != true) return;
            try
            {
                _store.ExportJson(dialog.FileName);
                ShowToast(_store.LastExportSkipped > 0
                    ? $"已导出 JSON，跳过 {_store.LastExportSkipped} 条私密记录"
                    : "已导出 JSON");
            }
            catch (Exception ex)
            {
                ShowToast("导出失败：" + ex.Message);
            }
        }

        private void ExportMarkdown_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new SaveFileDialog
            {
                Filter = "Markdown 文件 (*.md)|*.md",
                FileName = $"cooopy-export-{DateTime.Now:yyyyMMdd-HHmm}.md",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
            };
            if (dialog.ShowDialog(this) != true) return;
            try
            {
                _store.ExportMarkdown(dialog.FileName);
                ShowToast(_store.LastExportSkipped > 0
                    ? $"已导出 Markdown，跳过 {_store.LastExportSkipped} 条私密记录"
                    : "已导出 Markdown");
            }
            catch (Exception ex)
            {
                ShowToast("导出失败：" + ex.Message);
            }
        }

        private void Import_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Filter = "cooopy 导出文件 (*.json)|*.json|所有文件 (*.*)|*.*",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
            };
            if (dialog.ShowDialog(this) != true) return;
            try
            {
                _store.SaveNow();
                var (added, updated, skipped) = _store.ImportJson(dialog.FileName);
                RefreshAll();
                ShowToast($"导入完成：新增 {added}，更新 {updated}，跳过 {skipped}");
            }
            catch (Exception ex)
            {
                ShowToast("导入失败：" + ex.Message);
            }
        }

        private void Backup_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _store.SaveNow();
                var dir = Path.Combine(_store.DataDir, "backups");
                Directory.CreateDirectory(dir);
                var file = Path.Combine(dir, $"cooopy-{DateTime.Now:yyyyMMdd-HHmmss}.json");
                File.Copy(_store.DataFile, file, true);
                ShowToast("已备份到 " + file);
            }
            catch (Exception ex)
            {
                ShowToast("备份失败：" + ex.Message);
            }
        }

        private void Reload_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var exe = Process.GetCurrentProcess().MainModule?.FileName;
                if (string.IsNullOrEmpty(exe)) return;
                Process.Start(new ProcessStartInfo { FileName = exe, UseShellExecute = true });
                ExitApp();
            }
            catch (Exception ex)
            {
                ShowToast("重启失败：" + ex.Message);
            }
        }

        private void Exit_Click(object sender, RoutedEventArgs e) => ExitApp();

        // ------------------------------------------------------------- misc

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            // While typing in an input box, let the box handle Ctrl shortcuts itself
            // (Ctrl+Z undo typing, Ctrl+A/C/V/X ...) instead of the app-level shortcuts.
            if (Keyboard.FocusedElement is TextBox &&
                (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                return;
            }

            if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control && _undoStack.Count > 0)
            {
                var entry = _undoStack[^1];
                _undoStack.RemoveAt(_undoStack.Count - 1);

                string restored;
                if (entry.Record != null)
                {
                    entry.Record.Deleted = false;
                    restored = "记录";
                }
                else if (entry.Tag != null)
                {
                    _store.Data.Tags.Add(entry.Tag);
                    foreach (var recordId in entry.TagRecordIds)
                    {
                        var record = _store.Data.Records.FirstOrDefault(r => r.Id == recordId);
                        if (record != null && !record.TagIds.Contains(entry.Tag.Id))
                        {
                            record.TagIds.Add(entry.Tag.Id);
                            _store.NormalizeRecordTags(record);
                        }
                    }

                    if (entry.TagWasFiltered) _filterTagIds.Add(entry.Tag.Id);
                    restored = $"标签「{entry.Tag.Name}」";
                }
                else
                {
                    return;
                }

                _store.SaveNow();
                RefreshAll();
                ShowToast(_undoStack.Count > 0
                    ? $"已恢复{restored}，可继续按 Ctrl+Z（还剩 {_undoStack.Count} 步）"
                    : $"已恢复{restored}");
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Escape)
            {
                if (FilterPopup?.IsOpen == true)
                {
                    FilterPopup.IsOpen = false;
                    e.Handled = true;
                }
                else if ((SearchBox?.IsKeyboardFocused == true || !string.IsNullOrEmpty(SearchBox?.Text)) &&
                         Keyboard.FocusedElement is TextBox)
                {
                    // 搜索中按 Esc：退出搜索，不隐藏主界面
                    ExitSearch();
                    e.Handled = true;
                }
                else if (Keyboard.FocusedElement is TextBox box && !string.IsNullOrEmpty(box.Text))
                {
                    box.Clear();
                    e.Handled = true;
                }
                // 其余情况 Esc 不做任何事（尤其不再隐藏主界面）
            }
        }

        private DateTime _lastSaveErrorAt = DateTime.MinValue;

        /// <summary>保存失败时提示一次，否则剪贴板每次变化都会重试、提示会刷屏。</summary>
        private void OnSaveFailed(string message)
        {
            if ((DateTime.Now - _lastSaveErrorAt).TotalMinutes < 5) return;
            _lastSaveErrorAt = DateTime.Now;
            App.Log(message);

            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (IsVisible && WindowState != WindowState.Minimized)
                    ShowToast("保存失败，记录可能丢失");
                else
                    _tray?.ShowBalloon("cooopy 保存失败", "数据没能写入：" + message, 5000);
            }));
        }

        private void ShowToast(string message)
        {
            if (string.IsNullOrEmpty(message)) return;
            ToastText.Text = message;
            ToastBorder.BeginAnimation(OpacityProperty, null);
            ToastBorder.Opacity = 1;
            _toastTimer.Stop();
            _toastTimer.Start();
        }
    }
}
