using Dvc.Core;

namespace Dvc.Core.Market;

/// <summary>
/// 决策评估时的市场微观结构上下文（由 MarketIngestionService 汇总计算）。
/// 供 AI 输入快照组装与 MockAI 使用。
/// </summary>
public sealed class MarketContext
{
    public required string Symbol { get; init; }

    public DateTime SnapshotUtc { get; init; }

    public decimal LastPrice { get; init; }

    public decimal BestBid { get; init; }

    public decimal BestAsk { get; init; }

    /// <summary>盘口价差（bps）。</summary>
    public decimal SpreadBps { get; init; }

    /// <summary>买盘五档总深度。</summary>
    public decimal BidDepth { get; init; }

    /// <summary>卖盘五档总深度。</summary>
    public decimal AskDepth { get; init; }

    /// <summary>盘口不平衡度 = (BidDepth-AskDepth)/(BidDepth+AskDepth)，+1 强买方压力。</summary>
    public decimal Imbalance { get; init; }

    /// <summary>过去 1 小时波幅（%）：(max-min)/min * 100。</summary>
    public decimal Volatility1HPercent { get; init; }

    /// <summary>15 分钟趋势信号（BULLISH_STRONG / BULLISH / NEUTRAL / BEARISH / BEARISH_STRONG）。</summary>
    public string TrendSignal15M { get; init; } = "NEUTRAL";

    /// <summary>15 分钟涨跌幅（%）。</summary>
    public decimal Change15MPercent { get; init; }

    /// <summary>盘口五档原始数据（AI 快照 Bid_1_To_5 / Ask_1_To_5 直填）。</summary>
    public IReadOnlyList<PriceLevel> BidLevels { get; init; } = [];

    public IReadOnlyList<PriceLevel> AskLevels { get; init; } = [];

    /// <summary>近 5 分钟成交额（USD，换手率代理）。</summary>
    public decimal Turnover5M { get; init; }

    /// <summary>近 5 分钟主动买/卖量（USD）。</summary>
    public decimal BuyFlow5M { get; init; }

    public decimal SellFlow5M { get; init; }
}
