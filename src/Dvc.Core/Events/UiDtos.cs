using System.Text.Json.Serialization;
using Dvc.Core;

namespace Dvc.Core.Events;

/// <summary>看板市场行情节流推送 DTO（1 秒级）。</summary>
public sealed class UiMarketTick
{
    [JsonPropertyName("symbol")]
    public string Symbol { get; set; } = "";

    [JsonPropertyName("ts")]
    public DateTime Ts { get; set; }

    [JsonPropertyName("lastPrice")]
    public decimal LastPrice { get; set; }

    [JsonPropertyName("bestBid")]
    public decimal BestBid { get; set; }

    [JsonPropertyName("bestAsk")]
    public decimal BestAsk { get; set; }

    [JsonPropertyName("spreadBps")]
    public decimal SpreadBps { get; set; }

    [JsonPropertyName("imbalance")]
    public decimal Imbalance { get; set; }

    [JsonPropertyName("vol1H")]
    public decimal Vol1H { get; set; }

    [JsonPropertyName("trend15M")]
    public string Trend15M { get; set; } = "NEUTRAL";

    [JsonPropertyName("change15M")]
    public decimal Change15M { get; set; }

    [JsonPropertyName("turnover5M")]
    public decimal Turnover5M { get; set; }
}

/// <summary>看板账户资产推送 DTO。</summary>
public sealed class UiAccountTick
{
    [JsonPropertyName("initialCapital")]
    public decimal InitialCapital { get; set; }

    [JsonPropertyName("availableUsd")]
    public decimal AvailableUsd { get; set; }

    [JsonPropertyName("equityUsd")]
    public decimal EquityUsd { get; set; }

    [JsonPropertyName("accumulatedProfit")]
    public decimal AccumulatedProfit { get; set; }

    [JsonPropertyName("realizedPnl")]
    public decimal RealizedPnl { get; set; }

    [JsonPropertyName("maxOrderSize")]
    public decimal MaxOrderSize { get; set; }

    [JsonPropertyName("maxOrderSizeRatio")]
    public decimal MaxOrderSizeRatio { get; set; } = 0.20m;

    [JsonPropertyName("shieldCount")]
    public long ShieldCount { get; set; }

    [JsonPropertyName("holdings")]
    public Dictionary<string, decimal> Holdings { get; set; } = [];

    [JsonPropertyName("runState")]
    public string RunState { get; set; } = "Running";
}

/// <summary>AI 思考链分片推送 DTO（打字机）。</summary>
public sealed class UiThoughtChunk
{
    [JsonPropertyName("sessionId")]
    public string SessionId { get; set; } = "";

    [JsonPropertyName("text")]
    public string Text { get; set; } = "";

    [JsonPropertyName("trigger")]
    public string Trigger { get; set; } = "";
}

/// <summary>AI 决策完成推送 DTO。</summary>
public sealed class UiDecision
{
    [JsonPropertyName("sessionId")]
    public string SessionId { get; set; } = "";

    [JsonPropertyName("diagnosis")]
    public string Diagnosis { get; set; } = "";

    [JsonPropertyName("actionType")]
    public string ActionType { get; set; } = "";

    [JsonPropertyName("vetoed")]
    public bool Vetoed { get; set; }

    [JsonPropertyName("vetoReason")]
    public string VetoReason { get; set; } = "";

    [JsonPropertyName("targetSymbol")]
    public string TargetSymbol { get; set; } = "";

    [JsonPropertyName("equityAfter")]
    public decimal EquityAfter { get; set; }

    [JsonPropertyName("thinkingSummary")]
    public string ThinkingSummary { get; set; } = "";

    [JsonPropertyName("actionProbabilities")]
    public string? ActionProbabilities { get; set; }

    [JsonPropertyName("stopWaitProbabilities")]
    public string? StopWaitProbabilities { get; set; }
}

/// <summary>订单事件推送 DTO。</summary>
public sealed class UiOrderEvent
{
    [JsonPropertyName("clientOrderId")]
    public string ClientOrderId { get; set; } = "";

    [JsonPropertyName("symbol")]
    public string Symbol { get; set; } = "";

    [JsonPropertyName("side")]
    public string Side { get; set; } = "";

    [JsonPropertyName("price")]
    public decimal Price { get; set; }

    [JsonPropertyName("amountUsd")]
    public decimal AmountUsd { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = "";

    [JsonPropertyName("realizedPnl")]
    public decimal RealizedPnl { get; set; }

    [JsonPropertyName("tag")]
    public string Tag { get; set; } = "";

    [JsonPropertyName("ts")]
    public DateTime Ts { get; set; }
}

/// <summary>结构化流水日志推送 DTO。</summary>
public sealed class UiFlowLog
{
    [JsonPropertyName("message")]
    public string Message { get; set; } = "";
}

/// <summary>系统熔断推送 DTO。</summary>
public sealed class UiHaltEvent
{
    [JsonPropertyName("reason")]
    public string Reason { get; set; } = "";

    [JsonPropertyName("equityUsd")]
    public decimal EquityUsd { get; set; }

    [JsonPropertyName("initialCapital")]
    public decimal InitialCapital { get; set; }
}

/// <summary>模式/配置切换推送 DTO。</summary>
public sealed class UiConfigTick
{
    [JsonPropertyName("tradingMode")]
    public string TradingMode { get; set; } = "Scalping";

    [JsonPropertyName("assetScope")]
    public string AssetScope { get; set; } = "Single-Pair";

    [JsonPropertyName("activeSymbol")]
    public string ActiveSymbol { get; set; } = "";

    [JsonPropertyName("focusLocked")]
    public bool FocusLocked { get; set; }

    [JsonPropertyName("dataSourceMode")]
    public string DataSourceMode { get; set; } = "Paper";

    [JsonPropertyName("aiProvider")]
    public string AiProvider { get; set; } = "Mock";

    [JsonPropertyName("runState")]
    public string RunState { get; set; } = "Running";

    [JsonPropertyName("theme")]
    public string Theme { get; set; } = "cyberpunk";
}
