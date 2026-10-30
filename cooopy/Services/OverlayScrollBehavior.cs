using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace Cooopy.Services
{
    /// <summary>
    /// Turns a slim ScrollBar that lives inside a TextBox ControlTemplate into a floating
    /// (overlay) scrollbar: the ScrollViewer itself never shows a scrollbar, so the text area
    /// keeps its full width (no re-wrap when the content overflows) and the bar only appears
    /// while the content actually scrolls. Dragging the bar scrolls the TextBox through
    /// TextBoxBase.ScrollToVerticalOffset so the TextBox's own scroll state stays in sync.
    ///
    /// Template contract:
    ///   &lt;ScrollViewer x:Name="PART_ContentHost" VerticalScrollBarVisibility="Hidden" /&gt;
    ///   &lt;ScrollBar x:Name="OverlayScrollBar" Style="{StaticResource BubbleScrollBar}" /&gt;
    /// and on the TextBox: svc:OverlayScrollBehavior.Enabled="True".
    /// </summary>
    public static class OverlayScrollBehavior
    {
        public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
            "Enabled",
            typeof(bool),
            typeof(OverlayScrollBehavior),
            new PropertyMetadata(false, OnEnabledChanged));

        private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
            "State",
            typeof(ScrollState),
            typeof(OverlayScrollBehavior),
            new PropertyMetadata(null));

        private sealed class ScrollState
        {
            public TextBox Box;
            public ScrollViewer Scroll;
            public ScrollBar Bar;
            public bool Syncing;
        }

        public static bool GetEnabled(DependencyObject element) => (bool)element.GetValue(EnabledProperty);

        public static void SetEnabled(DependencyObject element, bool value) => element.SetValue(EnabledProperty, value);

        private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not TextBox box) return;

            if ((bool)e.NewValue)
            {
                box.Loaded += Box_Loaded;
                if (box.IsLoaded) Hook(box);
            }
            else
            {
                box.Loaded -= Box_Loaded;
            }
        }

        private static void Box_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox box) Hook(box);
        }

        private static void Box_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (sender is TextBox box && box.IsVisible)
            {
                box.IsVisibleChanged -= Box_IsVisibleChanged;
                Hook(box);
            }
        }

        private static void Hook(TextBox box)
        {
            if (box.GetValue(StateProperty) is ScrollState) return;

            // a TextBox that starts collapsed has no template yet - force it
            box.ApplyTemplate();

            var scroll = box.Template?.FindName("PART_ContentHost", box) as ScrollViewer;
            var bar = box.Template?.FindName("OverlayScrollBar", box) as ScrollBar;
            if (scroll == null || bar == null)
            {
                box.IsVisibleChanged -= Box_IsVisibleChanged;
                box.IsVisibleChanged += Box_IsVisibleChanged;
                return;
            }

            var state = new ScrollState { Box = box, Scroll = scroll, Bar = bar };
            box.SetValue(StateProperty, state);

            bar.ValueChanged += (_, _) =>
            {
                if (state.Syncing) return;
                box.ScrollToVerticalOffset(bar.Value);
            };

            scroll.ScrollChanged += (_, _) => Sync(state);
            box.SizeChanged += (_, _) => Sync(state);

            Sync(state);
        }

        private static void Sync(ScrollState state)
        {
            var box = state.Box;
            var bar = state.Bar;
            var scrollable = Math.Max(0, box.ExtentHeight - box.ViewportHeight);

            state.Syncing = true;
            try
            {
                bar.Maximum = scrollable;
                bar.ViewportSize = Math.Max(0, box.ViewportHeight);
                if (!bar.IsMouseCaptureWithin)
                    bar.Value = Math.Max(0, Math.Min(box.VerticalOffset, scrollable));
                bar.Visibility = scrollable > 0.5 ? Visibility.Visible : Visibility.Collapsed;
            }
            finally
            {
                state.Syncing = false;
            }
        }
    }
}
