using System.Windows.Threading;

namespace VPet.Plugin.WorkingPet;

/// <summary>
/// 后台采集 (移植自旧版 context_collector 的 ActivityMonitor + ScreenshotCollector + data_collector 的应用时长):
/// · 每 30 秒记录前台窗口: 窗口切换写入 samples, 各应用使用秒数累计 (人离开电脑超过 5 分钟不计)
/// · 每 2 分钟让 AI 把这段时间的窗口标题总结成一句话 (娱乐/无法判断就留空), 写入 activity
/// · 每 15-30 分钟截一次屏, 让视觉模型用一句话描述, 写入 vision (图片不保存)
/// 全天采集、永久保留; 由日报/周报让 AI 自己过滤掉娱乐内容.
/// 定时器跑在 UI 线程, 联网和写文件较多的部分放到线程池.
/// </summary>
public class ActivityCollector
{
    private const double IdleLimitSeconds = 300;
    private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(30);
    /// <summary>每条活动摘要代表的分钟数 (统计工作/摸鱼时长用)</summary>
    public const int SummaryMinutes = 2;
    private static readonly TimeSpan SummaryInterval = TimeSpan.FromMinutes(SummaryMinutes);
    private static readonly TimeSpan FlushInterval = TimeSpan.FromMinutes(5);

    private readonly AiConfig cfg;
    private readonly ActivityStore store;
    private readonly DispatcherTimer timer;
    private readonly Random rnd = new();
    private readonly SemaphoreSlim summaryGate = new(1, 1);
    private readonly SemaphoreSlim visionGate = new(1, 1);

    // 上次摘要之后新采到的 (时间, 进程, 标题)
    private readonly List<(string Time, string Process, string Title)> recent = new();
    private (string Process, string Title)? last;
    private DateTime lastTick = DateTime.MinValue;
    private DateTime lastSummaryAt = DateTime.Now;
    private DateTime lastFlushAt = DateTime.Now;
    private DateTime nextVisionAt = DateTime.Now.AddMinutes(5); // 启动 5 分钟后第一次, 与旧版一致
    private DateTime visionPausedUntil = DateTime.MinValue;
    private int visionFailures;
    private DateTime day = DateTime.MinValue;
    private Dictionary<string, double> usage = new();

    /// <summary>每次 AI 给出判断后触发: "work" / "slack" / "unknown". 在线程池线程上触发, 订阅方自己切回 UI 线程</summary>
    public event Action<string>? StateClassified;

    public ActivityCollector(AiConfig cfg, ActivityStore store, Dispatcher dispatcher)
    {
        this.cfg = cfg;
        this.store = store;
        // 显式绑定 UI 线程的 Dispatcher (插件可能在非 UI 线程里创建, 默认绑定会让定时器永远不触发)
        timer = new DispatcherTimer(DispatcherPriority.Normal, dispatcher) { Interval = SampleInterval };
        timer.Tick += (_, _) => Tick();
    }

    public void Start() => timer.Start();

    public void Stop()
    {
        timer.Stop();
        FlushUsage();
    }

    private void Tick()
    {
        try
        {
            TickCore();
        }
        catch (Exception e)
        {
            DebugLog.Write("采集出错: " + e);
        }
    }

    private void TickCore()
    {
        if (!cfg.CollectActivity && !cfg.CollectVision) return;
        var now = DateTime.Now;
        RollDay(now);

        double elapsed = lastTick == DateTime.MinValue ? 0 : Math.Min(90, (now - lastTick).TotalSeconds);
        lastTick = now;

        // 人不在电脑前 (或锁屏): 不记录, 也不截屏
        if (WinNative.IdleSeconds() > IdleLimitSeconds)
        {
            last = null;
            return;
        }

        if (cfg.CollectActivity)
        {
            var (proc, title) = WinNative.Foreground();
            bool skip = proc.Length == 0
                || proc.Equals("LockApp", StringComparison.OrdinalIgnoreCase)
                || proc.Equals("LogonUI", StringComparison.OrdinalIgnoreCase)
                || proc.StartsWith("VPet-Simulator", StringComparison.OrdinalIgnoreCase); // 桌宠自己的窗口不算
            if (!skip)
            {
                usage[proc] = usage.GetValueOrDefault(proc) + elapsed;
                if (last is not { } l || l.Process != proc || l.Title != title)
                {
                    store.AppendSample(now, new TitleSample { Time = now.ToString("HH:mm:ss"), Process = proc, Title = title });
                    last = (proc, title);
                }
                recent.Add((now.ToString("HH:mm"), proc, title));
            }

            if (now - lastSummaryAt >= SummaryInterval)
            {
                lastSummaryAt = now;
                var batch = recent.ToList();
                recent.Clear();
                if (batch.Count > 0) _ = Task.Run(() => SummarizeAsync(now, batch));
            }
        }

        if (now - lastFlushAt >= FlushInterval) FlushUsage();

        if (cfg.CollectVision && cfg.IsConfigured && now >= nextVisionAt && now >= visionPausedUntil)
        {
            nextVisionAt = now.AddMinutes(rnd.Next(15, 31));
            _ = Task.Run(() => VisionAsync(now));
        }
    }

    // ── 按天累计应用时长 ──────────────────────────────────────

    private void RollDay(DateTime now)
    {
        if (day.Date == now.Date) return;
        FlushUsage();
        day = now.Date;
        usage = store.ReadUsage(now); // 同一天重启游戏时接着上次的数字累计
        last = null;
    }

    private void FlushUsage()
    {
        lastFlushAt = DateTime.Now;
        if (day == DateTime.MinValue || usage.Count == 0) return;
        try
        {
            store.WriteUsage(day, usage);
        }
        catch (Exception e)
        {
            DebugLog.Write("写应用时长失败: " + e.Message);
        }
    }

    // ── AI: 窗口标题 → 一句话 ──────────────────────────────────

    private async Task SummarizeAsync(DateTime at, List<(string Time, string Process, string Title)> batch)
    {
        // 连续相同的合并, 最多取最近 12 条
        var lines = new List<string>();
        foreach (var (_, proc, title) in batch)
        {
            var line = title.Length > 0 ? $"{proc} - {title}" : proc;
            if (lines.Count == 0 || lines[^1] != line) lines.Add(line);
        }
        if (lines.Count > 12) lines = lines.GetRange(lines.Count - 12, 12);

        string summary = "", state = "";
        if (cfg.IsConfigured)
        {
            await summaryGate.WaitAsync();
            try
            {
                var raw = await DoubaoClient.ChatAsync(cfg, new[] { ("user", AiPrompts.ActivityPrompt(lines, cfg.WorkBackground)) }, 120);
                (state, summary) = AiPrompts.ParseActivity(raw);
            }
            catch (Exception e)
            {
                DebugLog.Write("活动摘要失败: " + e.Message);
            }
            finally
            {
                summaryGate.Release();
            }
        }

        try
        {
            store.AppendActivity(at, new ActivityEntry { Time = at.ToString("HH:mm"), Summary = summary, State = state, Titles = lines });
        }
        catch (Exception e)
        {
            DebugLog.Write("写活动摘要失败: " + e.Message);
        }

        if (state.Length > 0)
        {
            try { StateClassified?.Invoke(state); }
            catch (Exception e) { DebugLog.Write("处理状态判断失败: " + e.Message); }
        }
    }

    // ── AI: 截图 → 一句话 ─────────────────────────────────────

    private async Task VisionAsync(DateTime at)
    {
        if (!await visionGate.WaitAsync(0)) return; // 上一次还没结束
        try
        {
            var images = ScreenCapture.CaptureAllJpegBase64();
            if (images.Count == 0) return;
            var prompt = images.Count == 1
                ? "请观察这张屏幕截图，用一句话（不超过40字）如实描述用户正在做什么，"
                : $"这是用户的 {images.Count} 块显示器截图（按从左到右的顺序），请综合观察，用一句话（不超过60字）如实描述用户正在做什么，" +
                  "如果多块屏幕上各有不同的事，简要都提到，";
            prompt += "例如「正在编写Java订单模块代码」「正在阅读需求文档」「正在看视频」。" +
                      "如果所有屏幕都只是桌面、锁屏或看不出任何具体操作，只输出「无」。" +
                      "只输出这一句话，不加任何前缀或解释。";
            var text = (await DoubaoClient.ChatVisionAsync(cfg, prompt, images, 200)).Trim();
            if (text.Length > 0 && text.TrimEnd('。', '.') != "无")
                store.AppendVision(at, new VisionEntry { Time = at.ToString("HH:mm"), Description = text });
            visionFailures = 0;
        }
        catch (Exception e)
        {
            // 接入点不支持图片/没网等: 连续失败 3 次就歇 6 小时, 免得一直报错和白白消耗
            DebugLog.Write("屏幕描述失败: " + e.Message);
            if (++visionFailures >= 3)
            {
                visionFailures = 0;
                visionPausedUntil = DateTime.Now.AddHours(6);
                DebugLog.Write("屏幕描述连续失败, 暂停 6 小时");
            }
        }
        finally
        {
            visionGate.Release();
        }
    }
}
