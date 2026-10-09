using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VPet.Plugin.WorkingPet;

/// <summary>
/// 把数据同步到 Steam 云. 采集数据会无限增长, 而 Steam 云对文件个数/总大小有配额, 所以:
///   · 按月打成一个 zip (文本压缩后很小), 文件个数 = 月数 + 1
///   · 只有"本地有变化"的月份才重新上传; 已经过去的月份不会再动
/// 多台电脑: 每次先把云上别的电脑传的新内容合并进本地 (jsonl 按行去重合并, 工作记录按条去重),
/// 再把合并后的结果传上去. 用 SHA 记住"云上这份是不是我自己传的", 所以不会把你删掉的记录又合并回来.
/// 云上的文件位于 WorkingPetCloud/ 下, 与 VPet 自己的存档 (VPetCloud/) 互不干扰.
/// </summary>
public class SteamCloudSync
{
    private const string Prefix = "WorkingPetCloud/";
    private const string WorkLogName = Prefix + "work_log.json";
    private static readonly string[] Subs = { "samples", "activity", "vision", "apps" };
    private static readonly Regex MonthRegex = new(@"^(?<m>\d{4}-\d{2})-\d{2}", RegexOptions.Compiled);
    private static readonly string StatePath = Path.Combine(DataPaths.AppDataRoot, "steam_sync.json");

    private readonly Func<string> root;
    private readonly Func<string> workLogPath;
    private readonly Func<string, int> importWorkLog;
    private readonly SemaphoreSlim gate = new(1, 1);
    private Dictionary<string, FileState> state = new();

    public class FileState
    {
        /// <summary>云上这个文件最后一次被我们处理过的内容摘要 (自己上传的或已合并过的)</summary>
        public string SeenSha { get; set; } = "";
        /// <summary>上次上传时本地对应文件的指纹; 没变就不用重传</summary>
        public string Fingerprint { get; set; } = "";
    }

    public SteamCloudSync(Func<string> root, Func<string> workLogPath, Func<string, int> importWorkLog)
    {
        this.root = root;
        this.workLogPath = workLogPath;
        this.importWorkLog = importWorkLog;
    }

    public async Task<SyncResult> SyncAsync()
    {
        if (!await gate.WaitAsync(0)) return new SyncResult(false, "上一次 Steam 同步还没结束");
        try
        {
            return await Task.Run(SyncCore);
        }
        catch (Exception e)
        {
            DebugLog.Write("Steam 云同步出错: " + e);
            return new SyncResult(false, "出错：" + e.Message);
        }
        finally
        {
            gate.Release();
        }
    }

    private SyncResult SyncCore()
    {
        try
        {
            if (!SteamApi.IsValid()) return new SyncResult(false, "Steam 没有运行，或游戏不是通过 Steam 启动的");
        }
        catch (Exception e) // 非 Steam 版: 没有 Facepunch.Steamworks 程序集
        {
            return new SyncResult(false, "无法使用 Steam 接口：" + e.Message);
        }

        LoadState();
        int merged = 0, uploaded = 0;

        // ① 先把云上别的电脑传的新内容合并进来
        var cloud = SteamApi.ListFiles(Prefix);
        foreach (var name in cloud)
        {
            var bytes = SteamApi.Read(name);
            if (bytes == null || bytes.Length == 0) continue;
            var sha = Sha(bytes);
            if (state.TryGetValue(name, out var st) && st.SeenSha == sha) continue; // 就是我们自己传的, 没有新内容
            try
            {
                MergeFromCloud(name, bytes);
                merged++;
            }
            catch (Exception e)
            {
                DebugLog.Write($"合并云文件 {name} 失败: {e.Message}");
                continue; // 合并失败就不记录 SeenSha, 下次再试
            }
            (state.TryGetValue(name, out var s2) ? s2 : state[name] = new FileState()).SeenSha = sha;
        }

        // ② 再把本地(含刚合并的)传上去
        var result = Upload(cloud, ref uploaded);
        SaveState();
        if (!result.Ok) return result;
        return new SyncResult(true, $"已同步（上传 {uploaded} 个文件，合并了 {merged} 个来自其他电脑的文件）");
    }

    // ── 上传 ─────────────────────────────────────────────────

    private SyncResult Upload(List<string> cloud, ref int uploaded)
    {
        // 工作记录: 单个小文件
        var wl = workLogPath();
        if (File.Exists(wl))
        {
            var fi = new FileInfo(wl);
            var fp = $"{fi.Length}:{fi.LastWriteTimeUtc.Ticks}";
            if (!(state.TryGetValue(WorkLogName, out var st) && st.Fingerprint == fp && cloud.Contains(WorkLogName)))
            {
                var bytes = File.ReadAllBytes(wl);
                if (!SteamApi.Write(WorkLogName, bytes)) return WriteFailed(WorkLogName, bytes.Length);
                state[WorkLogName] = new FileState { Fingerprint = fp, SeenSha = Sha(bytes) };
                uploaded++;
            }
        }

        // 采集数据: 按月一个 zip
        var months = new SortedDictionary<string, List<(string Full, string Rel)>>();
        foreach (var sub in Subs)
        {
            var dir = Path.Combine(root(), sub);
            if (!Directory.Exists(dir)) continue;
            foreach (var f in Directory.EnumerateFiles(dir))
            {
                var m = MonthRegex.Match(Path.GetFileName(f));
                if (!m.Success) continue;
                var month = m.Groups["m"].Value;
                if (!months.TryGetValue(month, out var list)) months[month] = list = new();
                list.Add((f, sub + "/" + Path.GetFileName(f)));
            }
        }

        foreach (var (month, files) in months)
        {
            var name = $"{Prefix}data-{month}.zip";
            var fp = Fingerprint(files);
            if (state.TryGetValue(name, out var st) && st.Fingerprint == fp && cloud.Contains(name)) continue;

            byte[] bytes = null!;
            // 读文件期间挡住采集线程的追加, 保证 zip 里的内容是完整的行
            ActivityStore.Locked(() => bytes = BuildZip(files));
            if (!SteamApi.Write(name, bytes)) return WriteFailed(name, bytes.Length);
            state[name] = new FileState { Fingerprint = fp, SeenSha = Sha(bytes) };
            uploaded++;
        }
        return new SyncResult(true, "");
    }

    private static SyncResult WriteFailed(string name, int size) => new(false,
        $"写入 Steam 云失败（{name}，{size / 1024} KB）。可能是云空间配额用完了，或这台电脑的 Steam 里关闭了该游戏的云存档");

    private static byte[] BuildZip(List<(string Full, string Rel)> files)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (full, rel) in files)
            {
                var entry = zip.CreateEntry(rel, CompressionLevel.Optimal);
                using var es = entry.Open();
                var data = File.ReadAllBytes(full);
                es.Write(data, 0, data.Length);
            }
        }
        return ms.ToArray();
    }

    // ── 合并云上内容到本地 ─────────────────────────────────────

    private void MergeFromCloud(string name, byte[] bytes)
    {
        if (name == WorkLogName)
        {
            var tmp = Path.Combine(Path.GetTempPath(), "workingpet_cloud_work_log.json");
            File.WriteAllBytes(tmp, bytes);
            try { importWorkLog(tmp); }
            finally { File.Delete(tmp); }
            return;
        }
        if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) return;

        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        var rootFull = Path.GetFullPath(root());
        foreach (var entry in zip.Entries)
        {
            var rel = entry.FullName.Replace('\\', '/');
            if (entry.Length == 0 || rel.EndsWith('/')) continue;
            if (!Subs.Contains(rel.Split('/')[0])) continue;

            // 防止压缩包里的路径跳出数据目录
            var target = Path.GetFullPath(Path.Combine(rootFull, rel));
            if (!target.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;

            string remote;
            using (var reader = new StreamReader(entry.Open(), Encoding.UTF8))
                remote = reader.ReadToEnd();

            ActivityStore.Locked(() =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (!File.Exists(target))
                {
                    File.WriteAllText(target, remote);
                    return;
                }
                var local = File.ReadAllText(target);
                if (local == remote) return;
                var result = target.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)
                    ? MergeJsonl(local, remote)
                    : (UsageSum(remote) > UsageSum(local) ? remote : local); // 应用时长文件每台电脑各一份, 同名取较大的
                if (result != local) File.WriteAllText(target, result);
            });
        }
    }

    /// <summary>按行合并两份 jsonl: 去重, 并按每行里的 time 字段排序 (稳定排序, 没有 time 的保持相对顺序)</summary>
    internal static string MergeJsonl(string local, string remote)
    {
        var seen = new HashSet<string>();
        var rows = new List<(string Key, string Line)>();
        foreach (var raw in local.Split('\n').Concat(remote.Split('\n')))
        {
            var line = raw.Trim();
            if (line.Length == 0 || !seen.Add(line)) continue;
            rows.Add((TimeKey(line), line));
        }
        return string.Join("\n", rows.OrderBy(r => r.Key, StringComparer.Ordinal).Select(r => r.Line)) + "\n";
    }

    private static string TimeKey(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.TryGetProperty("time", out var t) && t.ValueKind == JsonValueKind.String)
                return t.GetString() ?? "";
        }
        catch (JsonException) { }
        return "";
    }

    private static double UsageSum(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, double>>(json)?.Values.Sum() ?? 0;
        }
        catch (JsonException)
        {
            return 0;
        }
    }

    // ── 状态 / 摘要 ───────────────────────────────────────────

    private static string Fingerprint(List<(string Full, string Rel)> files) =>
        Sha(Encoding.UTF8.GetBytes(string.Join("|", files.Select(f =>
        {
            var fi = new FileInfo(f.Full);
            return $"{f.Rel}:{fi.Length}:{fi.LastWriteTimeUtc.Ticks}";
        }))));

    private static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

    private void LoadState()
    {
        try
        {
            if (File.Exists(StatePath))
                state = JsonSerializer.Deserialize<Dictionary<string, FileState>>(File.ReadAllText(StatePath)) ?? new();
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            state = new(); // 状态丢了最多是多合并/多传一次, 不会丢数据
        }
    }

    private void SaveState()
    {
        try
        {
            Directory.CreateDirectory(DataPaths.AppDataRoot);
            File.WriteAllText(StatePath, JsonSerializer.Serialize(state));
        }
        catch (IOException e)
        {
            DebugLog.Write("保存 Steam 同步状态失败: " + e.Message);
        }
    }
}
