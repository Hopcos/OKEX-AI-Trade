using System.Text.Json.Serialization;
using Dvc.Core.Trading;

namespace Dvc.Core;

/// <summary>
/// AI 输入输出 DTO 的 JSON 源生成上下文（AOT：避免反射，发布时自动裁剪 reflect 依赖）。
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = false,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(Dvc.Core.Ai.AiInputSnapshot))]
[JsonSerializable(typeof(Dvc.Core.Ai.AiOutputDecision))]
[JsonSerializable(typeof(Dvc.Core.Ai.AiOrderbook))]
[JsonSerializable(typeof(Dvc.Core.Ai.AiPairSnapshot))]
[JsonSerializable(typeof(Dictionary<string, decimal>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(double))]
[JsonSerializable(typeof(RiskInterceptRecord))]
[JsonSerializable(typeof(OrderRecord))]
[JsonSerializable(typeof(ThoughtRecord))]
[JsonSerializable(typeof(AccountSettlement))]
[JsonSerializable(typeof(TradingConfig))]
[JsonSerializable(typeof(Dvc.Core.Events.UiMarketTick))]
[JsonSerializable(typeof(Dvc.Core.Events.UiAccountTick))]
[JsonSerializable(typeof(Dvc.Core.Events.UiThoughtChunk))]
[JsonSerializable(typeof(Dvc.Core.Events.UiDecision))]
[JsonSerializable(typeof(Dvc.Core.Events.UiConfigTick))]
[JsonSerializable(typeof(Dvc.Core.Events.UiOrderEvent))]
[JsonSerializable(typeof(Dvc.Core.Events.UiFlowLog))]
[JsonSerializable(typeof(Dvc.Core.Events.UiHaltEvent))]
public partial class DvcJsonContext : JsonSerializerContext;
