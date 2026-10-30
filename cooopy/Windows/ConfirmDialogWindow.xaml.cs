using System.Windows;
using System.Windows.Input;

namespace Cooopy.Windows
{
    public partial class ConfirmDialogWindow : Window
    {
        private bool _closing;
        private bool _closeScheduled;

        /// <summary>Raised only when the user presses the confirm button (or Enter).</summary>
        public event Action Confirmed;

        public ConfirmDialogWindow(string title, string message, string confirmText = "确定", bool danger = false)
        {
            InitializeComponent();
            Title = title;
            TitleText.Text = title;
            MessageText.Text = message ?? "";
            ConfirmButton.Content = confirmText;
            if (danger)
                ConfirmButton.Style = (Style)FindResource("DangerButton");

            PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.Escape)
                {
                    CloseAsCancel();
                    e.Handled = true;
                }
                else if (e.Key == Key.Enter)
                {
                    Confirm_Click(null, null);
                    e.Handled = true;
                }
            };

            // Click anywhere outside behaves exactly like the Cancel button.
            Deactivated += (_, _) =>
            {
                if (_closing || _closeScheduled) return;
                _closeScheduled = true;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    _closeScheduled = false;
                    if (_closing) return;
                    CloseAsCancel();
                }), System.Windows.Threading.DispatcherPriority.Background);
            };
        }

        private void CloseAsCancel()
        {
            if (_closing) return;
            _closing = true;
            Close();
        }

        private void Confirm_Click(object sender, RoutedEventArgs e)
        {
            if (_closing) return;
            _closing = true;
            Confirmed?.Invoke();
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => CloseAsCancel();

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        }
    }
}
