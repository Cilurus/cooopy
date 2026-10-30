using System.Windows;
using System.Windows.Controls;
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
        private int _showVersion;

        /// <summary>这次弹窗对应的取词上下文，供 Saved 处理时还原剪贴板。</summary>
        public bool ContextCaptured { get; private set; }
        public string ContextPrevious { get; private set; }

        /// <summary>Raised with the edited text and the selected tag ids.</summary>
        public event Action<string, List<string>> Saved;

        public TagPopupWindow()
        {
            InitializeComponent();
        }

        /// <summary>复用同一个窗口实例，避免每次取词都重新创建 WPF 窗口和模板（明显降低弹窗延迟）。</summary>
        public void Reset(string text, IEnumerable<TagItem> tags, bool autoSave,
            bool captured, string previous)
        {
            _originalText = text ?? "";
            _autoSave = autoSave;
            ContextCaptured = captured;
            ContextPrevious = previous;
            _selected.Clear();
            _closing = false;
            _closeScheduled = false;
            _showVersion++;

            ContentBox.Text = _originalText;
            BuildTags(tags);
        }

        private void Window_SourceInitialized(object sender, EventArgs e)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var style = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
            style |= Native.WS_EX_TOOLWINDOW;
            Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, new IntPtr(style));
        }

        public void ShowAtScreenPoint(Native.POINT point)
        {
            Opacity = 0;
            Show();
            Activate();
            UpdateLayout();

            var hwnd = new WindowInteropHelper(this).Handle;
            if (!Native.GetWindowRect(hwnd, out var rect))
            {
                Opacity = 1;
                return;
            }

            var work = Native.GetMonitorWorkArea(point);
            var width = rect.Width;
            var height = rect.Height;

            var x = point.X + 14;
            var y = point.Y + 14;
            if (x + width > work.Right - 6) x = point.X - width - 14;
            if (y + height > work.Bottom - 6) y = point.Y - height - 14;
            x = Math.Max(work.Left + 6, Math.Min(x, work.Right - width - 6));
            y = Math.Max(work.Top + 6, Math.Min(y, work.Bottom - height - 6));

            Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, x, y, 0, 0,
                Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
            Opacity = 1;
        }

        public Native.RECT? GetPhysicalRect()
        {
            if (!IsVisible) return null;
            var hwnd = new WindowInteropHelper(this).Handle;
            return Native.GetWindowRect(hwnd, out var rect) ? rect : null;
        }

        private void BuildTags(IEnumerable<TagItem> tags)
        {
            TagPanel.Children.Clear();
            var list = tags?.ToList() ?? new List<TagItem>();
            var defaultTags = list.Where(t => t.Builtin).OrderBy(BuiltinTags.OrderOf).ToList();
            var customTags = list.Where(t => !t.Builtin).ToList();
            EmptyHint.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

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
                Hide();
                return;
            }

            var text = edited.Length == 0 ? _originalText : edited;
            Saved?.Invoke(text, _selected.ToList());
            Hide();
        }

        /// <summary>Outside click: auto-save when enabled, otherwise dismiss without saving.</summary>
        public void OutsideClick()
        {
            if (_closing) return;
            if (_autoSave) CommitAndClose();
            else
            {
                _closing = true;
                Hide();
            }
        }

        private void Save_Click(object sender, RoutedEventArgs e) => CommitAndClose();

        private void Window_Deactivated(object sender, EventArgs e)
        {
            if (_closing || _closeScheduled) return;
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
    }
}
