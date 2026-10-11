using Dvc.Core;
using Dvc.Core.Market;

namespace Dvc.Core.Events;

/// <summary>
/// 引擎事件总线载荷。全系统事件（行情节流快照 / 思考链增量 / 决策 / 订单 / 风控拦截 /
/// 模式切换 / 资产快照 / 熔断）统一进 Channel，由 SignalR HubForwarder 推送到前端，
/// 同时供 Blazor 组件订阅。Payload 统一为 JSON 字符串（AOT 友好，序列化由各生产者
/// 使用源生成器完成）。
/// </summary>
public sealed record EngineEvent(
    EngineEventKind Kind,
    string PayloadJson,
    DateTime TimestampUtc,
    string? SessionId = null);
