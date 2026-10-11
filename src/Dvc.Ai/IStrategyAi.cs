using Dvc.Core.Ai;

namespace Dvc.Ai;

/// <summary>
/// AI 决策引擎契约。串行化推理：一次调用 = 一条思考链流（IsThinking 分片）
/// + 最终 JSON 决策（闭合 response 后输出）。Mock 与 DeepSeek 两种实现可热切换。
/// </summary>
public interface IStrategyAi
{
    string ProviderName { get; }

    /// <summary>
    /// 执行一次决策推理。实现方保证：先产出 thinking 分片，最后产出最终 JSON。
    /// 取消令牌触发时实现方应立即停止输出。
    /// </summary>
    IAsyncEnumerable<AiStreamPart> DecideAsync(AiRequest request, CancellationToken ct);
}
