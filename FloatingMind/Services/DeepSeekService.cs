using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using FloatingMind.Models.Config;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FloatingMind.Services;

/// <summary>
/// DeepSeek API 客户端 —— 对话补全(chat completions)
/// 直接读取 AppConfig 引用,设置页保存后即时生效,无需重启
/// </summary>
public class DeepSeekService
{
    private readonly AppConfig _config;

    // 静态共享 HttpClient,避免 socket 耗尽; 320s 总超时兜底
    // (reasoning模型处理大文件修复可达3-5分钟, 具体超时由 ChatAsync 的 timeoutSeconds 控制)
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(320) };

    public DeepSeekService(AppConfig config)
    {
        _config = config;
    }

    /// <summary>是否已配置API Key</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(_config.DeepSeekApiKey);

    /// <summary>
    /// 调用对话补全。model 为空时使用低成本模型(deepseek-chat)
    /// 显式超时(timeoutSeconds, 默认170秒)与HttpClient超时独立, 保证推理模型不会无限挂起
    /// </summary>
    public async Task<string> ChatAsync(string userPrompt, string? systemPrompt = null,
        string? model = null, double temperature = 0.7, CancellationToken ct = default,
        int timeoutSeconds = 170)
    {
        if (!IsConfigured)
            throw new InvalidOperationException("未配置 DeepSeek API Key (设置页填写)");

        var url = _config.DeepSeekApiUrl.TrimEnd('/') + "/chat/completions";

        var messages = new List<object>();
        if (!string.IsNullOrWhiteSpace(systemPrompt))
            messages.Add(new { role = "system", content = systemPrompt });
        messages.Add(new { role = "user", content = userPrompt });

        var payload = new
        {
            model = model ?? _config.LowCostModel,
            messages,
            temperature,
            stream = false
        };

        // 强制超时: 调用方未指定ct时默认timeoutSeconds秒, 防止reasoning模型长时间无响应
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.DeepSeekApiKey);
        req.Content = new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");

        using var resp = await _http.SendAsync(req, timeoutCts.Token);
        var body = await resp.Content.ReadAsStringAsync(timeoutCts.Token);

        if (!resp.IsSuccessStatusCode)
        {
            var snippet = body.Length > 300 ? body[..300] + "..." : body;
            throw new InvalidOperationException($"DeepSeek API 错误 {(int)resp.StatusCode}: {snippet}");
        }

        var obj = JObject.Parse(body);
        var content = obj["choices"]?[0]?["message"]?["content"]?.ToString();
        if (string.IsNullOrEmpty(content))
            throw new InvalidOperationException("DeepSeek API 返回空内容");
        return content;
    }
}
