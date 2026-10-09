using System.Globalization;
using System.Text;
using System.Text.Json;

namespace VPet.Plugin.WorkingPet;

/// <summary>
/// 提示词拼装与 AI 返回解析 (纯逻辑, 不联网, 便于单元测试). 移植自 work_log.py 的
/// generate_daily_summary / generate_weekly_report / understand_intent.
/// </summary>
public static class AiPrompts
{
    public const string WeeklyReportFormat = @"标题：周报（YYYY年MM月DD日 - YYYY年MM月DD日）

开头句式固定为：
本周（XX年XX月XX日 - XX年XX月XX日）共完成X项工作，主要涉及[不超过4个关键词领域]等方面，整体工作有序推进。

分项用""-""开头，格式：
- [小结词]：[内容]

语言平实不加修饰词。
下周计划和需协调事项若无则写""暂无具体安排""和""暂无""，不加额外说明。";

    private static readonly string[] WeekNames = { "一", "二", "三", "四", "五", "六", "日" };

    private static string Background(string background) =>
        string.IsNullOrWhiteSpace(background) ? "（未填写，请只根据记录本身归纳）" : background.Trim();

    // ── 日报 ─────────────────────────────────────────────────

    /// <summary>
    /// 日报提示词. 数据来源按可信度: 手动记录 &gt; AI 活动摘要 &gt; 屏幕描述 &gt; 应用时长 &gt; 窗口标题.
    /// 娱乐/闲聊等内容不在采集时过滤, 统一在这里交给 AI 判断剔除.
    /// </summary>
    public static string BuildDaily(DateTime now, IEnumerable<WorkEntry> manual, DaySnapshot data, string workBackground)
    {
        var manualText = JoinLines(manual.Select(e => $"  {e.Time} - {e.Content}"), "（无手动记录）");

        var summaries = new List<string>();
        string? prev = null;
        foreach (var e in data.Activity)
        {
            var sm = e.Summary.Trim();
            if (sm.Length == 0 || sm == prev) continue;
            summaries.Add($"  {e.Time} - {sm}");
            prev = sm;
        }
        var activityText = JoinLines(Tail(summaries, 80), "（无活动摘要）");

        var visionText = JoinLines(Tail(data.Vision.Select(v => $"  {v.Time} - {v.Description}").ToList(), 40), "（无屏幕描述）");

        var appsText = JoinLines(data.Apps.Take(10).Select(a => $"  {a.Key}  {Duration(a.Value)}"), "（无应用使用记录）");

        var titles = new List<string>();
        foreach (var t in data.Titles)
        {
            var line = t.Title.Length > 0 ? $"  {t.Time[..Math.Min(5, t.Time.Length)]} {t.Process} - {t.Title}" : $"  {t.Time[..Math.Min(5, t.Time.Length)]} {t.Process}";
            if (titles.Count == 0 || titles[^1] != line) titles.Add(line);
        }
        var windowText = JoinLines(Tail(titles, 60), "（无窗口记录）");

        return $@"今天是{now:yyyy年MM月dd日}，以下是用户今天的工作数据：

【用户手动记录（最权威，优先采用）】
{manualText}

【AI 活动摘要（每2分钟由AI根据窗口标题总结，可信度较高）】
{activityText}

【屏幕描述（每15-30分钟看一眼屏幕的一句话描述，仅供参考）】
{visionText}

【今日应用使用时长（前10，人离开电脑的时间不计）】
{appsText}

【窗口标题记录（仅供参考，只列出最近部分）】
{windowText}

请根据以上信息，提取今天实际完成的工作，生成日报草稿。

【用户工作背景】
{Background(workBackground)}

【严格过滤规则——以下内容绝对不写入日报】
- 锁屏、解锁、待机、电脑开关机等系统操作
- 与工作无关的即时通讯（微信/QQ 闲聊、刷朋友圈等）
- 游戏、游戏Wiki/攻略/论坛等娱乐内容
- 刷视频、看新闻、网购等个人行为
- 任何明显的非工作、非编程、非技术类活动
- 以上数据都是自动采集的，里面会混有大量娱乐和个人内容，请你自己判断并剔除

【输出要求】
- 每条工作一行，用""-""开头
- 优先采用「用户手动记录」的内容；其次是活动摘要；屏幕描述、应用时长、窗口标题只用于补充和佐证
- 格式：动词 + 项目/系统名 + 具体内容，语言简洁准确
- 合并同类项，去除重复，不逐条罗列
- 只输出工作条目，不加标题、日期、总结句、解释
- 最多8条；如果今天确实没有工作记录，只输出""（今日暂无工作记录）""

示例（好的格式）：
- 修复XX系统登录接口BUG
- 部署XX客户的服务并联调
- 处理服务器连接异常
- 编写XX模块技术文档";
    }

    // ── 周报 ─────────────────────────────────────────────────

    /// <param name="week">本周有记录的日期 → 条目, 见 WorkLogStore.GetWeek</param>
    /// <param name="activityFallback">某天没有手动记录/日报时, 用这一天的 AI 活动摘要兜底 (与旧版一致)</param>
    public static string BuildWeekly(DateTime now, IReadOnlyDictionary<string, List<WorkEntry>> week,
        Func<DateTime, IReadOnlyList<string>> activityFallback, string workBackground)
    {
        var monday = now.Date.AddDays(-(((int)now.DayOfWeek + 6) % 7));
        var blocks = new List<string>();
        for (var d = monday; d <= now.Date; d = d.AddDays(1))
        {
            var key = WorkLogStore.DayKey(d);
            var label = $"【{key} 周{WeekNames[((int)d.DayOfWeek + 6) % 7]}】";
            if (week.TryGetValue(key, out var entries) && entries.Count > 0)
            {
                blocks.Add(label + "\n" + string.Join("\n", entries.Select(e => $"  {e.Time} {e.Content}")));
                continue;
            }
            var fallback = activityFallback(d);
            blocks.Add(fallback.Count > 0
                ? label + "（来自活动记录，供参考）\n" + string.Join("\n", fallback.Select(x => "  " + x))
                : label + "\n  （无记录）");
        }

        string start = monday.ToString("yyyy年MM月dd日", CultureInfo.InvariantCulture);
        string end = now.ToString("yyyy年MM月dd日", CultureInfo.InvariantCulture);
        string weekday = WeekNames[((int)now.DayOfWeek + 6) % 7];

        return $@"以下是我本周每天的工作数据（周一到今天）：

{string.Join("\n\n", blocks)}

【我的工作背景】
{Background(workBackground)}

【过滤规则——以下内容绝对不写入周报】
锁屏/解锁/待机、与工作无关的即时通讯、游戏/娱乐内容、刷视频/购物等个人行为。

请根据以上数据生成周报，格式要求：

{WeeklyReportFormat}

本周时间范围：{start} - {end}
今天是{end}，星期{weekday}。
注意：标注「来自活动记录」的内容是自动采集的，请严格过滤非工作内容，合理归纳，不要原样照抄。
只输出周报正文，不要加任何解释。";
    }

    private static List<string> Tail(List<string> list, int max) =>
        list.Count <= max ? list : list.GetRange(list.Count - max, max);

    private static string Duration(double seconds)
    {
        int sec = (int)seconds;
        return sec >= 3600 ? $"{sec / 3600}小时{sec % 3600 / 60}分钟" : $"{Math.Max(1, sec / 60)}分钟";
    }

    // ── 工作 / 摸鱼 判断 ──────────────────────────────────────

    /// <summary>
    /// 把约 2 分钟内的窗口标题交给 AI, 同时判断状态和总结一句话 (一次调用, 不增加请求数).
    /// 宠物"陪伴模式"靠 state 决定做工作还是玩耍; summary 只在 work 时有内容, 供日报使用.
    /// </summary>
    public static string ActivityPrompt(IEnumerable<string> windowLines, string workBackground) => $@"以下是用户过去约2分钟内依次使用的窗口（进程名 - 标题）：
{string.Join("\n", windowLines.Select(l => "- " + l))}

【用户工作背景】
{Background(workBackground)}

请判断用户此刻是在工作还是在摸鱼，只返回JSON，格式：
{{""state"": ""work"", ""summary"": ""动词+具体内容""}}

state 取值：
- work：能明确看出在做具体的工作、编程、写文档、处理业务沟通、查阅技术资料等。summary 写成「动词 + 具体内容或项目名」，例如「调试订单系统的登录接口」「编写XX项目技术文档」
- slack：明显的娱乐、摸鱼，例如视频、游戏、购物、社交闲聊、刷资讯、看小说。summary 留空
- unknown：无法判断，例如只有桌面、锁屏、泛泛浏览网页、看不出内容。summary 留空

不要猜测；只返回JSON，不要任何解释。";

    /// <summary>解析 ActivityPrompt 的返回. 解析失败一律当 unknown, 不让宠物因为一次坏数据乱动</summary>
    public static (string State, string Summary) ParseActivity(string raw)
    {
        var text = raw.Replace("```json", "").Replace("```", "").Trim();
        int a = text.IndexOf('{'), b = text.LastIndexOf('}');
        if (a >= 0 && b > a) text = text[a..(b + 1)];
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            string state = root.TryGetProperty("state", out var st) ? (st.GetString() ?? "").Trim().ToLowerInvariant() : "";
            if (state != "work" && state != "slack") state = "unknown";
            string summary = state == "work" && root.TryGetProperty("summary", out var sm)
                ? (sm.GetString() ?? "").Trim().Trim('"', '「', '」', '“', '”')
                : "";
            return (state, summary);
        }
        catch (JsonException)
        {
            return ("unknown", "");
        }
    }

    // ── 意图识别 ─────────────────────────────────────────────

    /// <param name="petInfo">宠物此刻的状态 (名字/等级/心情/饥渴/在做什么/主人的状态), 让回复带上宠物自己的口吻</param>
    public static string IntentSystemPrompt(DateTime now, int todayCount, string petInfo) => $@"你是主人的桌面宠物，同时帮主人记录工作和生成周报。
今天是{now:yyyy年MM月dd日}，今天已有{todayCount}条工作记录。
{(string.IsNullOrWhiteSpace(petInfo) ? "" : "你现在的状态：" + petInfo + "\n闲聊回复要以宠物自己的口吻，并可以自然地结合这些状态（饿了就撒娇要吃的、累了就喊累、主人在摸鱼可以调皮地提醒），但不要罗列数据。")}

根据用户输入判断意图，只返回JSON，格式：
- 记录工作内容：{{""intent"": ""record"", ""content"": ""提炼后的工作内容""}}
- 生成/发送周报：{{""intent"": ""generate_report"", ""content"": """"}}
- 打开钉钉：{{""intent"": ""open_dingtalk"", ""content"": """"}}
- 其他对话：{{""intent"": ""chat"", ""content"": ""简短回复，不超过30字，口语化，可爱一点，像宠物在说话""}}

只返回JSON，不要任何解释。";

    /// <summary>
    /// 解析意图 JSON. AI 偶尔会包 ```json 围栏或多说几句, 所以先去围栏再截取最外层 {…};
    /// 实在解析不了就当闲聊, 把原文(截短)当回复, 不让用户看到报错.
    /// </summary>
    public static (string Intent, string Content) ParseIntent(string raw)
    {
        var text = raw.Replace("```json", "").Replace("```", "").Trim();
        int a = text.IndexOf('{'), b = text.LastIndexOf('}');
        if (a >= 0 && b > a) text = text[a..(b + 1)];
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            string intent = root.TryGetProperty("intent", out var i) ? i.GetString() ?? "chat" : "chat";
            string content = root.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";
            return (intent, content);
        }
        catch (JsonException)
        {
            var t = raw.Trim();
            return ("chat", t.Length > 60 ? t[..60] + "…" : t);
        }
    }

    // ── 日报草稿 <-> 条目 ─────────────────────────────────────

    /// <summary>把确认框里的文本转成条目: 去掉行首 "-", 空行忽略, 时间统一为确认时刻 (对应 DailyConfirmDialog.get_entries)</summary>
    public static List<WorkEntry> ParseDraft(string text, string time)
    {
        var result = new List<WorkEntry>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim().TrimStart('-', '－', '•').Trim();
            if (line.Length > 0) result.Add(new WorkEntry { Time = time, Content = line });
        }
        return result;
    }

    private static string JoinLines(IEnumerable<string> lines, string empty)
    {
        var sb = new StringBuilder();
        foreach (var l in lines) sb.AppendLine(l);
        return sb.Length == 0 ? empty : sb.ToString().TrimEnd();
    }
}
