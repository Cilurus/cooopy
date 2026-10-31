using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Cooopy.Services
{
    public class TrayService : IDisposable
    {
        private readonly NotifyIcon _icon;
        private readonly ToolStripMenuItem _pauseItem;
        private Icon _generatedIcon;
        private IntPtr _iconHandle;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr handle);

        public event Action ShowMainRequested;
        public event Action QuickSaveRequested;
        public event Action PauseToggleRequested;
        public event Action SettingsRequested;
        public event Action ExitRequested;

        public TrayService()
        {
            var menu = new ContextMenuStrip
            {
                ShowImageMargin = false,
                Font = new Font("Microsoft YaHei UI", 9f)
            };

            var openItem = new ToolStripMenuItem("打开 cooopy");
            openItem.Font = new Font(openItem.Font, System.Drawing.FontStyle.Bold);
            openItem.Click += (_, _) => ShowMainRequested?.Invoke();

            var quickItem = new ToolStripMenuItem("快速打标当前剪贴板");
            quickItem.Click += (_, _) => QuickSaveRequested?.Invoke();

            _pauseItem = new ToolStripMenuItem("暂停记录 30 分钟");
            _pauseItem.Click += (_, _) => PauseToggleRequested?.Invoke();

            var settingsItem = new ToolStripMenuItem("设置…");
            settingsItem.Click += (_, _) => SettingsRequested?.Invoke();

            var exitItem = new ToolStripMenuItem("退出");
            exitItem.Click += (_, _) => ExitRequested?.Invoke();

            menu.Items.Add(openItem);
            menu.Items.Add(quickItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(_pauseItem);
            menu.Items.Add(settingsItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(exitItem);

            _generatedIcon = BuildIcon();
            _icon = new NotifyIcon
            {
                Icon = _generatedIcon,
                Text = "cooopy · 复制即入库",
                Visible = true,
                ContextMenuStrip = menu
            };
            _icon.DoubleClick += (_, _) => ShowMainRequested?.Invoke();
        }

        /// <summary>Explorer 重启导致任务栏重建后，重新显示托盘图标。</summary>
        public void EnsureVisible()
        {
            try
            {
                if (!_icon.Visible) _icon.Visible = true;
            }
            catch
            {
                // ignored
            }
        }

        public void SetPaused(bool paused)
        {
            _pauseItem.Text = paused ? "恢复记录" : "暂停记录 30 分钟";
            _icon.Text = paused ? "cooopy · 记录已暂停" : "cooopy · 复制即入库";
        }

        public void ShowBalloon(string title, string text, int timeout = 2200)
        {
            try
            {
                _icon.BalloonTipTitle = title;
                _icon.BalloonTipText = text;
                _icon.BalloonTipIcon = ToolTipIcon.None;
                _icon.ShowBalloonTip(timeout);
            }
            catch
            {
                // ignored
            }
        }

        private Icon BuildIcon()
        {
            using var bitmap = new Bitmap(32, 32);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);

                using var path = RoundedRect(new RectangleF(1.5f, 1.5f, 29f, 29f), 9f);
                using var gradient = new LinearGradientBrush(
                    new Rectangle(0, 0, 32, 32),
                    Color.FromArgb(74, 140, 255),
                    Color.FromArgb(38, 92, 205),
                    45f);
                g.FillPath(gradient, path);

                using var font = new Font("Segoe UI", 20f, System.Drawing.FontStyle.Bold, GraphicsUnit.Pixel);
                using var format = new StringFormat
                {
                    Alignment = StringAlignment.Center,
                    LineAlignment = StringAlignment.Center
                };
                g.DrawString("c", font, Brushes.White, new RectangleF(0, -2, 32, 32), format);
            }

            _iconHandle = bitmap.GetHicon();
            return Icon.FromHandle(_iconHandle);
        }

        private static GraphicsPath RoundedRect(RectangleF rect, float radius)
        {
            var path = new GraphicsPath();
            var d = radius * 2;
            path.AddArc(rect.X, rect.Y, d, d, 180, 90);
            path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        public void Dispose()
        {
            _icon.Visible = false;
            _icon.Dispose();
            _generatedIcon?.Dispose();
            if (_iconHandle != IntPtr.Zero)
            {
                DestroyIcon(_iconHandle);
                _iconHandle = IntPtr.Zero;
            }
        }
    }
}
