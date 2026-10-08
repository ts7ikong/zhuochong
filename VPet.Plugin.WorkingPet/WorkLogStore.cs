using System.IO;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace VPet.Plugin.WorkingPet;

/// <summary>一条工作记录, JSON 字段名与旧版 work_log.json 一致 ({"time","content"})</summary>
public class WorkEntry
{
    [JsonPropertyName("time")] public string Time { get; set; } = "";
    [JsonPropertyName("content")] public string Content { get; set; } = "";
}

/// <summary>
/// 工作记录存储 (纯逻辑, 不依赖 VPet/WPF). 移植自 work_log.py:
/// 格式 {"2026-10-08": [{"time":"14:30","content":"..."}]}, 按日期分组.
/// 存放在 %AppData%\VPet-WorkingPet\work_log.json, 不放进 MOD 目录, 避免更新 MOD 时被覆盖.
/// </summary>
public class WorkLogStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // 中文不转成 \uXXXX, 文件可读
    };

    private static readonly Regex LineRegex = new(@"^(\d{1,2}):(\d{2})\s+(.*)$", RegexOptions.Compiled);

    private readonly object gate = new();
    private Dictionary<string, List<WorkEntry>> logs = new();

    public string FilePath { get; }

    public WorkLogStore(string? path = null)
    {
        FilePath = path ?? DefaultPath();
        Load();
    }

    public static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VPet-WorkingPet", "work_log.json");

    public static string DayKey(DateTime day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    // ── 读写 ─────────────────────────────────────────────────

    private void Load()
    {
        lock (gate)
        {
            logs = new();
            if (!File.Exists(FilePath)) return;
            try
            {
                var data = JsonSerializer.Deserialize<Dictionary<string, List<WorkEntry>>>(File.ReadAllText(FilePath), Options);
                if (data == null) return;
                foreach (var (k, v) in data)
                    if (v is { Count: > 0 }) logs[k] = v.Where(e => e != null).ToList();
            }
            catch (JsonException)
            {
                // 文件损坏: 改名留底而不是覆盖, 避免静默丢数据
                var bak = FilePath + ".corrupt-" + DateTime.Now.ToString("yyyyMMddHHmmss");
                File.Move(FilePath, bak);
                logs = new();
            }
        }
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        // 先写临时文件再替换, 写到一半崩溃也不会弄坏原文件
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(logs.OrderBy(kv => kv.Key).ToDictionary(kv => kv.Key, kv => kv.Value), Options));
        File.Move(tmp, FilePath, overwrite: true);
    }

    // ── 查询 ─────────────────────────────────────────────────

    public List<WorkEntry> GetDay(DateTime day)
    {
        lock (gate)
            return logs.TryGetValue(DayKey(day), out var l)
                ? l.Select(e => new WorkEntry { Time = e.Time, Content = e.Content }).ToList()
                : new();
    }

    public bool HasLog(DateTime day)
    {
        lock (gate) return logs.TryGetValue(DayKey(day), out var l) && l.Count > 0;
    }

    /// <summary>本周 (周一到 today) 有记录的日期, 对应 get_week_logs, 供周报使用</summary>
    public SortedDictionary<string, List<WorkEntry>> GetWeek(DateTime today)
    {
        var result = new SortedDictionary<string, List<WorkEntry>>();
        var monday = today.Date.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        for (var d = monday; d <= today.Date; d = d.AddDays(1))
        {
            var entries = GetDay(d);
            if (entries.Count > 0) result[DayKey(d)] = entries;
        }
        return result;
    }

    // ── 修改 ─────────────────────────────────────────────────

    /// <summary>追加一条到今天, 返回今天的总条数; 内容为空则忽略并返回 0</summary>
    public int Add(string text, DateTime? now = null)
    {
        text = text.Trim();
        if (text.Length == 0) return 0;
        var t = now ?? DateTime.Now;
        lock (gate)
        {
            var key = DayKey(t);
            if (!logs.TryGetValue(key, out var list)) logs[key] = list = new();
            list.Add(new WorkEntry { Time = t.ToString("HH:mm", CultureInfo.InvariantCulture), Content = text });
            Save();
            return list.Count;
        }
    }

    /// <summary>用给定条目覆盖某天的记录, 条目为空则删除这一天 (对应 replace_today_logs)</summary>
    public void ReplaceDay(DateTime day, IEnumerable<WorkEntry> entries)
    {
        var list = entries.Where(e => !string.IsNullOrWhiteSpace(e.Content)).ToList();
        lock (gate)
        {
            if (list.Count > 0) logs[DayKey(day)] = list;
            else logs.Remove(DayKey(day));
            Save();
        }
    }

    /// <summary>从旧版 work_log.json 合并导入, 已存在的 (同日期+时间+内容) 不重复添加, 返回新增条数</summary>
    public int ImportFrom(string file)
    {
        var data = JsonSerializer.Deserialize<Dictionary<string, List<WorkEntry>>>(File.ReadAllText(file), Options)
                   ?? throw new JsonException("文件内容为空");
        int added = 0;
        lock (gate)
        {
            foreach (var (day, entries) in data)
            {
                if (!DateTime.TryParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
                    || entries == null) continue;
                if (!logs.TryGetValue(day, out var list)) logs[day] = list = new();
                foreach (var e in entries)
                {
                    if (e == null || string.IsNullOrWhiteSpace(e.Content)) continue;
                    if (list.Any(x => x.Time == e.Time && x.Content == e.Content)) continue;
                    list.Add(new WorkEntry { Time = e.Time ?? "", Content = e.Content.Trim() });
                    added++;
                }
                if (list.Count == 0) logs.Remove(day);
            }
            if (added > 0) Save();
        }
        return added;
    }

    // ── 编辑框文本 <-> 条目 ─────────────────────────────────────

    /// <summary>
    /// 每行一条 "HH:MM  内容", 时间可省略 (用 defaultTime 补全), 空内容的行忽略. 对应 WorkCalendarDialog._save
    /// </summary>
    public static List<WorkEntry> ParseLines(string text, string defaultTime)
    {
        var result = new List<WorkEntry>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            string time = defaultTime, content = line;
            var m = LineRegex.Match(line);
            if (m.Success && int.Parse(m.Groups[1].Value) < 24 && int.Parse(m.Groups[2].Value) < 60)
            {
                time = $"{int.Parse(m.Groups[1].Value):00}:{m.Groups[2].Value}";
                content = m.Groups[3].Value.Trim();
            }
            if (content.Length > 0) result.Add(new WorkEntry { Time = time, Content = content });
        }
        return result;
    }

    public static string FormatLines(IEnumerable<WorkEntry> entries) =>
        string.Join(Environment.NewLine, entries.Select(e => $"{e.Time}  {e.Content}"));
}
