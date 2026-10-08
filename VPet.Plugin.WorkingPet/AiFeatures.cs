using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using VPet_Simulator.Windows.Interface;

namespace VPet.Plugin.WorkingPet;

/// <summary>
/// 第 3 步: 豆包 AI 对话(意图识别) / 周报 / 日报草稿确认 / 钉钉一键启动 / 周报提醒.
/// 所有联网都在后台 Task 里跑, 结果用 Dispatcher 切回 UI 线程再动界面 (对应旧版的 Bridge).
/// </summary>
public class AiFeatures
{
    private readonly IMainWindow mw;
    private readonly PluginSettings settings;
    private readonly Func<WorkLogStore> getStore;
    private readonly WindowTitleSampler sampler;
    // 同一天同一事件只提醒一次 (key = 日期 + 事件名)
    private readonly HashSet<string> fired = new();
    private bool busy;
    private AiSettingsWindow? settingsWindow;

    public AiConfig Config { get; } = AiConfig.Load();

    public AiFeatures(IMainWindow mw, PluginSettings settings, Func<WorkLogStore> getStore)
    {
        this.mw = mw;
        this.settings = settings;
        this.getStore = getStore;
        sampler = new WindowTitleSampler(() => settings.Schedule, mw.Dispatcher);
    }

    public void Start() => sampler.Start();
    public void Stop() => sampler.Stop();

    private WorkLogStore Store => getStore();
    private void Say(string text) => mw.Main.SayRnd(text);
    private void UI(Action action) => mw.Dispatcher.InvokeAsync(action);

    // ── 对话 (意图识别) ────────────────────────────────────────

    /// <summary>弹出输入框: 没配 AI 就直接当一条工作记录, 配了就让 AI 判断是记录/周报/钉钉/闲聊</summary>
    public void Chat()
    {
        mw.ShowInputBox("和宠物说话", "记录工作 / 生成周报 / 打开钉钉 / 随便说点什么…", "", OnUserInput);
    }

    private void OnUserInput(string input)
    {
        var text = input.Trim();
        if (text.Length == 0) return;

        if (!Config.IsConfigured)
        {
            Say($"已记录！今天共 {SafeAdd(text)} 条记录 ✓");
            return;
        }
        if (!TryBegin()) return;
        Say("让我想想…");
        var cfg = Config;
        _ = Task.Run(async () =>
        {
            try
            {
                var now = DateTime.Now;
                int todayCount = Store.GetDay(now).Count;
                var raw = await DoubaoClient.ChatAsync(cfg, new[]
                {
                    ("system", AiPrompts.IntentSystemPrompt(now, todayCount)),
                    ("user", text),
                }, 200);
                var (intent, content) = AiPrompts.ParseIntent(raw);

                switch (intent)
                {
                    case "record":
                        var n = SafeAdd(string.IsNullOrWhiteSpace(content) ? text : content);
                        UI(() => Say($"已记录！今天共 {n} 条记录 ✓"));
                        break;
                    case "generate_report":
                        var report = await BuildWeeklyAsync();
                        UI(() => ShowWeekly(report));
                        break;
                    case "open_dingtalk":
                        UI(() => { Say("好的，正在打开钉钉…"); LaunchDingTalk(); });
                        break;
                    default:
                        UI(() => Say(string.IsNullOrWhiteSpace(content) ? "嗯嗯～" : content));
                        break;
                }
            }
            catch (Exception e)
            {
                UI(() => Say("出错了：" + e.Message));
            }
            finally
            {
                busy = false;
            }
        });
    }

    private int SafeAdd(string text)
    {
        try { return Store.Add(text); }
        catch (Exception e) { UI(() => Say("记录失败：" + e.Message)); return 0; }
    }

    // ── 周报 ─────────────────────────────────────────────────

    public void GenerateWeekly()
    {
        if (!Config.IsConfigured)
        {
            Say("⚠️ 请先在「AI设置」里配置 API Key 和接入点");
            return;
        }
        if (!TryBegin()) return;
        Say("正在生成周报，稍等一下…");
        _ = Task.Run(async () =>
        {
            try
            {
                var report = await BuildWeeklyAsync();
                UI(() => ShowWeekly(report));
            }
            catch (Exception e)
            {
                UI(() => Say("周报生成失败：" + e.Message));
            }
            finally
            {
                busy = false;
            }
        });
    }

    private Task<string> BuildWeeklyAsync()
    {
        var now = DateTime.Now;
        var prompt = AiPrompts.BuildWeekly(now, Store.GetWeek(now), Config.WorkBackground);
        return DoubaoClient.ChatAsync(Config, new[] { ("user", prompt) }, 1200);
    }

    private void ShowWeekly(string report)
    {
        Say("周报已生成，请查看弹窗 📋");
        var win = new DraftWindow("📋 本周周报", "可直接编辑修改，点「复制到剪贴板」后粘贴到钉钉。", report,
            ("📋 复制到剪贴板", w => CopyToClipboard(w)),
            ("打开钉钉", _ => LaunchDingTalk()),
            ("关闭", w => w.Close()));
        WindowTracker.Track(mw, win);
        win.Show();
    }

    private static void CopyToClipboard(DraftWindow w)
    {
        try
        {
            Clipboard.SetText(w.Text);
            w.ShowStatus("✓ 已复制！");
        }
        catch (Exception e) // 剪贴板偶尔被别的程序占用
        {
            w.ShowStatus("复制失败：" + e.Message, false);
        }
    }

    // ── 日报草稿确认 ───────────────────────────────────────────

    /// <summary>生成今日日报草稿并弹出确认框; 确认后覆盖今天的记录. 没配 AI 就弹空白框让你手填</summary>
    public void DailyConfirm()
    {
        if (!Config.IsConfigured)
        {
            ShowDaily("（未配置AI，请手动填写今日工作内容）\n- ");
            return;
        }
        if (!TryBegin()) return;
        Say("正在整理今天的日报草稿…");
        var manual = Store.GetDay(DateTime.Now);
        var windows = sampler.Today();
        _ = Task.Run(async () =>
        {
            try
            {
                var prompt = AiPrompts.BuildDaily(DateTime.Now, manual, windows, Config.WorkBackground);
                var draft = await DoubaoClient.ChatAsync(Config, new[] { ("user", prompt) }, 600);
                UI(() => ShowDaily(draft));
            }
            catch (Exception e)
            {
                UI(() => ShowDaily($"（AI生成失败：{e.Message}）\n- "));
            }
            finally
            {
                busy = false;
            }
        });
    }

    private void ShowDaily(string draft)
    {
        var win = new DraftWindow("📋 今日工作日报", "AI 已根据你今天的记录生成草稿，可直接编辑，确认后会【覆盖】今天已有的记录。", draft,
            ("跳过（不保存）", w => w.Close()),
            ("✅ 确认保存", w =>
            {
                var entries = AiPrompts.ParseDraft(w.Text, DateTime.Now.ToString("HH:mm"));
                try
                {
                    Store.ReplaceDay(DateTime.Today, entries);
                }
                catch (Exception e)
                {
                    w.ShowStatus("保存失败：" + e.Message, false);
                    return;
                }
                Say(entries.Count > 0 ? $"日报已保存，共 {entries.Count} 条 ✓" : "已清空今天的记录");
                w.Close();
            }));
        WindowTracker.Track(mw, win);
        win.Show();
    }

    // ── 钉钉 ─────────────────────────────────────────────────

    public void LaunchDingTalk()
    {
        try
        {
            var path = Config.DingTalkPath;
            // 配了路径就用路径, 否则交给系统在 PATH 里找 DingTalk.exe
            Process.Start(new ProcessStartInfo(!string.IsNullOrWhiteSpace(path) && System.IO.File.Exists(path) ? path : "DingTalk.exe")
            {
                UseShellExecute = true,
            });
            Say("已打开钉钉 ✓");
        }
        catch (Exception)
        {
            Say("找不到钉钉，请在「AI设置」里配置钉钉路径");
        }
    }

    // ── 设置窗口 ──────────────────────────────────────────────

    public void OpenSettings()
    {
        if (settingsWindow != null)
        {
            settingsWindow.Activate();
            return;
        }
        settingsWindow = new AiSettingsWindow(Config, () => fired.Clear());
        var win = settingsWindow;
        win.Closed += (_, _) => settingsWindow = null;
        WindowTracker.Track(mw, win);
        win.Show();
    }

    // ── 定时提醒 (由插件的 30 秒定时器调用) ─────────────────────

    public void CheckReminders(DateTime now)
    {
        var t = now.TimeOfDay;
        var s = settings.Schedule;
        string day = now.ToString("yyyyMMdd");

        // 日报确认: 下班前 10 分钟起, 每天一次
        if (Config.DailyConfirm && t >= s.PmEnd - TimeSpan.FromMinutes(10) && t < s.PmEnd && fired.Add(day + "daily"))
            DailyConfirm();

        // 周报提醒: 到设定的星期和时间后 1 小时内提醒一次, 气泡里带按钮
        if (((int)now.DayOfWeek + 6) % 7 == Config.RemindDay
            && WorkSchedule.TryParseTime(Config.RemindTime, out var rt)
            && t >= rt && t < rt + TimeSpan.FromHours(1)
            && fired.Add(day + "weekly"))
        {
            var btn = new Button { Content = "生成周报", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 6, 0, 0) };
            btn.Click += (_, _) => GenerateWeekly();
            mw.Main.Say("📋 周报时间到！要生成本周周报吗？", btn);
        }
    }

    /// <summary>同一时间只跑一个 AI 请求, 避免连点出一堆重复弹窗</summary>
    private bool TryBegin()
    {
        if (busy)
        {
            Say("还在处理上一个请求，稍等一下～");
            return false;
        }
        busy = true;
        return true;
    }
}
