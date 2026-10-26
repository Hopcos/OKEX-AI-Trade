using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Dvc.Ai.DeepSeek;
using Dvc.Core;
using Dvc.Core.Ai;
using Dvc.Core.Configuration;

namespace Dvc.Ai.Brain;

/// <summary>
/// 大脑 LLM 客户端：OpenAI 兼容 Chat Completions（非流式），一次请求 → 一份 <see cref="BrainDirective"/>。
/// 连接信息取 Brain 配置，留空的项复用 DeepSeek 配置；占位值（空 / "xxx"）视为未配置。
/// </summary>
public sealed class BrainLlmClient
{
    private readonly BrainOptions _brain;
    private readonly DeepSeekOptions _fallback;
    private readonly HttpClient _http;

    public BrainLlmClient(BrainOptions brain, DeepSeekOptions fallback, HttpClient http)
    {
        _brain = brain;
        _fallback = fallback;
        _http = http;
    }

    private string BaseUrl => Pick(_brain.BaseUrl, _fallback.BaseUrl);

    private string ApiKey => Pick(_brain.ApiKey, _fallback.ApiKey);

    private string Model => Pick(_brain.Model, _fallback.Model);

    /// <summary>是否已配置可用的 LLM 连接（BaseUrl 为 http(s)、ApiKey/Model 非空非占位）。</summary>
    public bool IsConfigured =>
        BaseUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase) && Real(ApiKey) && Real(Model);

    public string ModelName => Model;

    /// <summary>发起一次复盘；失败（网络/解析）抛异常，由调用方降级到规则复盘。</summary>
    public async Task<BrainDirective> ReviewAsync(string systemPrompt, string userContent, CancellationToken ct)
    {
        var temp = _brain.Temperature.ToString("0.###", CultureInfo.InvariantCulture);
        var modelJson = JsonSerializer.Serialize(Model, DvcJsonContext.Default.String);
        var systemJson = JsonSerializer.Serialize(systemPrompt, DvcJsonContext.Default.String);
        var userJson = JsonSerializer.Serialize(userContent, DvcJsonContext.Default.String);
        var payload =
            $$"""{"model":{{modelJson}},"messages":[{"role":"system","content":{{systemJson}}},{"role":"user","content":{{userJson}}}],"stream":false,"temperature":{{temp}},"max_tokens":4096}""";

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(10, _brain.TimeoutSeconds)));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

        using var req = new HttpRequestMessage(HttpMethod.Post, BaseUrl.TrimEnd('/') + "/chat/completions")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);

        using var resp = await _http.SendAsync(req, linked.Token);
        var text = await resp.Content.ReadAsStringAsync(linked.Token);
        if (!resp.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"大脑 LLM 请求失败 HTTP {(int)resp.StatusCode}（{(text.Length > 200 ? text[..200] : text)}）");
        }

        using var doc = JsonDocument.Parse(text);
        var choices = doc.RootElement.GetProperty("choices");
        if (choices.GetArrayLength() == 0)
        {
            throw new InvalidOperationException("大脑 LLM 返回空 choices");
        }

        var content = choices[0].GetProperty("message").GetProperty("content").GetString() ?? "";
        var json = DeepSeekStrategyAi.ExtractJsonObject(content)
            ?? throw new InvalidOperationException("大脑 LLM 输出中找不到 JSON 对象");
        return JsonSerializer.Deserialize(json, DvcJsonContext.Default.BrainDirective)
            ?? throw new InvalidOperationException("大脑 LLM 输出 JSON 反序列化为空");
    }

    private static string Pick(string own, string fallback) => Real(own) ? own : fallback;

    private static bool Real(string v) =>
        !string.IsNullOrWhiteSpace(v) && !v.Trim().Equals("xxx", StringComparison.OrdinalIgnoreCase);
}
