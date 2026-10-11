using Dvc.Core;

namespace Dvc.Core.Market;

/// <summary>盘口一档价格/数量。</summary>
public sealed record PriceLevel(decimal Price, decimal Size);

/// <summary>订单簿快照（买一卖一至买五卖五）。</summary>
public sealed record OrderBookSnapshot(
    string Symbol,
    DateTime TimestampUtc,
    IReadOnlyList<PriceLevel> Bids,
    IReadOnlyList<PriceLevel> Asks)
{
    public decimal BestBid => Bids.Count > 0 ? Bids[0].Price : 0m;
    public decimal BestAsk => Asks.Count > 0 ? Asks[0].Price : 0m;
    public decimal Mid => (BestBid + BestAsk) / 2m;
    public bool IsValid => BestBid > 0m && BestAsk > 0m && BestAsk >= BestBid;
}

/// <summary>逐笔成交（Ticks）。</summary>
public sealed record TradeTick(
    string Symbol,
    DateTime TimestampUtc,
    decimal Price,
    decimal Size,
    string Side /* buy | sell */);

/// <summary>K 线（收盘事件推送）。</summary>
public sealed record Kline(
    string Symbol,
    string Granularity, // 1m | 15m | 1H
    DateTime OpenTimeUtc,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    decimal Volume);
