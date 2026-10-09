using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;

namespace VPet.Plugin.WorkingPet;

/// <summary>同步结果: Ok 表示成功(包括"没有新内容"), Message 是给人看的说明</summary>
public record SyncResult(bool Ok, string Message);

/// <summary>
/// 把数据目录当成 git 仓库自动同步: add → commit → pull --rebase → push. 用你机器上已有的 git 和凭据,
/// 插件不保存任何账号密码. 因为数据里有你的活动记录, 目标是公开的 GitHub 仓库时会拒绝推送.
/// </summary>
public class GitSync
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<SyncResult> SyncAsync(string dir)
    {
        if (!await gate.WaitAsync(0)) return new SyncResult(false, "上一次同步还没结束");
        try
        {
            return await Task.Run(() => SyncCore(dir));
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<SyncResult> SyncCore(string dir)
    {
        try
        {
            if (!Directory.Exists(Path.Combine(dir, ".git")))
                return new SyncResult(false, "数据目录不是 git 仓库。请先在这个目录里 git init 并设置私有仓库的 origin，或把数据目录改到私有仓库的本地副本");

            var remote = await Git(dir, "remote get-url origin", 15);
            if (remote.Code != 0) return new SyncResult(false, "仓库还没有设置 origin 远程地址");
            var url = remote.Output.Trim();

            if (await IsPublicGitHubRepo(url) == true)
                return new SyncResult(false, "origin 是公开的 GitHub 仓库，数据里有你的活动记录，已拒绝推送。请改用私有仓库");

            var add = await Git(dir, "add -A", 60);
            if (add.Code != 0) return new SyncResult(false, "git add 失败：" + Tail(add.Output));

            var status = await Git(dir, "status --porcelain", 30);
            if (status.Code == 0 && status.Output.Trim().Length > 0)
            {
                var commit = await Git(dir, $"commit -m \"WorkingPet data {DateTime.Now:yyyy-MM-dd HH:mm}\"", 60);
                if (commit.Code != 0) return new SyncResult(false, "git commit 失败（是否设置了 user.name / user.email？）：" + Tail(commit.Output));
            }

            var pull = await Git(dir, "pull --rebase --autostash", 120);
            if (pull.Code != 0)
            {
                await Git(dir, "rebase --abort", 30); // 冲突时放弃这次合并, 不留半成品状态
                // 刚建的空远程没有分支可拉, 属于正常情况, 继续推送
                if (!Regex.IsMatch(pull.Output, "couldn't find remote ref|no tracking information|There is no tracking", RegexOptions.IgnoreCase))
                    return new SyncResult(false, "git pull 失败：" + Tail(pull.Output));
            }

            var push = await Git(dir, "push", 120);
            if (push.Code != 0)
                push = await Git(dir, "push -u origin HEAD", 120); // 第一次推送需要建立上游分支
            if (push.Code != 0) return new SyncResult(false, "git push 失败：" + Tail(push.Output));

            return new SyncResult(true, "已同步");
        }
        catch (Exception e)
        {
            return new SyncResult(false, "同步出错：" + e.Message);
        }
    }

    /// <summary>是公开的 GitHub 仓库返回 true; 私有/不存在返回 false; 不是 GitHub 或查不出来返回 null</summary>
    private static async Task<bool?> IsPublicGitHubRepo(string remoteUrl)
    {
        var m = Regex.Match(remoteUrl, @"github\.com[:/](?<owner>[^/\s]+)/(?<repo>[^/\s]+?)(\.git)?\s*$", RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        try
        {
            // 不带凭据的请求: 公开仓库返回 200, 私有仓库返回 404
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{m.Groups["owner"].Value}/{m.Groups["repo"].Value}");
            req.Headers.UserAgent.ParseAdd("VPet-WorkingPet");
            using var resp = await Http.SendAsync(req);
            if (resp.StatusCode == HttpStatusCode.OK) return true;
            if (resp.StatusCode == HttpStatusCode.NotFound) return false;
        }
        catch (Exception e)
        {
            DebugLog.Write("检查仓库是否公开失败: " + e.Message);
        }
        return null;
    }

    private static async Task<(int Code, string Output)> Git(string dir, string args, int timeoutSeconds)
    {
        var psi = new ProcessStartInfo("git", args)
        {
            WorkingDirectory = dir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0"; // 凭据缺失时直接失败, 不要停在输入提示上卡住
        using var p = new Process { StartInfo = psi };
        try
        {
            p.Start();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return (-1, "找不到 git，请先安装 git 并确保它在 PATH 里");
        }
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeoutSeconds * 1000))
        {
            try { p.Kill(true); } catch (Exception) { }
            return (-1, $"git {args} 超时");
        }
        var text = await stdout + await stderr;
        DebugLog.Write($"git {args} → {p.ExitCode}  {Tail(text)}");
        return (p.ExitCode, text);
    }

    private static string Tail(string s)
    {
        s = s.Trim();
        return s.Length <= 300 ? s : "…" + s[^300..];
    }
}
