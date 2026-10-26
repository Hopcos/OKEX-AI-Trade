using Dvc.Core.Ai;
using Dvc.Core.Configuration;

namespace Dvc.Engine;

/// <summary>
/// 盈利护栏参数解析：配置值为底线，大脑指引只能把要求调得更严（更高保本利润 / 更紧止损），
/// 不能放宽——大脑的作用是让系统更聚焦盈利，而不是更冒险。
/// </summary>
public static class ProfitPolicy
{
    /// <summary>
    /// 有效的最小净利润（bps）：大脑可调高，不可低于配置值；
    /// 并随持仓时间线性下调——持仓越久，止盈目标越贴近"刚好覆盖手续费"（Profit:MinNetProfitFloorBps），
    /// 在 Profit:TargetDecayMinutes 分钟后降到底。盈利只在卖出时兑现，价格没按预期走时，宁可薄利尽快回笼现金，
    /// 也不能让一笔仓位占着全部额度干等一个够不到的目标价。heldMinutes ≤ 0 表示不衰减（刚买入/未知）。
    /// </summary>
    public static decimal MinNetProfitBps(TradingOptions o, BrainDirective? d, decimal heldMinutes = 0m)
    {
        var floor = Math.Max(0m, o.Profit.MinNetProfitBps);
        var start = d?.MinNetProfitBps is { } v ? Math.Clamp(v, floor, 300m) : floor;
        var decayMinutes = o.Profit.TargetDecayMinutes;
        if (decayMinutes <= 0 || heldMinutes <= 0m)
        {
            return start;
        }

        var end = Math.Clamp(o.Profit.MinNetProfitFloorBps, 0m, start);
        var progress = Math.Min(1m, heldMinutes / decayMinutes);
        return start - (start - end) * progress;
    }

    /// <summary>有效止损线（%）：大脑可收紧，不可超过配置值。</summary>
    public static decimal StopLossPercent(TradingOptions o, BrainDirective? d)
    {
        var cap = Math.Max(0.1m, o.Profit.StopLossPercent);
        return d?.StopLossPercent is { } v ? Math.Clamp(v, 0.5m, cap) : cap;
    }

    /// <summary>
    /// 保本卖出价：卖出净得（成交额 × (1 − 费率)）≥ 持仓成本（已含买入手续费）× (1 + 最小净利润)。
    /// </summary>
    public static decimal MinProfitSellPrice(decimal avgCost, decimal feeBps, decimal minNetProfitBps)
    {
        if (avgCost <= 0m)
        {
            return 0m;
        }

        var fee = Math.Clamp(feeBps, 0m, 500m) / 10000m;
        return avgCost * (1m + minNetProfitBps / 10000m) / (1m - fee);
    }
}
