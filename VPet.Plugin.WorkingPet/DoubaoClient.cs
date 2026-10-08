using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace VPet.Plugin.WorkingPet;

/// <summary>
/// 豆包(火山引擎 ARK)对话接口, OpenAI 兼容格式. 对应 work_log._doubao_request:
/// model 字段填推理接入点 ID, 429 限流时退避重试 (8s → 16s).
/// 注意: 会把提示词(含你的工作记录)发给你配置的接口, 只会发给你自己填写的地址.
/// </summary>
public static class DoubaoClient
{
    public const string DefaultUrl = "https://ark.cn-beijing.volces.com/api/v3/chat/completions";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    public static async Task<string> ChatAsync(AiConfig cfg, IReadOnlyList<(string Role, string Content)> messages, int maxTokens = 1000)
    {
        var url = string.IsNullOrWhiteSpace(cfg.ApiUrl) ? DefaultUrl : cfg.ApiUrl.Trim();
        var payload = JsonSerializer.Serialize(new
        {
            model = cfg.EndpointId.Trim(),
            max_tokens = maxTokens,
            messages = messages.Select(m => new { role = m.Role, content = m.Content }).ToArray(),
        });

        for (int attempt = 0; ; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cfg.ApiKey.Trim());

            using var resp = await Http.SendAsync(req);
            var body = await resp.Content.ReadAsStringAsync();

            if (resp.StatusCode == (HttpStatusCode)429 && attempt < 2)
            {
                await Task.Delay(TimeSpan.FromSeconds(8 * (attempt + 1)));
                continue;
            }
            if (!resp.IsSuccessStatusCode)
                throw new HttpRequestException($"HTTP {(int)resp.StatusCode}: {Shorten(body)}");

            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
        }
    }

    private static string Shorten(string s) => s.Length <= 200 ? s : s[..200] + "…";
}
