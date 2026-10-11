using Dvc.Core;

namespace Dvc.Core.Trading;

/// <summary>账户资产快照（Asset_Snapshot 表行，每日/每小时/亏损唤醒/启动时记录）。</summary>
public sealed class AccountSettlement
{
    public long Id { get; set; }

    public DateTime SnapshotAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>STARTUP / HOURLY / DAILY / LOSS_WAKEUP / HALT。</summary>
    public string Kind { get; set; } = "";

    /// <summary>每日初始本金（硬编码风控基准）。</summary>
    public decimal InitialCapital { get; set; }

    /// <summary>当前可用 USD。</summary>
    public decimal AvailableUsd { get; set; }

    /// <summary>总权益（USD）。</summary>
    public decimal EquityUsd { get; set; }

    /// <summary>已实现盈亏。</summary>
    public decimal RealizedPnl { get; set; }

    /// <summary>当前最大允许下单额（风控公式计算结果）。</summary>
    public decimal MaxOrderSize { get; set; }

    /// <summary>持仓（base->qty JSON）。</summary>
    public string HoldingsJson { get; set; } = "{}";

    /// <summary>持仓折算价（base->price JSON，用于"已买入代币折算价"）。</summary>
    public string LastPricesJson { get; set; } = "{}";
}
