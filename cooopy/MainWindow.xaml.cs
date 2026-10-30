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
        private RightClickService _rightClick;
        private TagPopupWindow _popup;
        private HwndSource _source;
        private IntPtr _hwnd;
        private IntPtr _hotkeyHook;
        private Native.HookProc _hotkeyHookProc;
        private bool _hotkeyApplyPending;
        private bool _initialized;
        private bool _exiting;
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
        private bool _chatBusy;
        private DispatcherTimer _aiCheckTimer;
        private bool _aiChecking;
        private bool _aiLineOk;
        private string _aiCheckReason = "请先填写 API 地址和 API Key";
        private bool _suppressAdvancedToggle;
        private string _aiCheckedSignature;
        private double _noteWindowWidth = 1000;
        private string _inlineEditId;
        private RecordRow _inlineEditRow;
        private bool _clampingBubbleScroll;

        public MainWindow(DataStore store)
        {
            _store = store;
            InitializeComponent();

            // Keep the window inside the current work area (small screens / high DPI).
            var work = SystemParameters.WorkArea;
            Width = Math.Max(MinWidth, Math.Min(Width, work.Width - 60));
            Height = Math.Max(MinHeight, Math.Min(Height, work.Height - 48));
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = work.Left + (work.Width - Width) / 2;
            Top = work.Top + (work.Height - Height) / 2;

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

                _rightClick = new RightClickService(
                    () => _store.Data.Settings,
                    () => _popup != null && _popup.IsVisible,
                    () => _popup?.GetPhysicalRect(),
                    () => _popup?.OutsideClick(),
                    ShowTagPopup);
                _rightClick.Notice += message =>
                    Dispatcher.BeginInvoke(new Action(() => ShowToast(message)));
                _rightClick.Install();
                App.Log("right click hook installed: " + _rightClick.IsInstalled);

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
                    else if (id == HotkeyService.IdSave) CaptureSelectionAndShowPopup();
                    break;
            }

            return IntPtr.Zero;
        }

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            if (_exiting) return;
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
                _rightClick?.Dispose();
                _tray?.Dispose();
                _popup?.Close();
                _recordEditor?.Close();
                _tagEditor?.Close();
                _deepSeek?.Dispose();
                _store.FlushPending();
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

            // 关闭时如果输入框正聚焦，结束录制状态
            if (!mainEnabled && HotkeyMainBox != null && HotkeyMainBox.IsKeyboardFocusWithin)
                Keyboard.ClearFocus();
            if (!saveEnabled && HotkeySaveBox != null && HotkeySaveBox.IsKeyboardFocusWithin)
                Keyboard.ClearFocus();
            UpdateHotkeyHook();
        }

        private void HotkeyBox_GotFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox box) box.SelectAll();
            UpdateHotkeyHook();
        }

        private void HotkeyBox_LostFocus(object sender, RoutedEventArgs e) => UpdateHotkeyHook();

        /// <summary>
        /// 录制快捷键时使用低级键盘钩子：这样 Win 键（以及 Win+字母 这类系统组合）也能被捕获，
        /// 并且在录制期间屏蔽系统/输入框的默认处理。
        /// </summary>
        private void UpdateHotkeyHook()
        {
            var recording = HotkeyMainBox != null && HotkeyMainBox.IsKeyboardFocusWithin ||
                            HotkeySaveBox != null && HotkeySaveBox.IsKeyboardFocusWithin;

            if (recording)
            {
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
        private bool _hkWin;

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
                case 0x5B:
                case 0x5C:
                    _hkWin = down;
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
            if (_hkWin) modifiers |= ModifierKeys.Windows;
            return modifiers;
        }

        private void ResetHotkeyModifierState()
        {
            _hkCtrl = false;
            _hkAlt = false;
            _hkShift = false;
            _hkWin = false;
        }

        private void ApplyRecordedHotkey(TextBox box, string combo)
        {
            var isMain = ReferenceEquals(box, HotkeyMainBox);
            var id = isMain ? HotkeyService.IdMain : HotkeyService.IdSave;
            var result = _hotkeys.Apply(id, combo);
            if (!result.ok)
            {
                ShowToast(result.error);
                return;
            }

            box.Text = combo;
            _store.UpdateSettings(s =>
            {
                if (isMain) s.HotkeyMain = combo;
                else s.HotkeySave = combo;
            });
            ShowToast("快捷键已更新为 " + combo);
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
            if (Native.IsKeyDown(Native.VK_LWIN) || Native.IsKeyDown(Native.VK_RWIN))
                modifiers |= ModifierKeys.Windows;

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
            ShowTagPopupInternal(text, false, point, null);
        }

        /// <summary>
        /// “cooopy 到剪贴板”快捷键：先把当前光标选中的内容复制（Ctrl+C），
        /// 再打开快速打标签弹窗；如果没取到选中内容，则显示当前剪贴板里的内容。
        /// </summary>
        private async void CaptureSelectionAndShowPopup()
        {
            var started = Stopwatch.StartNew();
            try
            {
                var previous = ClipboardService.ReadClipboardText();
                var sequence = Native.GetClipboardSequenceNumber();
                Native.GetCursorPos(out var point);

                // 热键的修饰键（Alt / Ctrl / Shift / Win）可能还按着，
                // 先等它们松开，否则合成的 Ctrl+C 会变成 Ctrl+Alt+C 之类。
                for (var i = 0; i < 30; i++)
                {
                    if (!Native.IsKeyDown(Native.VK_CONTROL) &&
                        !Native.IsKeyDown(Native.VK_MENU) &&
                        !Native.IsKeyDown(Native.VK_SHIFT) &&
                        !Native.IsKeyDown(Native.VK_LWIN) &&
                        !Native.IsKeyDown(Native.VK_RWIN))
                        break;
                    await Task.Delay(10);
                }

                var captured = false;
                if (_store.Data.Settings.AutoCopySelection)
                {
                    Native.SendCtrlC();
                    // 有选中文字时剪贴板通常几十毫秒内就变化，短轮询即可；
                    // 没选中时不要再长时间空等，否则弹窗会明显变慢。
                    for (var i = 0; i < 7; i++)
                    {
                        await Task.Delay(15);
                        if (Native.GetClipboardSequenceNumber() != sequence)
                        {
                            captured = true;
                            break;
                        }
                    }
                }

                var text = captured ? ClipboardService.ReadClipboardText() : previous;
                if (string.IsNullOrWhiteSpace(text)) text = previous;
                if (string.IsNullOrWhiteSpace(text))
                {
                    ShowToast("没取到选中文字，剪贴板里也没有文本");
                    return;
                }

                ShowTagPopupInternal(text, captured, point, previous);
                App.Log($"hotkey popup: captured={captured}, elapsed={started.ElapsedMilliseconds}ms");
            }
            catch (Exception ex)
            {
                ShowToast("取词失败：" + ex.Message);
            }
        }

        private void ShowTagPopup(string text, bool captured, Native.POINT point, string previous)
        {
            ShowTagPopupInternal(text, captured, point, previous);
        }

        private void ShowTagPopupInternal(string text, bool captured, Native.POINT point, string previous)
        {
            var counts = CountActiveRecordsByTag();
            var tags = _store.Data.Tags
                .OrderByDescending(t => counts.TryGetValue(t.Id, out var c) ? c : 0)
                .ToList();

            // 复用同一个弹窗实例：窗口和模板只创建一次，后续取词弹窗会快很多。
            if (_popup == null)
            {
                _popup = new TagPopupWindow();
                _popup.Saved += (editedText, tagIds) =>
                    HandleQuickSave(editedText, tagIds, _popup.ContextCaptured, _popup.ContextPrevious);
            }

            _popup.Reset(text, tags, _store.Data.Settings.QuickTagAutoSave, captured, previous);
            _popup.ShowAtScreenPoint(point);
        }

        private void HandleQuickSave(string text, IList<string> tagIds, bool captured, string previous)
        {
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

            if (_store.Data.Settings.RestoreClipboard && captured && previous != null)
                _clipboard.SetClipboardText(previous);

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
            if (bubble)
            {
                if (WindowState == WindowState.Maximized) WindowState = WindowState.Normal;
                if (Width > 520) _noteWindowWidth = Width;
                // Bubble mode uses one fixed width (the current minimum width).
                MinWidth = 360;
                MaxWidth = 360;
                if (Math.Abs(Width - 360) > 0.5) Width = 360;
            }
            else
            {
                MaxWidth = double.PositiveInfinity;
                MinWidth = 780;
                if (_noteWindowWidth >= MinWidth && Math.Abs(Width - _noteWindowWidth) > 0.5)
                    Width = _noteWindowWidth;
            }


            RecordsList.Visibility = bubble ? Visibility.Collapsed : Visibility.Visible;
            RecordsBubbleList.Visibility = bubble ? Visibility.Visible : Visibility.Collapsed;
            if (SettingsContent != null)
            {
                SettingsContent.Margin = bubble
                    ? new Thickness(14, 18, 14, 30)
                    : new Thickness(22, 18, 22, 30);
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

                rows.Add(new RecordRow
                {
                    Record = record,
                    // 默认标签不显示成文字胶囊，只显示成小球 / 置顶图标
                    Tags = tagItems.Where(t => !t.Builtin).ToList(),
                    UrgencyBrush = urgency?.DotBrush,
                    UrgencyVisibility = urgency != null ? Visibility.Visible : Visibility.Collapsed,
                    TimeText = FormatTime(record.CreatedAt),
                    // 气泡模式用不到笔记模式的单行预览，不必为每条记录都压平文本
                    Preview = bubble ? "" : FlattenPreview(record.Text),
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
                    return;
                }

                BeginInlineEdit(record);
                return;
            }

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
                ShowToast("已保存修改");
            };
            editor.Closed += (_, _) =>
            {
                if (ReferenceEquals(_recordEditor, editor)) _recordEditor = null;
            };
            editor.Show();
            editor.Activate();
        }

        private void RecordRow_RightClick(object sender, MouseButtonEventArgs e)
        {
            var record = RowRecord(sender);
            if (record == null) return;
            _clipboard.SetClipboardText(record.Text);
            ShowToast("已复制到剪贴板");
            e.Handled = true;
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

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            UpdateSearchPlaceholder();
            if (!_initialized) return;
            _searchTimer.Stop();
            _searchTimer.Start();
        }

        private void SearchBox_GotFocus(object sender, KeyboardFocusChangedEventArgs e) => UpdateSearchPlaceholder();

        private void SearchBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e) => UpdateSearchPlaceholder();

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
                ChkQuickAutoSave.IsChecked = settings.QuickTagAutoSave;
                ApiUrlBox.Text = settings.DeepSeekApiUrl ?? "";
                ApiKeyBox.Text = settings.DeepSeekApiKey ?? "";
                ModelBox.Text = string.IsNullOrWhiteSpace(settings.DeepSeekModel) ? "deepseek-chat" : settings.DeepSeekModel;
                ChkAdvancedEnabled.IsChecked = settings.AdvancedEnabled;
                var bubbleMode = string.Equals(settings.UiMode, "bubble", StringComparison.OrdinalIgnoreCase);
                ModeBubbleRadio.IsChecked = bubbleMode;
                ModeNoteRadio.IsChecked = !bubbleMode;
                ChkContextEnabled.IsChecked = settings.ContextMenuEnabled;
                ChkAutoCopy.IsChecked = settings.AutoCopySelection;
                ChkRestore.IsChecked = settings.RestoreClipboard;
                ChkAutoCapture.IsChecked = settings.AutoCapture;
                MaxLengthBox.Text = settings.MaxLength.ToString();
                HotkeyMainBox.Text = settings.HotkeyMain;
                HotkeySaveBox.Text = settings.HotkeySave;
                ChkHotkeyMainEnabled.IsChecked = settings.HotkeyMainEnabled;
                ChkHotkeySaveEnabled.IsChecked = settings.HotkeySaveEnabled;
                UpdateHotkeyEnableUi();

                ModifierCombo.SelectedIndex = settings.ContextModifier switch
                {
                    "Ctrl" => 1,
                    "Alt" => 2,
                    "无" => 3,
                    _ => 0
                };

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

            var modifier = ModifierCombo.SelectedIndex switch
            {
                1 => "Ctrl",
                2 => "Alt",
                3 => "无",
                _ => "Shift"
            };

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
                settings.ContextMenuEnabled = ChkContextEnabled.IsChecked == true;
                settings.AutoCopySelection = ChkAutoCopy.IsChecked == true;
                settings.RestoreClipboard = ChkRestore.IsChecked == true;
                settings.AutoCapture = ChkAutoCapture.IsChecked == true;
                settings.HotkeyMainEnabled = ChkHotkeyMainEnabled.IsChecked == true;
                settings.HotkeySaveEnabled = ChkHotkeySaveEnabled.IsChecked == true;
                settings.MaxLength = maxLength;
                settings.ContextModifier = modifier;
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
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"\"{_store.DataDir}\"",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                ShowToast("打开目录失败：" + ex.Message);
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
                ShowToast("已导出 JSON");
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
                ShowToast("已导出 Markdown");
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
                else if (Keyboard.FocusedElement is TextBox box && !string.IsNullOrEmpty(box.Text))
                {
                    box.Clear();
                    e.Handled = true;
                }
                else if (Keyboard.Modifiers == ModifierKeys.None)
                {
                    Hide();
                    e.Handled = true;
                }
            }
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
