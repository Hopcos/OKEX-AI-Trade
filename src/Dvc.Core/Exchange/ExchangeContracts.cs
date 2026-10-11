using Dvc.Core.Market;
using Dvc.Core.Trading;

namespace Dvc.Core.Exchange;

/// <summary>
/// 行情数据源契约（物理隔离管道之一）。Paper 模式由模拟行情发生器实现，
/// Live 模式由 OKX 公共 WebSocket v5 实现。订阅侧在引擎的 MarketIngestionService。
/// </summary>
public interface IMarketDataFeed : IAsyncDisposable
{
    /// <summary>数据源模式名（Paper / Live）。</summary>
    string Mode { get; }

    IReadOnlyList<string> Symbols { get; }

    /// <summary>逐笔成交（Ticks）。</summary>
    event Action<TradeTick>? OnTrade;

    /// <summary>盘口快照更新（Orderbook）。</summary>
    event Action<OrderBookSnapshot>? OnOrderBook;

    /// <summary>K 线收盘事件（仅收盘推送，供 Swing 低频决策）。</summary>
    event Action<Kline>? OnKlineClosed;

    Task StartAsync(CancellationToken ct);

    Task StopAsync();

    bool TryGetOrderBook(string symbol, out OrderBookSnapshot book);

    decimal GetLastPrice(string symbol);
}

/// <summary>
/// 下单网关契约。只存在现货（Spot）路径 —— 从接口层面物理切断 Margin/Futures。
/// Limit 订单只作普通散户式单步限价操作；Market 订单仅暴露给硬编码风控层的紧急清仓路径
/// （EmergencyMarketAsync），AI 决策层永远禁止触碰。
/// </summary>
public interface IOrderGateway
{
    /// <summary>当前挂单（交易所查询 / 纸面簿查询）。</summary>
    Task<IReadOnlyList<OrderRecord>> GetOpenOrdersAsync(string symbol, CancellationToken ct);

    /// <summary>按客户端订单号查询当前状态（Live: REST order 详情；Paper: 簿内/历史快照）。</summary>
    Task<OrderRecord?> GetOrderAsync(string clientOrderId, CancellationToken ct);

    /// <summary>挂限价单（amountUsd 为 USD 名义金额，网关内部换算 base 数量）。</summary>
    Task<OrderRecord> PlaceLimitAsync(string symbol, OrderSide side, decimal price, decimal amountUsd, string reason, CancellationToken ct);

    /// <summary>按客户端订单号撤单。</summary>
    Task<bool> CancelAsync(string clientOrderId, CancellationToken ct);

    /// <summary>账户余额（可用 USD + 现货持仓 + 折算权益）。</summary>
    Task<AccountBalances> GetBalancesAsync(CancellationToken ct);

    /// <summary>成交推送（Paper 撮合即时推送；Live 由执行管道轮询 REST 详情补齐）。</summary>
    event Action<OrderRecord>? OrderFilled;

    /// <summary>
    /// 仅供硬编码风控层使用的紧急市价路径（熔断清仓 / 认赔止损）。
    /// AI 决策层绝不允许调用。Buy: amountUsd 为名义 USD 金额；Sell: amountUsd 参数承载
    /// 基础币数量（base qty）直接市价卖出；Paper 模式下以最近价即时成交。
    /// </summary>
    Task<OrderRecord> EmergencyMarketAsync(string symbol, OrderSide side, decimal amountUsd, string reason, CancellationToken ct);
}

/// <summary>账户镜像（每日初始本金 / 可用 USD / 已买入代币折算价）。</summary>
public sealed record AccountBalances(
    decimal AvailableUsd,
    IReadOnlyDictionary<string, decimal> Holdings,
    decimal EquityUsd,
    decimal RealizedPnlUsd = 0m)
{
    public static AccountBalances Empty(decimal availableUsd) =>
        new(availableUsd, new Dictionary<string, decimal>(), availableUsd);
}
