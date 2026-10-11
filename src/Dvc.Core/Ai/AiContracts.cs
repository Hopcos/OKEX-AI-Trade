using System.Text.Json.Serialization;
using Dvc.Core;

namespace Dvc.Core.Ai;

// ============================================================================
// 与《最终版技术方案》第四节完全对齐的 AI 输入/输出 JSON 结构。
// 使用 System.Text.Json 源生成器（AOT 友好，无反射）。
// ============================================================================

/// <summary>动态输入结构（User Message 传入的 JSON 快照）。</summary>
public sealed class AiInputSnapshot
{
    /// <summary>MACRO_HOURLY / LOSS_EVENT_WAKEUP / STRATEGY_TICK / STARTUP。</summary>
    [JsonPropertyName("Trigger_Reason")]
    public string TriggerReason { get; set; } = "";

    [JsonPropertyName("System_Time")]
    public string SystemTime { get; set; } = "";

    [JsonPropertyName("Financial_Metrics")]
    public AiFinancialMetrics FinancialMetrics { get; set; } = new();

    [JsonPropertyName("Current_Config")]
    public AiCurrentConfig CurrentConfig { get; set; } = new();

    [JsonPropertyName("Market_Snapshot")]
    public AiMarketSnapshot MarketSnapshot { get; set; } = new();

    /// <summary>Multi-Pair 模式扩展：候选池各币种微观结构（Single-Pair 时为 null）。</summary>
    [JsonPropertyName("Pair_Snapshots")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<AiPairSnapshot>? PairSnapshots { get; set; }
}

public sealed class AiFinancialMetrics
{
    [JsonPropertyName("Initial_Capital")]
    public decimal InitialCapital { get; set; }

    [JsonPropertyName("Current_Total_Equity")]
    public decimal CurrentTotalEquity { get; set; }

    [JsonPropertyName("Accumulated_Profit")]
    public decimal AccumulatedProfit { get; set; }

    /// <summary>风控硬编码计算出的本次最大下单上限。</summary>
    [JsonPropertyName("Max_Allowed_Order_Size")]
    public decimal MaxAllowedOrderSize { get; set; }

    [JsonPropertyName("Last_Trade_Loss_Amount")]
    public decimal LastTradeLossAmount { get; set; }

    /// <summary>当前现货持仓（base 数量，0 表示空仓）——方案快照的扩展字段，供卖出/轮动决策。</summary>
    [JsonPropertyName("Position_Base_Qty")]
    public decimal PositionBaseQty { get; set; }

    /// <summary>已实现盈亏（USD）。</summary>
    [JsonPropertyName("Realized_Pnl")]
    public decimal RealizedPnl { get; set; }
}

public sealed class AiCurrentConfig
{
    [JsonPropertyName("Trading_Mode")]
    public string TradingMode { get; set; } = "Scalping";

    [JsonPropertyName("Asset_Scope")]
    public string AssetScope { get; set; } = "Single-Pair";

    [JsonPropertyName("Active_Symbol")]
    public string ActiveSymbol { get; set; } = "";

    [JsonPropertyName("Blacklist")]
    public List<string> Blacklist { get; set; } = [];
}

public sealed class AiMarketSnapshot
{
    [JsonPropertyName("Active_Symbol_Orderbook")]
    public AiOrderbook? ActiveSymbolOrderbook { get; set; }

    [JsonPropertyName("Last_Price")]
    public decimal LastPrice { get; set; }

    [JsonPropertyName("Volatility_1H_Percent")]
    public decimal Volatility1HPercent { get; set; }

    [JsonPropertyName("Trend_Signal_15M")]
    public string TrendSignal15M { get; set; } = "NEUTRAL";

    [JsonPropertyName("Change_15M_Percent")]
    public decimal Change15MPercent { get; set; }

    [JsonPropertyName("Turnover_5M_USD")]
    public decimal Turnover5MUsd { get; set; }
}

/// <summary>买一卖一至买五卖五：[[price, size], ...]。</summary>
public sealed class AiOrderbook
{
    [JsonPropertyName("Bid_1_To_5")]
    public List<List<decimal>> Bid1To5 { get; set; } = [];

    [JsonPropertyName("Ask_1_To_5")]
    public List<List<decimal>> Ask1To5 { get; set; } = [];
}

/// <summary>Multi-Pair 候选币种快照（资金调度算法输入）。</summary>
public sealed class AiPairSnapshot
{
    [JsonPropertyName("Symbol")]
    public string Symbol { get; set; } = "";

    [JsonPropertyName("Last_Price")]
    public decimal LastPrice { get; set; }

    [JsonPropertyName("Spread_Bps")]
    public decimal SpreadBps { get; set; }

    [JsonPropertyName("Turnover_5M_USD")]
    public decimal Turnover5MUsd { get; set; }

    [JsonPropertyName("Volatility_1H_Percent")]
    public decimal Volatility1HPercent { get; set; }

    [JsonPropertyName("Change_15M_Percent")]
    public decimal Change15MPercent { get; set; }

    [JsonPropertyName("Imbalance")]
    public decimal Imbalance { get; set; }

    [JsonPropertyName("Trend_Signal_15M")]
    public string TrendSignal15M { get; set; } = "NEUTRAL";
}

/// <summary>最终 JSON 决策输出结构（模型闭合 response 后输出）。</summary>
public sealed class AiOutputDecision
{
    [JsonPropertyName("Decision_Logic")]
    public AiDecisionLogic DecisionLogic { get; set; } = new();

    [JsonPropertyName("Target_Config_Status")]
    public AiTargetConfig TargetConfigStatus { get; set; } = new();

    [JsonPropertyName("Immediate_Execution_Order")]
    public AiExecutionOrder ImmediateExecutionOrder { get; set; } = new();

    /// <summary>action 问题答案的概率分布（观察用，不参与执行，便于评估 intern-decision 是否需优化）。</summary>
    [JsonPropertyName("Action_Probabilities")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ActionProbabilities { get; set; }

    /// <summary>stop_and_wait 问题答案的概率分布（观察用）。</summary>
    [JsonPropertyName("Stop_Wait_Probabilities")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StopWaitProbabilities { get; set; }
}

public sealed class AiDecisionLogic
{
    [JsonPropertyName("Diagnosis")]
    public string Diagnosis { get; set; } = "";

    [JsonPropertyName("Action_Type")]
    public string ActionType { get; set; } = "HOLD";
}

public sealed class AiTargetConfig
{
    [JsonPropertyName("Trading_Mode")]
    public string TradingMode { get; set; } = "";

    [JsonPropertyName("Asset_Scope")]
    public string AssetScope { get; set; } = "";

    [JsonPropertyName("Active_Symbol")]
    public string ActiveSymbol { get; set; } = "";
}

public sealed class AiExecutionOrder
{
    /// <summary>是否立即执行（false 时仅记录观察）。</summary>
    [JsonPropertyName("Execute_Now")]
    public bool ExecuteNow { get; set; }

    /// <summary>HOLD / PLACE_LIMIT_BUY / PLACE_LIMIT_SELL / CANCEL_ORDER / CANCEL_ALL_AND_WAIT / MODE_SWITCH。</summary>
    [JsonPropertyName("Action")]
    public string Action { get; set; } = "HOLD";

    [JsonPropertyName("Target_Symbol")]
    public string TargetSymbol { get; set; } = "";

    [JsonPropertyName("Proposed_Order_Size_USD")]
    public decimal ProposedOrderSizeUsd { get; set; }

    // ------- 扩展字段（方案约束内补充，供限价单执行参数化） -------

    /// <summary>挂单方向（BUY / SELL）。</summary>
    [JsonPropertyName("Side")]
    public string? Side { get; set; }

    /// <summary>挂单价；为空时由引擎按盘口（买一/卖一内沿）自动定价。</summary>
    [JsonPropertyName("Price")]
    public decimal? Price { get; set; }

    /// <summary>挂单生命周期秒数，超时由 AI 决策继续等待或 CANCEL。</summary>
    [JsonPropertyName("Ttl_Seconds")]
    public int TtlSeconds { get; set; } = 120;
}

/// <summary>思考链流式分片（打字机效果数据源）。</summary>
public sealed record AiStreamPart(bool IsThinking, string Text);

/// <summary>一次推理请求（System Prompt + 动态输入）。</summary>
public sealed record AiRequest(string SystemPrompt, AiInputSnapshot Input);
