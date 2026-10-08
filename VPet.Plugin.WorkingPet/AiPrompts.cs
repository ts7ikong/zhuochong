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

    public static string BuildDaily(DateTime now, IEnumerable<WorkEntry> manual,
        IEnumerable<(string Time, string Title)> windows, string workBackground)
    {
        var manualText = JoinLines(manual.Select(e => $"  {e.Time} - {e.Content}"), "（无手动记录）");
        var windowText = JoinLines(windows.Select(w => $"  {w.Time} [窗口标题] {w.Title}"), "（无窗口记录）");

        return $@"今天是{now:yyyy年MM月dd日}，以下是用户今天的工作数据：

【用户手动记录（最权威，优先采用）】
{manualText}

【窗口标题记录（每5分钟采样，仅供参考）】
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

【输出要求】
- 每条工作一行，用""-""开头
- 优先采用「用户手动记录」的内容，窗口标题仅用于补充手动记录未覆盖的工作
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
    public static string BuildWeekly(DateTime now, IReadOnlyDictionary<string, List<WorkEntry>> week, string workBackground)
    {
        var monday = now.Date.AddDays(-(((int)now.DayOfWeek + 6) % 7));
        var blocks = new List<string>();
        for (var d = monday; d <= now.Date; d = d.AddDays(1))
        {
            var key = WorkLogStore.DayKey(d);
            var label = $"【{key} 周{WeekNames[((int)d.DayOfWeek + 6) % 7]}】";
            blocks.Add(week.TryGetValue(key, out var entries) && entries.Count > 0
                ? label + "\n" + string.Join("\n", entries.Select(e => $"  {e.Time} {e.Content}"))
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
只输出周报正文，不要加任何解释。";
    }

    // ── 意图识别 ─────────────────────────────────────────────

    public static string IntentSystemPrompt(DateTime now, int todayCount) => $@"你是一个桌面宠物助手，帮用户记录工作和生成周报。
今天是{now:yyyy年MM月dd日}，今天已有{todayCount}条工作记录。

根据用户输入判断意图，只返回JSON，格式：
- 记录工作内容：{{""intent"": ""record"", ""content"": ""提炼后的工作内容""}}
- 生成/发送周报：{{""intent"": ""generate_report"", ""content"": """"}}
- 打开钉钉：{{""intent"": ""open_dingtalk"", ""content"": """"}}
- 其他对话：{{""intent"": ""chat"", ""content"": ""简短回复，不超过30字，口语化，可爱一点""}}

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
