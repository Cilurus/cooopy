using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Cooopy.Models;

namespace Cooopy.Windows
{
    public partial class TagEditorWindow : Window
    {
        private bool _loading;
        private bool _closing;
        private bool _closeScheduled;
        private string _color = ColorUtil.Palette[0];

        public string TagName { get; private set; }
        public string TagColor { get; private set; }

        /// <summary>Raised when the user confirms with a valid name/colour.</summary>
        public event Action<string, string> Saved;

        public TagEditorWindow(TagItem existing)
        {
            InitializeComponent();
            Title = existing != null ? "编辑标签" : "新建标签";
            _loading = true;

            if (existing != null)
            {
                TitleText.Text = "编辑标签";
                Title = "编辑标签";
                NameBox.Text = existing.Name;
                _color = existing.Color;
            }

            BuildSwatches();
            SetColor(_color);
            _loading = false;

            Loaded += (_, _) =>
            {
                NameBox.Focus();
                NameBox.SelectAll();
            };

            PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.Escape)
                {
                    _closing = true;
                    Close();
                    e.Handled = true;
                    return;
                }

                if (e.Key == Key.Enter)
                {
                    Save_Click(null, null);
                    e.Handled = true;
                }
            };

            Deactivated += (_, _) =>
            {
                if (_closing || _closeScheduled) return;
                _closeScheduled = true;
                // Let the activation change caused by the outside click finish first.
                // Closing synchronously inside Deactivated hands activation back to the
                // previously active window, which makes the main window drop one layer.
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    _closeScheduled = false;
                    if (_closing) return;
                    _closing = true;
                    Close();
                }), System.Windows.Threading.DispatcherPriority.Background);
            };

        }

        private void BuildSwatches()
        {
            var index = 0;
            foreach (var hex in ColorUtil.Palette)
            {
                var swatch = new RadioButton
                {
                    Style = (Style)FindResource("Swatch"),
                    Background = ColorUtil.Solid(hex),
                    Tag = hex,
                    GroupName = "tagcolor",
                    // 8 per row; even gaps and the last swatch of each row sits
                    // flush with the text box on the right.
                    Margin = new Thickness(0, 0, index % 8 == 7 ? 0 : 17.4, 10)
                };
                index++;
                swatch.Checked += (s, _) =>
                {
                    if (_loading) return;
                    _color = (string)((RadioButton)s).Tag;
                    PreviewDot.Background = ColorUtil.Solid(_color);
                    _loading = true;
                    HexBox.Text = _color;
                    _loading = false;
                };
                SwatchPanel.Children.Add(swatch);
            }
        }

        private void SetColor(string hex)
        {
            _color = hex;
            PreviewDot.Background = ColorUtil.Solid(hex);
            _loading = true;
            HexBox.Text = hex;
            _loading = false;

            foreach (var child in SwatchPanel.Children)
            {
                if (child is RadioButton button && button.Tag is string swatchHex)
                    button.IsChecked = string.Equals(swatchHex, hex, StringComparison.OrdinalIgnoreCase);
            }
        }

        private void HexBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_loading) return;
            var text = HexBox.Text ?? "";
            if (!text.StartsWith('#')) text = "#" + text;
            if (!Regex.IsMatch(text, "^#[0-9a-fA-F]{6}$")) return;
            _color = text.ToUpperInvariant();
            PreviewDot.Background = ColorUtil.Solid(_color);
            foreach (var child in SwatchPanel.Children)
            {
                if (child is RadioButton button && button.IsChecked == true)
                {
                    _loading = true;
                    button.IsChecked = false;
                    _loading = false;
                }
            }
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            var name = (NameBox.Text ?? "").Trim();
            if (name.Length == 0)
            {
                ErrorText.Text = "请填写标签名称";
                ErrorText.Visibility = Visibility.Visible;
                return;
            }

            if (!Regex.IsMatch(_color, "^#[0-9a-fA-F]{6}$"))
            {
                ErrorText.Text = "颜色格式应为 #RRGGBB";
                ErrorText.Visibility = Visibility.Visible;
                return;
            }

            TagName = name;
            TagColor = _color.ToUpperInvariant();
            _closing = true;
            Saved?.Invoke(TagName, TagColor);
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
