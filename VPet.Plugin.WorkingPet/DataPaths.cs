using System.IO;

namespace VPet.Plugin.WorkingPet;

/// <summary>
/// 数据目录: 工作记录和所有采集数据都放这里, 可以改成任意文件夹 (比如私有 git 仓库的本地副本、网盘同步目录),
/// 这样数据就能跟着同步. API Key 等配置不在这里, 留在 %AppData%\VPet-WorkingPet\ 根目录, 不会被同步出去.
/// </summary>
internal static class DataPaths
{
    public static string AppDataRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VPet-WorkingPet");

    public static string DefaultRoot => Path.Combine(AppDataRoot, "data");

    public static string Root(AiConfig cfg) =>
        string.IsNullOrWhiteSpace(cfg.DataDir) ? DefaultRoot : cfg.DataDir.Trim();

    public static string WorkLog(AiConfig cfg) => Path.Combine(Root(cfg), "work_log.json");

    /// <summary>第 2 步的记录文件原先放在 %AppData%\VPet-WorkingPet\work_log.json, 首次启用数据目录时复制过去 (不删原文件)</summary>
    public static void MigrateLegacyWorkLog(AiConfig cfg)
    {
        try
        {
            var old = Path.Combine(AppDataRoot, "work_log.json");
            var target = WorkLog(cfg);
            if (!File.Exists(old) || File.Exists(target)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(old, target);
        }
        catch (Exception e)
        {
            DebugLog.Write("迁移 work_log.json 失败: " + e.Message);
        }
    }
}
