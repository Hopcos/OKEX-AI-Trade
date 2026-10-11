using Dvc.Core;

namespace Dvc.Core.Trading;

/// <summary>风控拦截记录（盾牌计数器明细）。</summary>
public sealed class RiskInterceptRecord
{
    public long Id { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>BLACKLIST / OVER_LIMIT / ILLEGAL_ACTION / CIRCUIT_TRIPPED。</summary>
    public string Kind { get; set; } = "";

    public string Symbol { get; set; } = "";

    public decimal RequestedAmount { get; set; }

    public decimal AllowedAmount { get; set; }

    public string Detail { get; set; } = "";
}
