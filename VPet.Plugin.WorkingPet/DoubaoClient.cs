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

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(90) };

    public static Task<string> ChatAsync(AiConfig cfg, IReadOnlyList<(string Role, string Content)> messages, int maxTokens = 1000)
    {
        var payload = JsonSerializer.Serialize(new
        {
            model = cfg.EndpointId.Trim(),
            max_tokens = maxTokens,
            messages = messages.Select(m => new { role = m.Role, content = m.Content }).ToArray(),
        });
        return PostAsync(cfg, payload);
    }

    /// <summary>
    /// 带一张图片的提问 (OpenAI 兼容的 image_url 格式). 需要你的推理接入点背后是支持图片的模型.
    /// </summary>
    public static Task<string> ChatVisionAsync(AiConfig cfg, string prompt, string imageJpegBase64, int maxTokens = 150) =>
        ChatVisionAsync(cfg, prompt, new[] { imageJpegBase64 }, maxTokens);

    /// <summary>带多张图片 (比如多块屏幕各一张), 图片按顺序排在文字前面</summary>
    public static Task<string> ChatVisionAsync(AiConfig cfg, string prompt, IReadOnlyList<string> imagesJpegBase64, int maxTokens = 150)
    {
        var content = new List<object>();
        foreach (var img in imagesJpegBase64)
            content.Add(new { type = "image_url", image_url = new { url = "data:image/jpeg;base64," + img } });
        content.Add(new { type = "text", text = prompt });
        var payload = JsonSerializer.Serialize(new
        {
            model = cfg.EndpointId.Trim(),
            max_tokens = maxTokens,
            messages = new[]
            {
                new
                {
                    role = "user",
                    content,
                },
            },
        });
        return PostAsync(cfg, payload);
    }

    private static async Task<string> PostAsync(AiConfig cfg, string payload)
    {
        var url = string.IsNullOrWhiteSpace(cfg.ApiUrl) ? DefaultUrl : cfg.ApiUrl.Trim();

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
