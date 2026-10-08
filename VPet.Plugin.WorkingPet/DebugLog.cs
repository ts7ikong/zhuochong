using System.IO;

namespace VPet.Plugin.WorkingPet;

/// <summary>
/// 排查问题用的简单日志: %AppData%\VPet-WorkingPet\debug.log (超过 200KB 自动清空). 写日志失败一律忽略, 不影响功能.
/// </summary>
internal static class DebugLog
{
    private static readonly object Gate = new();

    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VPet-WorkingPet", "debug.log");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var fi = new FileInfo(FilePath);
                if (fi.Exists && fi.Length > 200_000) fi.Delete();
                File.AppendAllText(FilePath, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
        }
        catch (Exception) { /* 日志不能影响正常功能 */ }
    }
}
