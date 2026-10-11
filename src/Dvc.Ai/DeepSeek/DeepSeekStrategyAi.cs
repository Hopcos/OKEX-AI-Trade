using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Dvc.Core;
using Dvc.Core.Ai;
using Dvc.Core.Configuration;

namespace Dvc.Ai.DeepSeek;

/// <summary>
/// DeepSeek 推理客户端（OpenAI 兼容 Chat Completions，流式 SSE）。
/// - 思考链：reasoning_content（推理模式）或 content 中 的 thinking 标签，分片输出。
/// - 最终 JSON：content 闭合后提取首个 JSON 对象（容忍 markdown 代码围栏）。
/// </summary>
public sealed class DeepSeekStrategyAi : IStrategyAi
{
    private readonly DeepSeekOptions _opts;
    private readonly HttpClient _http;

    public DeepSeekStrategyAi(DeepSeekOptions opts, HttpClient http)
    {
        _opts = opts;
        _http = http;
    }

    public string ProviderName => $"DeepSeek({_opts.Model})";

    public async IAsyncEnumerable<AiStreamPart> DecideAsync(AiRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var userContent = "以下是当前盘面与账户快照（JSON 文本，仅作参考，不要照抄为输出）：\n"
            + JsonSerializer.Serialize(request.Input, DvcJsonContext.Default.AiInputSnapshot);
        var userJson = JsonSerializer.Serialize(userContent, DvcJsonContext.Default.String);
        var systemJson = JsonSerializer.Serialize(request.SystemPrompt, DvcJsonContext.Default.String);
        var temp = _opts.Temperature.ToString("0.###", CultureInfo.InvariantCulture);
        var payloadJson =
            $$"""{"model":{{JsonSerializer.Serialize(_opts.Model, DvcJsonContext.Default.String)}},"messages":[{"role":"system","content":{{systemJson}}},{"role":"user","content":{{userJson}}}],"stream":true,"temperature":{{temp}},"max_tokens":8192}""";

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(_opts.TimeoutSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

        using var resp = await SendWithRetryAsync(payloadJson, linked.Token);

        var contentBuffer = new StringBuilder();
        await using var stream = await resp.Content.ReadAsStreamAsync(linked.Token);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        string? line;
        while ((line = await reader.ReadLineAsync(linked.Token)) is not null)
        {
            if (line.StartsWith("data: ", StringComparison.Ordinal))
            {
                var data = line[6..].Trim();
                if (data == "[DONE]")
                {
                    break;
                }

                using var doc = JsonDocument.Parse(data);
                var choices = doc.RootElement.GetProperty("choices");
                if (choices.GetArrayLength() == 0)
                {
                    continue;
                }

                var delta = choices[0].GetProperty("delta");

                // 1) 思考链分片（DeepSeek: reasoning_content；vLLM 网关: reasoning）
                JsonElement thinkEl = default;
                var hasThinking = delta.TryGetProperty("reasoning_content", out thinkEl);
                if (!hasThinking)
                {
                    hasThinking = delta.TryGetProperty("reasoning", out thinkEl);
                }

                if (hasThinking && thinkEl.ValueKind == JsonValueKind.String)
                {
                    var text = thinkEl.GetString();
                    if (!string.IsNullOrEmpty(text))
                    {
                        yield return new AiStreamPart(true, text);
                    }
                }

                // 2) 内容分片（最终 JSON / 或非推理模型的 thinking 标签）
                if (delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                {
                    var text = content.GetString();
                    if (string.IsNullOrEmpty(text))
                    {
                        continue;
                    }

                    // 兼容非推理模型的 思考标签 → 思考链（未闭合标签留待收尾容错）
                    var stripped = StripThinkingLive(text, out var thinking, out var remaining);
                    if (stripped && thinking.Length > 0)
                    {
                        yield return new AiStreamPart(true, thinking);
                    }

                    if (remaining.Length > 0)
                    {
                        contentBuffer.Append(remaining);
                    }
                }
            }
        }

        // 3) 收尾：从缓冲提取首个完整 JSON 对象（容忍 markdown 围栏与前缀噪音）；
        //    仅在最终一次性输出，避免与流式分片重复。
        if (contentBuffer.Length > 0)
        {
            var finalText = contentBuffer.ToString();
            var json = ExtractJsonObject(finalText);
            yield return new AiStreamPart(false, json ?? finalText);
        }
    }

    /// <summary>带退避重试的请求发送：429 / 5xx / 网络异常 最多重试 2 次（2s、4s 退避），成功后返回响应体。</summary>
    private async Task<HttpResponseMessage> SendWithRetryAsync(string payloadJson, CancellationToken ct)
    {
        const int maxAttempts = 3;
        var lastStatus = 0;
        string lastBody = "";

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                using var httpReq = new HttpRequestMessage(HttpMethod.Post, _opts.BaseUrl.TrimEnd('/') + "/chat/completions")
                {
                    Content = new StringContent(payloadJson, Encoding.UTF8, "application/json"),
                };
                httpReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _opts.ApiKey);

                var resp = await _http.SendAsync(httpReq, HttpCompletionOption.ResponseHeadersRead, ct);
                var status = (int)resp.StatusCode;
                if (status >= 200 && status < 300)
                {
                    return resp;
                }

                lastStatus = status;
                lastBody = await resp.Content.ReadAsStringAsync(ct);
                resp.Dispose();

                if (status != 429 && status < 500)
                {
                    break; // 4xx（非限流）不重试，直接抛错
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (HttpRequestException ex)
            {
                // 网络层异常：可重试；最后一轮直接把异常抛出
                if (attempt >= maxAttempts)
                {
                    throw;
                }

                _ = ex;
                await Task.Delay(TimeSpan.FromSeconds(2) * attempt, ct);
                continue;
            }

            if (attempt >= maxAttempts)
            {
                break;
            }

            // 429/5xx：指数退避后重试
            await Task.Delay(TimeSpan.FromSeconds(2) * attempt, ct);
        }

        var snippet = lastBody.Length > 300 ? lastBody[..300] : lastBody;
        throw new HttpRequestException($"AI 网关请求失败 HTTP {lastStatus}（{snippet}）");
    }

    /// <summary>流式 thinking 标签剥离（非推理模型）；返回是否剥离成功并给出思考/剩余部分。</summary>
    private static bool StripThinkingLive(string text, out string thinking, out string remaining)
    {
        if (!text.Contains("\\u003cthinking\\u003e") && !text.Contains("<thinking>"))
        {
            thinking = "";
            remaining = text;
            return false;
        }

        const string openTag = "<thinking>";
        const string closeTag = "</thinking>";
        thinking = "";
        remaining = text;
        var open = text.IndexOf(openTag, StringComparison.Ordinal);
        var close = text.IndexOf(closeTag, StringComparison.Ordinal);
        if (open >= 0 && close > open)
        {
            thinking = text[(open + openTag.Length)..close];
            remaining = text[..open] + text[(close + closeTag.Length)..];
            return true;
        }

        return false;
    }

    /// <summary>从文本中提取首个完整 JSON 对象（容忍 markdown ``` 围栏与前缀噪音）。</summary>
    internal static string? ExtractJsonObject(string text)
    {
        var start = text.IndexOf('{');
        if (start < 0)
        {
            return null;
        }

        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var i = start; i < text.Length; i++)
        {
            var ch = text[i];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (ch == '\\')
                {
                    escaped = true;
                }
                else if (ch == '"')
                {
                    inString = false;
                }

                continue;
            }

            switch (ch)
            {
                case '"':
                    inString = true;
                    break;
                case '{':
                    depth++;
                    break;
                case '}':
                    depth--;
                    if (depth == 0)
                    {
                        return text[start..(i + 1)];
                    }

                    break;
            }
        }

        return null;
    }
}
