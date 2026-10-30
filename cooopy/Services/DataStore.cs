using System.IO;
using System.Text;
using System.Text.Json;
using System.Timers;
using Cooopy.Models;

namespace Cooopy.Services
{
    public class DataStore
    {
        private readonly object _ioLock = new();
        private readonly System.Timers.Timer _saveTimer;
        private bool _savePending;
        private bool _loading;

        public AppData Data { get; private set; } = new();
        public string DataDir { get; }
        public string DataFile { get; }
        public string BackupFile { get; }
        public string LastLoadError { get; private set; }

        public event Action Changed;

        /// <summary>保存失败时触发，参数是错误描述。用于让界面能把“数据没存进去”告诉用户，
        /// 而不是只写进一个没人看的字段。</summary>
        public event Action<string> SaveFailed;

        /// <summary>数据目录当前能否写入。低完整性级别运行时（例如某些方式启动的进程）
        /// 这里会返回 false，表现为记录照常新增但一关窗口就丢。</summary>
        public bool CanWriteDataDir()
        {
            try
            {
                Directory.CreateDirectory(DataDir);
                var probe = Path.Combine(DataDir, $".write-probe-{Environment.ProcessId}");
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = false,
            PropertyNameCaseInsensitive = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        private static readonly JsonSerializerOptions JsonOptionsIndented = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        public DataStore()
        {
            DataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "cooopy");
            DataFile = Path.Combine(DataDir, "data.json");
            BackupFile = Path.Combine(DataDir, "data.backup.json");

            _saveTimer = new System.Timers.Timer(500) { AutoReset = false };
            _saveTimer.Elapsed += (_, _) => FlushPending();
        }

        public static DataStore Load()
        {
            var store = new DataStore();
            store.LoadInternal();
            if (store.EnsureBuiltinTags()) store.SaveNow();
            return store;
        }

        /// <summary>
        /// Makes sure the four built-in default tags exist (置顶 / 红 / 黄 / 绿) and keeps
        /// record pin & urgency state in sync with their tags. Returns true when data changed.
        /// </summary>
        public bool EnsureBuiltinTags()
        {
            var changed = false;
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            foreach (var def in BuiltinTags.All)
            {
                var tag = Data.Tags.FirstOrDefault(t => t.Id == def.Id);
                if (tag == null)
                {
                    Data.Tags.Add(new TagItem
                    {
                        Id = def.Id,
                        Name = def.Name,
                        Color = def.Color,
                        Kind = def.Kind,
                        Builtin = true,
                        CreatedAt = now
                    });
                    changed = true;
                }
                else
                {
                    if (!tag.Builtin) { tag.Builtin = true; changed = true; }
                    if (!string.Equals(tag.Kind, def.Kind, StringComparison.Ordinal)) { tag.Kind = def.Kind; changed = true; }
                }
            }

            foreach (var record in Data.Records)
                if (NormalizeRecordTags(record)) changed = true;

            return changed;
        }

        /// <summary>
        /// Keeps the built-in tag rules consistent: 置顶 drives Record.Pinned, and only one
        /// urgency (red / yellow / green) tag may stay on a record.
        /// </summary>
        public bool NormalizeRecordTags(RecordItem record)
        {
            if (record == null) return false;
            var changed = false;

            var pinned = record.TagIds.Contains(BuiltinTags.PinId);
            if (record.Pinned != pinned)
            {
                record.Pinned = pinned;
                changed = true;
            }

            var urgencies = record.TagIds.Where(BuiltinTags.IsUrgency).ToList();
            if (urgencies.Count > 1)
            {
                var keep = urgencies.OrderBy(BuiltinTags.OrderOfId).First();
                record.TagIds.RemoveAll(id => BuiltinTags.IsUrgency(id) && !string.Equals(id, keep, StringComparison.Ordinal));
                changed = true;
            }

            return changed;
        }

        private void LoadInternal()
        {
            _loading = true;
            try
            {
                Directory.CreateDirectory(DataDir);
                if (!File.Exists(DataFile))
                {
                    Data = new AppData();
                    return;
                }

                try
                {
                    var json = File.ReadAllText(DataFile, Encoding.UTF8);
                    var data = JsonSerializer.Deserialize<AppData>(json, JsonOptions);
                    if (data == null) throw new JsonException("empty document");
                    Data = data;
                    Data.Records ??= new List<RecordItem>();
                    Data.Tags ??= new List<TagItem>();
                    Data.Settings ??= new AppSettings();
                    Data.Records.RemoveAll(r => r == null);
                    Data.Tags.RemoveAll(t => t == null);
                    EnsureBackup();
                }
                catch (Exception ex)
                {
                    LastLoadError = ex.Message;
                    TryRecoverFromBackup();
                }
            }
            finally
            {
                _loading = false;
            }
        }

        private void TryRecoverFromBackup()
        {
            try
            {
                if (File.Exists(BackupFile))
                {
                    var json = File.ReadAllText(BackupFile, Encoding.UTF8);
                    var data = JsonSerializer.Deserialize<AppData>(json, JsonOptions);
                    if (data != null)
                    {
                        Data = data;
                        Data.Records ??= new List<RecordItem>();
                        Data.Tags ??= new List<TagItem>();
                        Data.Settings ??= new AppSettings();
                        LastLoadError += "（已从备份恢复）";
                        return;
                    }
                }
            }
            catch
            {
                // ignored
            }

            try
            {
                File.Copy(DataFile, DataFile + ".corrupt-" + DateTime.Now.ToString("yyyyMMddHHmmss"), true);
            }
            catch
            {
                // ignored
            }

            Data = new AppData();
            LastLoadError += "（已重建空库，原文件已另存）";
        }

        private void EnsureBackup()
        {
            try
            {
                var info = new FileInfo(DataFile);
                var backupInfo = new FileInfo(BackupFile);
                if (!backupInfo.Exists || info.LastWriteTime.Date > backupInfo.LastWriteTime.Date)
                    File.Copy(DataFile, BackupFile, true);
            }
            catch
            {
                // ignored
            }
        }

        public void Save() => ScheduleSave();

        public void ScheduleSave()
        {
            if (_loading) return;
            _savePending = true;
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        public void FlushPending()
        {
            if (!_savePending) return;
            _savePending = false;
            SaveNow();
        }

        public void SaveNow()
        {
            lock (_ioLock)
            {
                try
                {
                    Directory.CreateDirectory(DataDir);
                    var json = JsonSerializer.Serialize(Data, JsonOptions);
                    var tmp = DataFile + ".tmp";
                    File.WriteAllText(tmp, json, new UTF8Encoding(false));
                    if (File.Exists(DataFile)) File.Replace(tmp, DataFile, null);
                    else File.Move(tmp, DataFile);
                }
                catch (Exception ex)
                {
                    LastLoadError = "保存失败：" + ex.Message;
                    try { SaveFailed?.Invoke(LastLoadError); } catch { /* 通知失败不影响主流程 */ }
                }
            }
            RaiseChanged();
        }

        public void RaiseChanged() => Changed?.Invoke();

        // ---------------------------------------------------------------- records

        public RecordItem AddRecord(string text, IEnumerable<string> tagIds = null, bool applyDedupe = true)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            if (text.Length > Math.Max(1000, Data.Settings.MaxLength)) return null;

            text = text.Trim('\r', '\n', ' ', '\t');
            if (text.Length == 0) return null;

            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var dedupe = applyDedupe && Data.Settings.DedupeMinutes > 0;
            if (dedupe)
            {
                var cutoff = now - Data.Settings.DedupeMinutes * 60_000L;
                var existing = Data.Records.FirstOrDefault(r =>
                    !r.Deleted && r.CreatedAt >= cutoff && string.Equals(r.Text, text, StringComparison.Ordinal));
                if (existing != null)
                {
                    existing.CreatedAt = now;
                    existing.UpdatedAt = now;
                    ScheduleSave();
                    RaiseChanged();
                    return existing;
                }
            }

            var record = new RecordItem
            {
                Id = Guid.NewGuid().ToString("N"),
                Text = text,
                TagIds = tagIds?.Where(t => !string.IsNullOrEmpty(t)).Distinct().ToList() ?? new List<string>(),
                CreatedAt = now,
                UpdatedAt = now
            };
            NormalizeRecordTags(record);
            Data.Records.Add(record);
            ScheduleSave();
            RaiseChanged();
            return record;
        }

        public void UpdateRecord(RecordItem record, string newText, IEnumerable<string> tagIds)
        {
            if (record == null) return;
            if (newText != null)
            {
                record.Text = newText.Trim('\r', '\n', ' ', '\t');
            }
            if (tagIds != null)
            {
                record.TagIds = tagIds.Distinct().ToList();
                NormalizeRecordTags(record);
            }
            record.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            ScheduleSave();
            RaiseChanged();
        }

        public void DeleteRecord(RecordItem record)
        {
            if (record == null) return;
            record.Deleted = true;
            record.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            ScheduleSave();
            RaiseChanged();
        }

        public void TogglePin(RecordItem record)
        {
            if (record == null) return;
            record.Pinned = !record.Pinned;
            record.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            ScheduleSave();
            RaiseChanged();
        }

        public void TouchRecord(RecordItem record)
        {
            if (record == null) return;
            record.UseCount++;
            record.LastUsedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            ScheduleSave();
        }

        // ---------------------------------------------------------------- tags

        public TagItem FindTag(string id) => Data.Tags.FirstOrDefault(t => t.Id == id);

        public TagItem CreateTag(string name, string color)
        {
            name = (name ?? "").Trim();
            if (name.Length == 0) return null;
            if (name.Length > 12) name = name[..12];
            if (Data.Tags.Any(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)))
                return null;

            var tag = new TagItem
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = name,
                Color = string.IsNullOrWhiteSpace(color) ? ColorUtil.Palette[0] : color,
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
            Data.Tags.Add(tag);
            ScheduleSave();
            RaiseChanged();
            return tag;
        }

        public bool RenameOrRecolorTag(TagItem tag, string name, string color)
        {
            if (tag == null || tag.Builtin) return false;
            name = (name ?? "").Trim();
            if (name.Length == 0) return false;
            if (name.Length > 12) name = name[..12];
            if (Data.Tags.Any(t => t.Id != tag.Id && string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)))
                return false;
            tag.Name = name;
            if (!string.IsNullOrWhiteSpace(color)) tag.Color = color;
            ScheduleSave();
            RaiseChanged();
            return true;
        }

        public void DeleteTag(TagItem tag)
        {
            if (tag == null || tag.Builtin) return;
            Data.Tags.Remove(tag);
            foreach (var record in Data.Records)
                record.TagIds.Remove(tag.Id);
            ScheduleSave();
            RaiseChanged();
        }

        public int CountRecordsForTag(string tagId) =>
            Data.Records.Count(r => !r.Deleted && r.TagIds.Contains(tagId));

        // ---------------------------------------------------------------- settings

        public void UpdateSettings(Action<AppSettings> mutate)
        {
            mutate(Data.Settings);
            ScheduleSave();
            RaiseChanged();
        }

        // ---------------------------------------------------------------- import / export

        public string ExportJson(string path, bool onlySelected = false, IEnumerable<RecordItem> selected = null)
        {
            var records = onlySelected && selected != null
                ? selected.ToList()
                : Data.Records.Where(r => !r.Deleted).ToList();
            var tags = Data.Tags.ToList();

            var payload = new
            {
                schema_version = 1,
                app = "cooopy",
                exported_at = DateTimeOffset.Now.ToString("o"),
                tags,
                records
            };
            File.WriteAllText(path, JsonSerializer.Serialize(payload, JsonOptionsIndented), new UTF8Encoding(false));
            return path;
        }

        public string ExportMarkdown(string path)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# cooopy 导出（" + DateTime.Now.ToString("yyyy-MM-dd") + "）");
            sb.AppendLine();

            var records = Data.Records.Where(r => !r.Deleted)
                .OrderByDescending(r => r.CreatedAt).ToList();
            var groups = records.GroupBy(r => r.TagIds.Count == 0
                ? "未分类"
                : string.Join(" / ", r.TagIds.Select(id => FindTag(id)?.Name).Where(n => n != null)));

            foreach (var group in groups.OrderBy(g => g.Key == "未分类"))
            {
                sb.AppendLine("## " + group.Key);
                sb.AppendLine();
                foreach (var r in group)
                {
                    var firstLine = r.Text.Split('\n').FirstOrDefault()?.Trim() ?? "";
                    if (firstLine.Length > 60) firstLine = firstLine[..60] + "…";
                    sb.AppendLine("### " + (firstLine.Length == 0 ? "（空）" : firstLine));
                    sb.AppendLine("> " + r.LocalCreated.ToString("yyyy-MM-dd HH:mm"));
                    sb.AppendLine();
                    sb.AppendLine(r.Text);
                    sb.AppendLine();
                }
            }
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            return path;
        }

        public (int added, int updated, int skipped) ImportJson(string path)
        {
            var json = File.ReadAllText(path, Encoding.UTF8);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var importedTags = new List<TagItem>();
            var importedRecords = new List<RecordItem>();

            if (root.TryGetProperty("tags", out var tagsEl) && tagsEl.ValueKind == JsonValueKind.Array)
                importedTags = JsonSerializer.Deserialize<List<TagItem>>(tagsEl.GetRawText(), JsonOptions) ?? new();
            if (root.TryGetProperty("records", out var recEl) && recEl.ValueKind == JsonValueKind.Array)
                importedRecords = JsonSerializer.Deserialize<List<RecordItem>>(recEl.GetRawText(), JsonOptions) ?? new();

            int added = 0, updated = 0, skipped = 0;

            foreach (var tag in importedTags)
            {
                if (string.IsNullOrEmpty(tag.Id)) tag.Id = Guid.NewGuid().ToString("N");
                var existing = Data.Tags.FirstOrDefault(t => t.Id == tag.Id);
                if (existing == null)
                {
                    if (Data.Tags.Any(t => string.Equals(t.Name, tag.Name, StringComparison.OrdinalIgnoreCase)))
                        continue;

                    // 外部文件里自带的“内置标签”标记不可信：只有我们自己的固定 Id 才算默认标签，
                    // 其他一律按普通自定义标签导入，避免默认标签区被污染。
                    if (tag.Builtin && !BuiltinTags.All.Any(def => def.Id == tag.Id))
                    {
                        tag.Builtin = false;
                        tag.Kind = "";
                    }
                    Data.Tags.Add(tag);
                }
            }

            foreach (var record in importedRecords)
            {
                if (string.IsNullOrWhiteSpace(record.Text))
                {
                    skipped++;
                    continue;
                }
                if (string.IsNullOrEmpty(record.Id)) record.Id = Guid.NewGuid().ToString("N");
                var existing = Data.Records.FirstOrDefault(r => r.Id == record.Id);
                if (existing == null)
                {
                    record.TagIds = record.TagIds.Where(id => FindTag(id) != null).Distinct().ToList();
                    NormalizeRecordTags(record);
                    Data.Records.Add(record);
                    added++;
                }
                else if (record.UpdatedAt > existing.UpdatedAt)
                {
                    existing.Text = record.Text;
                    existing.UpdatedAt = record.UpdatedAt;
                    existing.TagIds = record.TagIds.Where(id => FindTag(id) != null).Distinct().ToList();
                    existing.Pinned = record.Pinned;
                    NormalizeRecordTags(existing);
                    updated++;
                }
                else
                {
                    skipped++;
                }
            }

            SaveNow();
            return (added, updated, skipped);
        }

        public void Shutdown()
        {
            _saveTimer.Stop();
            SaveNow();
        }
    }
}

