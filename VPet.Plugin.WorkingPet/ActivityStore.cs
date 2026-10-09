using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VPet.Plugin.WorkingPet;

/// <summary>窗口切换记录: 前台窗口变了才记一条</summary>
public class TitleSample
{
    [JsonPropertyName("time")] public string Time { get; set; } = "";
    [JsonPropertyName("process")] public string Process { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
}

/// <summary>活动摘要: AI 把约 2 分钟内的窗口标题总结成一句话 (summary 为空表示娱乐/无法判断), titles 是原始依据</summary>
public class ActivityEntry
{
    [JsonPropertyName("time")] public string Time { get; set; } = "";
    [JsonPropertyName("summary")] public string Summary { get; set; } = "";
    /// <summary>AI 对这段时间的判断: work 工作 / slack 摸鱼 / unknown 无法判断 (旧数据没有这个字段, 为空)</summary>
    [JsonPropertyName("state")] public string State { get; set; } = "";
    [JsonPropertyName("titles")] public List<string> Titles { get; set; } = new();
}

/// <summary>屏幕描述: 视觉模型对截图的一句话描述 (截图本身不保存)</summary>
public class VisionEntry
{
    [JsonPropertyName("time")] public string Time { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
}

/// <summary>某一天的全部采集数据, 供日报/周报使用</summary>
public record DaySnapshot(
    List<ActivityEntry> Activity,
    List<VisionEntry> Vision,
    List<TitleSample> Titles,
    List<KeyValuePair<string, double>> Apps);

/// <summary>
/// 采集数据的读写 (不依赖 VPet/WPF). 按天分文件、只追加, 永久保留:
///   samples\yyyy-MM-dd.jsonl   窗口切换记录
///   activity\yyyy-MM-dd.jsonl  活动摘要
///   vision\yyyy-MM-dd.jsonl    屏幕描述
///   apps\yyyy-MM-dd.json       当天各应用使用秒数
/// 追加式 + 按天拆文件, 放进 git 时几乎不会冲突, 历史也不会反复改写.
/// </summary>
public class ActivityStore
{
    private static readonly JsonSerializerOptions Options = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly object Gate = new();
    private readonly Func<string> root;

    public ActivityStore(Func<string> root) => this.root = root;

    /// <summary>应用时长文件按电脑分开存 (文件名带电脑名), 你手动把多台电脑的数据合并到一起时互不覆盖, 读取时再相加</summary>
    private static readonly string MachineId =
        new string(Environment.MachineName.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());

    private string File_(string sub, DateTime day, string ext) =>
        Path.Combine(root(), sub, $"{WorkLogStore.DayKey(day)}.{ext}");

    // ── 写 ───────────────────────────────────────────────────

    public void AppendSample(DateTime day, TitleSample s) => Append("samples", day, s);
    public void AppendActivity(DateTime day, ActivityEntry e) => Append("activity", day, e);
    public void AppendVision(DateTime day, VisionEntry e) => Append("vision", day, e);

    private void Append<T>(string sub, DateTime day, T item)
    {
        var path = File_(sub, day, "jsonl");
        lock (Gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, JsonSerializer.Serialize(item, Options) + "\n");
        }
    }

    public void WriteUsage(DateTime day, Dictionary<string, double> usage)
    {
        var path = File_("apps", day, MachineId + ".json");
        lock (Gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(
                usage.OrderByDescending(kv => kv.Value).ToDictionary(kv => kv.Key, kv => Math.Round(kv.Value)),
                new JsonSerializerOptions(Options) { WriteIndented = true }));
            File.Move(tmp, path, overwrite: true);
        }
    }

    // ── 读 ───────────────────────────────────────────────────

    public List<TitleSample> ReadSamples(DateTime day) => ReadJsonl<TitleSample>(File_("samples", day, "jsonl"));
    public List<ActivityEntry> ReadActivity(DateTime day) => ReadJsonl<ActivityEntry>(File_("activity", day, "jsonl"));
    public List<VisionEntry> ReadVision(DateTime day) => ReadJsonl<VisionEntry>(File_("vision", day, "jsonl"));

    /// <summary>这台电脑当天的应用时长 (重启游戏时接着累计用)</summary>
    public Dictionary<string, double> ReadUsage(DateTime day)
    {
        var path = File_("apps", day, MachineId + ".json");
        try
        {
            lock (Gate)
            {
                if (File.Exists(path))
                    return JsonSerializer.Deserialize<Dictionary<string, double>>(File.ReadAllText(path), Options) ?? new();
            }
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            DebugLog.Write("读取应用时长失败: " + e.Message);
        }
        return new();
    }

    /// <summary>
    /// 当天"工作 / 摸鱼 / 无法判断"各多少分钟. 每条活动摘要约代表 ActivityCollector.SummaryMinutes 分钟
    /// (人离开电脑的时段不会产生记录, 所以不计入), 是个估算值.
    /// </summary>
    public (int Work, int Slack, int Unknown) DayStats(DateTime day)
    {
        int work = 0, slack = 0, unknown = 0;
        foreach (var e in ReadActivity(day))
        {
            switch (e.State)
            {
                case "work": work++; break;
                case "slack": slack++; break;
                default: unknown++; break;
            }
        }
        int m = ActivityCollector.SummaryMinutes;
        return (work * m, slack * m, unknown * m);
    }

    /// <summary>当天所有电脑的应用时长之和</summary>
    public Dictionary<string, double> ReadAllUsage(DateTime day)
    {
        var total = new Dictionary<string, double>();
        var dir = Path.Combine(root(), "apps");
        if (!Directory.Exists(dir)) return total;
        foreach (var file in Directory.EnumerateFiles(dir, WorkLogStore.DayKey(day) + "*.json"))
        {
            try
            {
                Dictionary<string, double>? one;
                lock (Gate) one = JsonSerializer.Deserialize<Dictionary<string, double>>(File.ReadAllText(file), Options);
                if (one == null) continue;
                foreach (var (app, sec) in one) total[app] = total.GetValueOrDefault(app) + sec;
            }
            catch (Exception e) when (e is JsonException or IOException)
            {
                DebugLog.Write("读取应用时长失败: " + e.Message);
            }
        }
        return total;
    }

    public DaySnapshot Snapshot(DateTime day) => new(
        ReadActivity(day), ReadVision(day), ReadSamples(day),
        ReadAllUsage(day).OrderByDescending(kv => kv.Value).ToList());

    /// <summary>某天的活动摘要 (去掉空的, 连续重复的合并), 供周报给"没写日报的那天"兜底</summary>
    public List<string> DaySummaries(DateTime day)
    {
        var result = new List<string>();
        foreach (var e in ReadActivity(day))
        {
            var s = e.Summary.Trim();
            if (s.Length == 0 || (result.Count > 0 && result[^1] == s)) continue;
            result.Add(s);
        }
        return result;
    }

    private static List<T> ReadJsonl<T>(string path)
    {
        var result = new List<T>();
        lock (Gate)
        {
            if (!File.Exists(path)) return result;
            foreach (var line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var item = JsonSerializer.Deserialize<T>(line, Options);
                    if (item != null) result.Add(item);
                }
                catch (JsonException) { /* 半行/坏行直接跳过, 不影响其余数据 */ }
            }
        }
        return result;
    }
}
