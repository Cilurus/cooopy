using System.IO;
using System.Windows;
using Cooopy.Models;

namespace Cooopy.Services
{
    public class ClipboardService
    {
        private readonly DataStore _store;
        private string _lastKnownText;
        private uint _lastSequence;

        public event Action<RecordItem> Captured;
        public event Action<string> Suppressed;

        public ClipboardService(DataStore store)
        {
            _store = store;
        }

        public void Attach(IntPtr hwnd) => Native.AddClipboardFormatListener(hwnd);

        public void Detach(IntPtr hwnd) => Native.RemoveClipboardFormatListener(hwnd);

        public static string ReadClipboardText()
        {
            // 剪贴板偶尔被其他进程占用；最多重试 4 次（约 40ms），避免调用方 UI 卡顿。
            for (var i = 0; i < 4; i++)
            {
                try
                {
                    if (Clipboard.ContainsText()) return Clipboard.GetText();
                    return null;
                }
                catch
                {
                    Thread.Sleep(10);
                }
            }
            return null;
        }

        public void SetClipboardText(string text)
        {
            try
            {
                Clipboard.SetText(text ?? string.Empty);
                _lastKnownText = text ?? string.Empty;
                _lastSequence = Native.GetClipboardSequenceNumber();
            }
            catch (Exception ex)
            {
                Suppressed?.Invoke("复制失败：" + ex.Message);
            }
        }

        /// <summary>Called from the window proc when WM_CLIPBOARDUPDATE arrives.</summary>
        public void OnClipboardUpdate()
        {
            var sequence = Native.GetClipboardSequenceNumber();
            if (sequence == _lastSequence) return;
            _lastSequence = sequence;

            var text = ReadClipboardText();
            if (string.IsNullOrEmpty(text)) return;

            // Ignore writes performed by cooopy itself.
            if (_lastKnownText != null && text == _lastKnownText) return;

            var settings = _store.Data.Settings;
            if (!settings.AutoCapture || settings.Paused) return;
            if (text.Length > settings.MaxLength) return;
            if (string.IsNullOrWhiteSpace(text)) return;

            // 只有真正准备入库时才记录文本；暂停/超长/关闭自动采集期间的内容
            // 不应影响恢复后再次复制同一段文字。
            _lastKnownText = text;

            var record = _store.AddRecord(text);
            if (record != null) Captured?.Invoke(record);
        }
    }
}
