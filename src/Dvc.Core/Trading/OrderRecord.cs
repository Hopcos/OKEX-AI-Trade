using Dvc.Core;

namespace Dvc.Core.Trading;

/// <summary>交易流水（Trade_Order 表行 + 内存对象）。</summary>
public sealed class OrderRecord
{
    public long Id { get; set; }

    /// <summary>本地客户端订单号（幂等键）。</summary>
    public string ClientOrderId { get; set; } = "";

    public string Symbol { get; set; } = "";

    public OrderSide Side { get; set; }

    /// <summary>挂单价（USD 计价）。</summary>
    public decimal Price { get; set; }

    /// <summary>USD 名义金额。</summary>
    public decimal AmountUsd { get; set; }

    /// <summary>基础币数量（交易所拍卖数量，sz）。</summary>
    public decimal BaseAmount { get; set; }

    public decimal FilledBaseAmount { get; set; }

    public decimal FilledAvgPrice { get; set; }

    public OrderStatus Status { get; set; } = OrderStatus.Queued;

    /// <summary>交易所订单 ID（Live 模式）。</summary>
    public string? ExchangeOrderId { get; set; }

    /// <summary>来源（AI 决策 / 风控应急 / 人工）。</summary>
    public string Reason { get; set; } = "";

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime? UpdatedAtUtc { get; set; }

    public bool IsBuy => Side == OrderSide.Buy;

    /// <summary>本次订单实现盈亏（仅卖出订单成交后有效）。</summary>
    public decimal RealizedPnl { get; set; }
}
