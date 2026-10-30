using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Media;

namespace Cooopy.Models
{
    public class RecordItem
    {
        public string Id { get; set; } = "";
        public string Text { get; set; } = "";
        public List<string> TagIds { get; set; } = new();
        public long CreatedAt { get; set; }
        public long UpdatedAt { get; set; }
        public long LastUsedAt { get; set; }
        public int UseCount { get; set; }
        public bool Pinned { get; set; }
        public bool Deleted { get; set; }

        [JsonIgnore]
        public DateTime LocalCreated => DateTimeOffset.FromUnixTimeMilliseconds(CreatedAt).LocalDateTime;
    }

    public class TagItem
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Color { get; set; } = "#2F6FEB";
        public long CreatedAt { get; set; }

        /// <summary>Built-in (default) tag: shown under "默认标签", cannot be edited or deleted.</summary>
        public bool Builtin { get; set; }

        /// <summary>"pin" (置顶) or "urgency" (紧急程度) for built-in tags; empty for custom tags.</summary>
        public string Kind { get; set; } = "";

        [JsonIgnore] public SolidColorBrush ChipBackground => ColorUtil.Solid(Color);
        [JsonIgnore] public SolidColorBrush ChipForeground => ColorUtil.ContrastText(Color);
        [JsonIgnore] public SolidColorBrush DotBrush => ColorUtil.Solid(Color);
    }

    /// <summary>Definitions and ordering of the built-in default tags.</summary>
    public static class BuiltinTags
    {
        public const string PinId = "builtin-pin";
        public const string UrgentRedId = "builtin-urgent-red";
        public const string UrgentYellowId = "builtin-urgent-yellow";
        public const string UrgentGreenId = "builtin-urgent-green";

        public const string KindPin = "pin";
        public const string KindUrgency = "urgency";

        public static readonly (string Id, string Name, string Color, string Kind)[] All =
        {
            (PinId, "置顶", "#2F6FEB", KindPin),
            (UrgentRedId, "紧急", "#E5484D", KindUrgency),
            (UrgentYellowId, "较急", "#F2A93B", KindUrgency),
            (UrgentGreenId, "不急", "#37A855", KindUrgency),
        };

        public static bool IsPin(string id) => string.Equals(id, PinId, StringComparison.Ordinal);

        public static bool IsUrgency(string id) =>
            id == UrgentRedId || id == UrgentYellowId || id == UrgentGreenId;

        /// <summary>Sort order for built-in tags; custom tags sort after them.</summary>
        public static int OrderOf(TagItem tag)
        {
            if (tag == null) return int.MaxValue;
            for (var i = 0; i < All.Length; i++)
                if (string.Equals(All[i].Id, tag.Id, StringComparison.Ordinal)) return i;
            return int.MaxValue;
        }

        public static int OrderOfId(string id)
        {
            for (var i = 0; i < All.Length; i++)
                if (string.Equals(All[i].Id, id, StringComparison.Ordinal)) return i;
            return int.MaxValue;
        }
    }

    public class AppSettings
    {
        public string HotkeyMain { get; set; } = "Ctrl+Alt+C";
        public string HotkeySave { get; set; } = "Ctrl+Alt+S";
        public bool AutoCapture { get; set; } = true;
        public int MaxLength { get; set; } = 20000;
        public int DedupeMinutes { get; set; } = 1440;
        public long PauseUntil { get; set; }
        public bool CloseToTray { get; set; } = true;
        public bool StartMinimized { get; set; } = false;
        public string SortMode { get; set; } = "time_desc";
        public bool HideUntagged { get; set; }
        public bool QuickTagAutoSave { get; set; } = true;
        public bool TextBoxShortcuts { get; set; } = true;
        public string DeepSeekApiUrl { get; set; } = "https://api.deepseek.com";
        public string DeepSeekApiKey { get; set; } = "";
        public string DeepSeekModel { get; set; } = "deepseek-chat";
        public bool AdvancedEnabled { get; set; }
        public string UiMode { get; set; } = "note";

        [JsonIgnore] public bool Paused => PauseUntil > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    public class AppData
    {
        public int SchemaVersion { get; set; } = 1;
        public List<RecordItem> Records { get; set; } = new();
        public List<TagItem> Tags { get; set; } = new();
        public AppSettings Settings { get; set; } = new();
    }

    /// <summary>Display row for the records list.</summary>
    public class RecordRow : INotifyPropertyChanged
    {
        public RecordItem Record { get; set; }
        public List<TagItem> Tags { get; set; } = new();
        public string TimeText { get; set; }
        public string Preview { get; set; }

        private string _editText;
        public string EditText
        {
            get => _editText;
            set
            {
                if (_editText == value) return;
                _editText = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EditText)));
            }
        }

        private List<TagEditOption> _editTags = new();
        public List<TagEditOption> EditTags
        {
            get => _editTags;
            set
            {
                if (ReferenceEquals(_editTags, value)) return;
                _editTags = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EditTags)));
            }
        }
        public bool Pinned => Record.Pinned;
        public Visibility PinVisibility => Record.Pinned ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>Urgency (red / yellow / green) ball shown next to the record time.</summary>
        public Brush UrgencyBrush { get; set; }
        public Visibility UrgencyVisibility { get; set; } = Visibility.Collapsed;

        private bool _isEditing;
        public bool IsEditing
        {
            get => _isEditing;
            set
            {
                if (_isEditing == value) return;
                _isEditing = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEditing)));
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
    }

    public class TagEditOption : INotifyPropertyChanged
    {
        public TagItem Tag { get; set; }

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value) return;
                _isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
    }
    public class TagRow
    {
        public TagItem Tag { get; set; }
        public int Count { get; set; }
        public string CountText => Count > 0 ? $"{Count} 条记录" : "未使用";
    }

    public class TagFilterOption : INotifyPropertyChanged
    {
        private bool _selected;
        public TagItem Tag { get; set; }
        public bool Selected
        {
            get => _selected;
            set { _selected = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Selected))); }
        }
        public event PropertyChangedEventHandler PropertyChanged;
    }

    public static class ColorUtil
    {
        public static Color Parse(string hex)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(hex)) return Colors.SteelBlue;
                hex = hex.Trim().TrimStart('#');
                if (hex.Length == 3)
                    hex = $"{hex[0]}{hex[0]}{hex[1]}{hex[1]}{hex[2]}{hex[2]}";
                if (hex.Length != 6) return Colors.SteelBlue;
                return Color.FromRgb(
                    Convert.ToByte(hex.Substring(0, 2), 16),
                    Convert.ToByte(hex.Substring(2, 2), 16),
                    Convert.ToByte(hex.Substring(4, 2), 16));
            }
            catch
            {
                return Colors.SteelBlue;
            }
        }

        public static SolidColorBrush Solid(string hex)
        {
            var brush = new SolidColorBrush(Parse(hex));
            brush.Freeze();
            return brush;
        }

        public static SolidColorBrush ContrastText(string hex)
        {
            var c = Parse(hex);
            var luminance = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
            var textColor = luminance > 0.62 ? Color.FromRgb(0x2D, 0x37, 0x48) : Colors.White;
            var brush = new SolidColorBrush(textColor);
            brush.Freeze();
            return brush;
        }

        public static SolidColorBrush Tint(string hex, double amount)
        {
            var c = Parse(hex);
            byte Mix(byte v) => (byte)Math.Round(v + (255 - v) * amount);
            var brush = new SolidColorBrush(Color.FromRgb(Mix(c.R), Mix(c.G), Mix(c.B)));
            brush.Freeze();
            return brush;
        }

        public static SolidColorBrush Darken(string hex, double amount)
        {
            var c = Parse(hex);
            byte Mix(byte v) => (byte)Math.Round(v * (1 - amount));
            var brush = new SolidColorBrush(Color.FromRgb(Mix(c.R), Mix(c.G), Mix(c.B)));
            brush.Freeze();
            return brush;
        }

        public static readonly string[] Palette =
        {
            "#2F6FEB", "#0E7C86", "#2E7D32", "#B7791F", "#C05621", "#C53030",
            "#B83280", "#6B46C1", "#4A5568", "#8B5E3C", "#2D3748", "#0F766E",
            "#0369A1", "#15803D", "#B45309", "#9333EA"
        };
    }
}
