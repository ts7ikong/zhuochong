using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VPet.Plugin.WorkingPet;

/// <summary>
/// AI / 周报 / 钉钉 相关配置. 含 API Key, 所以单独存在本机 %AppData%\VPet-WorkingPet\ai_config.json,
/// 不放进游戏存档 (存档可能被备份/上传), 也不进 git. 对应旧版 config.json 的 report_config.
/// </summary>
public class AiConfig
{
    [JsonPropertyName("api_key")] public string ApiKey { get; set; } = "";
    /// <summary>豆包(火山引擎 ARK)推理接入点 ID, 形如 ep-xxxxxxxx-xxxxx</summary>
    [JsonPropertyName("endpoint_id")] public string EndpointId { get; set; } = "";
    /// <summary>留空使用默认的 ARK 地址; 填了就用自定义的 OpenAI 兼容接口</summary>
    [JsonPropertyName("api_url")] public string ApiUrl { get; set; } = "";
    /// <summary>周报提醒星期, 0=周一 … 6=周日</summary>
    [JsonPropertyName("remind_day")] public int RemindDay { get; set; } = 4;
    [JsonPropertyName("remind_time")] public string RemindTime { get; set; } = "11:00";
    [JsonPropertyName("dingtalk_path")] public string DingTalkPath { get; set; } = "";
    /// <summary>下班前 10 分钟自动弹出日报确认</summary>
    [JsonPropertyName("daily_confirm")] public bool DailyConfirm { get; set; } = true;
    /// <summary>写进日报/周报提示词里的"我的工作背景", 让 AI 更懂你在做什么</summary>
    [JsonPropertyName("work_background")] public string WorkBackground { get; set; } = "";

    // ── 后台采集 / 数据同步 (本机配置, 不随游戏存档) ──

    /// <summary>数据目录 (工作记录 + 采集数据). 留空 = %AppData%\VPet-WorkingPet\data. 改动后需要重启游戏生效</summary>
    [JsonPropertyName("data_dir")] public string DataDir { get; set; } = "";
    /// <summary>记录前台窗口、统计应用使用时长、定期用 AI 总结"你在做什么"</summary>
    [JsonPropertyName("collect_activity")] public bool CollectActivity { get; set; } = true;
    /// <summary>每隔 15-30 分钟截一次屏, 让视觉模型用一句话描述 (截图会上传到上面配置的接口, 图片本身不保存)</summary>
    [JsonPropertyName("collect_vision")] public bool CollectVision { get; set; } = true;
    /// <summary>通过 Steam 启动时, 把数据同步到 Steam 云 (VPet 自己存档用的同一套云存储)</summary>
    [JsonPropertyName("steam_sync")] public bool SteamSync { get; set; } = true;
    /// <summary>把数据目录当 git 仓库, 定期自动 commit + push (仅限私有仓库)</summary>
    [JsonPropertyName("git_sync")] public bool GitSync { get; set; } = false;
    /// <summary>自动同步的间隔 (小时), Steam 云和 git 共用</summary>
    [JsonPropertyName("git_sync_hours")] public int GitSyncHours { get; set; } = 6;
    /// <summary>已经提示过"开始后台采集"了</summary>
    [JsonPropertyName("collect_notice_shown")] public bool CollectNoticeShown { get; set; } = false;

    [JsonIgnore]
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(EndpointId);

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VPet-WorkingPet", "ai_config.json");

    public static AiConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AiConfig>(File.ReadAllText(FilePath), Options) ?? new AiConfig();
        }
        catch (Exception) { /* 读不出来就当没配置, 用户重新填一次即可 */ }
        return new AiConfig();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Options));
        File.Move(tmp, FilePath, overwrite: true);
    }
}
