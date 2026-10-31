using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using Cooopy.Models;
using Cooopy.Services;

namespace Cooopy.Windows
{
    public partial class TagPopupWindow : Window
    {
        private string _originalText = "";
        private bool _autoSave;
        private readonly HashSet<string> _selected = new();
        private bool _closing;
        private bool _closeScheduled;
        private bool _interactive;
        private int _showVersion;
        private string _cachedTagKey = "";
        private readonly List<CheckBox> _cachedBoxes = new();

        /// <summary>Raised with the edited text and the selected tag ids.</summary>
        public event Action<string, List<string>> Saved;

        /// <summary>弹窗隐藏（保存/放弃）后触发，宿主用它停掉外部点击监视。</summary>
        public event Action Hidden;

        public TagPopupWindow()
        {
            InitializeComponent();
        }

        /// <summary>复用同一个窗口实例，避免每次取词都重新创建 WPF 窗口和模板（明显降低弹窗延迟）。</summary>
        public void Reset(string text, IEnumerable<TagItem> tags, bool autoSave)
        {
            _originalText = text ?? "";
            _autoSave = autoSave;
            _selected.Clear();
            _closing = false;
            _closeScheduled = false;
            _interactive = false;
            _showVersion++;

            ContentBox.Text = _originalText;
            BuildTags(tags);
        }

        /// <summary>后台取词拿到选中文本后调用：仅当用户尚未交互（未点入、未改字）时替换内容，
        /// 避免覆盖用户正在输入的文字。</summary>
        public void UpdateTextIfUntouched(string text)
        {
            if (_interactive || _closing) return;
            if (ContentBox.Text != _originalText) return;
            if (string.IsNullOrEmpty(text)) return;
            _originalText = text;
            ContentBox.Text = text;
        }

        private void Window_SourceInitialized(object sender, EventArgs e)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var style = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
            style |= Native.WS_EX_TOOLWINDOW;
            Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, new IntPtr(style));
            SetNoActivate(hwnd);
        }

        /// <summary>
        /// 不抢键盘焦点：目标程序始终保持焦点和选中状态，快捷键连续取词时
        /// 合成的 Ctrl+C 永远落在有选中的窗口上。用户点击弹窗内部才进入交互模式。
        /// </summary>
        private void SetNoActivate(IntPtr hwnd)
        {
            var style = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
            style |= Native.WS_EX_NOACTIVATE;
            Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, new IntPtr(style));
            _interactive = false;
        }

        /// <summary>用户点击了弹窗内部：进入可交互模式（去掉不激活样式并取得焦点）。</summary>
        private void EnableInteraction()
        {
            if (_interactive) return;
            _interactive = true;
            var hwnd = new WindowInteropHelper(this).Handle;
            var style = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
            style &= ~Native.WS_EX_NOACTIVATE;
            Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, new IntPtr(style));
            Activate();
        }

        private void Window_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // 第一次点击只是“进入”弹窗：先取得交互能力，让这次点击正常落到所点的控件上。
            EnableInteraction();
        }

        public void ShowAtScreenPoint(Native.POINT point)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero)
            {
                // 首次显示：先创建窗口（一次性，位置随后立即修正）
                Show();
                UpdateLayout();
                hwnd = new WindowInteropHelper(this).Handle;
            }

            if (!Native.GetWindowRect(hwnd, out var rect))
                return;

            var work = Native.GetMonitorWorkArea(point);
            var width = rect.Width;
            var height = rect.Height;

            var x = point.X + 14;
            var y = point.Y + 14;
            if (x + width > work.Right - 6) x = point.X - width - 14;
            if (y + height > work.Bottom - 6) y = point.Y - height - 14;
            x = Math.Max(work.Left + 6, Math.Min(x, work.Right - width - 6));
            y = Math.Max(work.Top + 6, Math.Min(y, work.Bottom - height - 6));

            // 一次性移到新位置：无论弹窗当前是隐藏还是可见都不会“闪现”。
            // （旧实现 Opacity=0 → Show → 挪 → Opacity=1，连续取词时弹窗会在
            //   上一个位置和新位置之间闪跳。）
            Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, x, y, 0, 0,
                Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);

            if (!IsVisible) Show();
            UpdateLayout(); // SizeToContent 在新位置按新文本调整尺寸
        }

        /// <summary>屏幕坐标是否落在弹窗之外（外部点击监视用）。</summary>
        public bool IsOutside(Native.POINT pt)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (!Native.GetWindowRect(hwnd, out var rect)) return true;
            return pt.X < rect.Left || pt.X > rect.Right || pt.Y < rect.Top || pt.Y > rect.Bottom;
        }

        private void BuildTags(IEnumerable<TagItem> tags)
        {
            var list = tags?.ToList() ?? new List<TagItem>();
            EmptyHint.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            // 标签集合没变时只刷新勾选状态，不重建控件（每次取词重建 12+ 个
            // CheckBox 的开销在弹窗总耗时里占比不小）。
            var key = string.Join(",", list.Select(t => t.Id));
            if (_cachedBoxes.Count > 0 && key == _cachedTagKey &&
                TagPanel.Children.Count == _cachedBoxes.Count)
            {
                foreach (var box in _cachedBoxes)
                {
                    var id = ((TagItem)box.DataContext).Id;
                    var want = _selected.Contains(id);
                    if ((box.IsChecked == true) != want) box.IsChecked = want;
                }
                return;
            }

            TagPanel.Children.Clear();
            _cachedBoxes.Clear();
            _cachedTagKey = key;

            var defaultTags = list.Where(t => t.Builtin).OrderBy(BuiltinTags.OrderOf).ToList();
            var customTags = list.Where(t => !t.Builtin).ToList();

            // 默认标签：和自定义标签同高的小球（置顶图钉 / 红黄绿纯色小球）
            var urgencyBoxes = new List<CheckBox>();
            foreach (var tag in defaultTags)
            {
                var box = new CheckBox
                {
                    Style = (Style)FindResource("DefaultTagCheckBox"),
                    DataContext = tag,
                    Tag = tag,
                    IsChecked = _selected.Contains(tag.Id),
                    ToolTip = tag.Name
                };

                if (string.Equals(tag.Kind, BuiltinTags.KindUrgency, StringComparison.Ordinal))
                    urgencyBoxes.Add(box);

                box.Checked += (_, _) =>
                {
                    _selected.Add(tag.Id);
                    EmptyHint.Visibility = Visibility.Collapsed;
                    // 紧急程度只能选一个
                    if (string.Equals(tag.Kind, BuiltinTags.KindUrgency, StringComparison.Ordinal))
                    {
                        foreach (var other in urgencyBoxes)
                            if (!ReferenceEquals(other, box)) other.IsChecked = false;
                    }
                };
                box.Unchecked += (_, _) => _selected.Remove(tag.Id);
                TagPanel.Children.Add(box);
                _cachedBoxes.Add(box);
            }

            // 自定义标签：文字胶囊
            foreach (var tag in customTags)
            {
                var chip = new CheckBox
                {
                    Style = (Style)FindResource("TagChipCheckBox"),
                    DataContext = tag,
                    Tag = tag,
                    IsChecked = _selected.Contains(tag.Id),
                    ToolTip = tag.Name
                };
                chip.Checked += (_, _) =>
                {
                    _selected.Add(tag.Id);
                    EmptyHint.Visibility = Visibility.Collapsed;
                };
                chip.Unchecked += (_, _) => _selected.Remove(tag.Id);
                TagPanel.Children.Add(chip);
                _cachedBoxes.Add(chip);
            }
        }

        /// <summary>
        /// 保存：文本框内容如果被修改过，即使没有选标签也要保存修改后的内容；
        /// 内容没改、也没选标签时才直接关闭。
        /// </summary>
        public void CommitAndClose()
        {
            if (_closing) return;
            _closing = true;

            var edited = (ContentBox.Text ?? "").Trim('\r', '\n', ' ', '\t');
            var original = (_originalText ?? "").Trim('\r', '\n', ' ', '\t');
            var textChanged = edited.Length > 0 &&
                              !string.Equals(edited, original, StringComparison.Ordinal);

            if (_selected.Count == 0 && !textChanged)
            {
                HideAndNotify();
                return;
            }

            var text = edited.Length == 0 ? _originalText : edited;
            Saved?.Invoke(text, _selected.ToList());
            HideAndNotify();
        }

        /// <summary>Outside click: auto-save when enabled, otherwise dismiss without saving.</summary>
        public void OutsideClick()
        {
            if (_closing) return;
            if (_autoSave) CommitAndClose();
            else
            {
                _closing = true;
                HideAndNotify();
            }
        }

        private void Save_Click(object sender, RoutedEventArgs e) => CommitAndClose();

        private void Window_Deactivated(object sender, EventArgs e)
        {
            // 未进入交互模式时弹窗从没激活过，不会收到 Deactivated；
            // 交互模式下点外面 = 放弃/自动保存，与旧行为一致。
            if (_closing || _closeScheduled || !_interactive) return;
            _closeScheduled = true;
            var version = _showVersion;
            // Defer so the activation change finishes first (see TagEditorWindow).
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _closeScheduled = false;
                if (version != _showVersion) return;
                OutsideClick();
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        private void HideAndNotify()
        {
            Hidden?.Invoke();
            Hide();
        }
    }
}
