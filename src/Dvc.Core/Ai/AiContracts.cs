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

    /// <summary>大脑（LLM）最近一次复盘产出的策略指引（仅 InternDecision 模式且大脑启用时存在）。</summary>
    [JsonPropertyName("Brain_Directive")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BrainDirective? BrainDirective { get; set; }
}

public sealed class AiFinancialMetrics
{
    [JsonPropertyName("Initial_Capital")]
    public decimal InitialCapital { get; set; }

    [JsonPropertyName("Current_Total_Equity")]
    public decimal CurrentTotalEquity { get; set; }

    /// <summary>当前可用 USD 现金（未买入部分）——决定能否再买；耗尽时禁止 buy，否则会被风控以余额不足拦截。</summary>
    [JsonPropertyName("Available_USD")]
    public decimal AvailableUsd { get; set; }

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

    /// <summary>已实现盈亏（USD，净手续费）。</summary>
    [JsonPropertyName("Realized_Pnl")]
    public decimal RealizedPnl { get; set; }

    /// <summary>聚焦标的持仓均价（含买入手续费；空仓为 0）。</summary>
    [JsonPropertyName("Position_Avg_Cost")]
    public decimal PositionAvgCost { get; set; }

    /// <summary>聚焦标的持仓保本卖出价（成本 + 卖出手续费 + 最小净利润；空仓为 0）——卖价低于它就是亏钱。</summary>
    [JsonPropertyName("Position_Min_Profit_Sell_Price")]
    public decimal PositionMinProfitSellPrice { get; set; }

    /// <summary>全账户持仓浮动盈亏（USD）。</summary>
    [JsonPropertyName("Unrealized_Pnl")]
    public decimal UnrealizedPnl { get; set; }

    /// <summary>已占用资金（持仓市值 + 挂买单冻结，USD）。</summary>
    [JsonPropertyName("Committed_Usd")]
    public decimal CommittedUsd { get; set; }

    /// <summary>已占用资金占总权益的百分比。</summary>
    [JsonPropertyName("Exposure_Percent")]
    public decimal ExposurePercent { get; set; }

    /// <summary>总仓位上限（占总权益百分比，Trading:MaxExposureRatio）。</summary>
    [JsonPropertyName("Max_Exposure_Percent")]
    public decimal MaxExposurePercent { get; set; }

    /// <summary>当前还能新买多少（USD）= min(单笔上限, 仓位上限剩余, 可用现金)；&lt; 1 表示不能再买，只能卖出或观望。</summary>
    [JsonPropertyName("Buy_Room_USD")]
    public decimal BuyRoomUsd { get; set; }

    /// <summary>聚焦标的持仓当前能否盈利卖出（买一价 ≥ 保本卖出价）。</summary>
    [JsonPropertyName("Position_Sellable_Now")]
    public bool PositionSellableNow { get; set; }

    /// <summary>聚焦标的持仓浮动盈亏百分比（相对含费均价）。</summary>
    [JsonPropertyName("Position_Unrealized_Percent")]
    public decimal PositionUnrealizedPercent { get; set; }

    /// <summary>距离上一次下单（或程序启动）已空转多少分钟——一直 hold 没有任何订单就没有收益。</summary>
    [JsonPropertyName("Idle_Minutes")]
    public decimal IdleMinutes { get; set; }

    /// <summary>空转达到多少分钟后触发试单兜底（Profit:IdleProbeMinutes，0 = 关闭）。</summary>
    [JsonPropertyName("Idle_Probe_After_Minutes")]
    public decimal IdleProbeAfterMinutes { get; set; }

    /// <summary>空转试单允许的最大价差（bps）。</summary>
    [JsonPropertyName("Idle_Probe_Max_Spread_Bps")]
    public decimal IdleProbeMaxSpreadBps { get; set; }

    /// <summary>当前持有的交易对（市值 ≥ $1，已排除碎仓）。</summary>
    [JsonPropertyName("Held_Symbols")]
    public List<string> HeldSymbols { get; set; } = [];

    /// <summary>同时持仓币种数上限（Profit:MaxOpenPositions），空转试单据此决定要不要再开新仓。</summary>
    [JsonPropertyName("Max_Open_Positions")]
    public int MaxOpenPositions { get; set; }

    /// <summary>聚焦标的已持有多少分钟（止盈目标随之衰减）。</summary>
    [JsonPropertyName("Position_Held_Minutes")]
    public decimal PositionHeldMinutes { get; set; }
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

    /// <summary>买单是否直接挂卖一价（跨价差、基本立即成交）而不是排在买一价等——空转试单用，避免行情上行时永远买不到。</summary>
    [JsonPropertyName("Cross_Spread")]
    public bool CrossSpread { get; set; }

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
