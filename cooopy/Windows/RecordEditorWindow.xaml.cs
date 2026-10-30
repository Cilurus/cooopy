using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Cooopy.Models;
using Cooopy.Services;

namespace Cooopy.Windows
{
    public partial class RecordEditorWindow : Window
    {
        private readonly DataStore _store;
        private readonly HashSet<string> _selected;
        private bool _closing;
        private bool _closeScheduled;

        public event Action<string, List<string>> Saved;

        public RecordEditorWindow(DataStore store, RecordItem record)
        {
            InitializeComponent();
            _store = store;
            TextBox.Text = record.Text;
            _selected = new HashSet<string>(record.TagIds);
            BuildTags();

            Loaded += (_, _) =>
            {
                TextBox.Focus();
                TextBox.CaretIndex = TextBox.Text.Length;
            };

            PreviewKeyDown += (_, e) =>
            {
                if (e.Key != Key.Escape) return;
                _closing = true;
                Close();
                e.Handled = true;
            };
        }

        private void BuildTags()
        {
            TagPanel.Children.Clear();
            var tags = _store.Data.Tags
                .OrderBy(t => t.Builtin ? BuiltinTags.OrderOf(t) : 1000)
                .ThenByDescending(t => _store.CountRecordsForTag(t.Id))
                .ThenBy(t => t.Name)
                .ToList();
            EmptyTagHint.Visibility = tags.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            var urgencyBoxes = new List<CheckBox>();
            foreach (var tag in tags)
            {
                if (tag.Builtin)
                {
                    // 默认标签：小球（置顶图钉 / 红黄绿纯色）
                    var ball = new CheckBox
                    {
                        Style = (Style)FindResource("DefaultTagCheckBox"),
                        DataContext = tag,
                        Tag = tag,
                        IsChecked = _selected.Contains(tag.Id),
                        ToolTip = tag.Name
                    };

                    if (string.Equals(tag.Kind, BuiltinTags.KindUrgency, StringComparison.Ordinal))
                        urgencyBoxes.Add(ball);

                    ball.Checked += (_, _) =>
                    {
                        _selected.Add(tag.Id);
                        if (string.Equals(tag.Kind, BuiltinTags.KindUrgency, StringComparison.Ordinal))
                        {
                            foreach (var other in urgencyBoxes)
                                if (!ReferenceEquals(other, ball)) other.IsChecked = false;
                        }
                    };
                    ball.Unchecked += (_, _) => _selected.Remove(tag.Id);
                    TagPanel.Children.Add(ball);
                    continue;
                }

                var chip = new CheckBox
                {
                    Style = (Style)FindResource("TagChipCheckBox"),
                    DataContext = tag,
                    Tag = tag,
                    IsChecked = _selected.Contains(tag.Id),
                    ToolTip = tag.Name
                };
                chip.Checked += (_, _) => _selected.Add(tag.Id);
                chip.Unchecked += (_, _) => _selected.Remove(tag.Id);
                TagPanel.Children.Add(chip);
            }
        }

        /// <summary>Clicking anywhere outside the editor applies the changes.</summary>
        private void Window_Deactivated(object sender, EventArgs e)
        {
            if (_closing || _closeScheduled) return;
            _closeScheduled = true;
            // Defer the close so the activation change finishes first (see TagEditorWindow).
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _closeScheduled = false;
                SaveAndClose();
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        private void Save_Click(object sender, RoutedEventArgs e) => SaveAndClose();

        private void SaveAndClose()
        {
            if (_closing) return;

            var text = (TextBox.Text ?? "").Trim('\r', '\n', ' ', '\t');
            if (text.Length == 0)
            {
                // Do not wipe the record with empty content.
                _closing = true;
                Close();
                return;
            }

            _closing = true;
            Saved?.Invoke(text, _selected.ToList());
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            _closing = true;
            Close();
        }

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        }
    }
}
