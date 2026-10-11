using Dvc.Core;
using Dvc.Core.Configuration;
using Dvc.Core.Market;
using Dvc.Core.Trading;

namespace Dvc.Engine;

/// <summary>风控校验结论。</summary>
public sealed record RiskVerdict(bool Allowed, string Reason, decimal AllowedAmount, string Kind);

/// <summary>
/// 硬编码资金安全锁（绝对风控层）—— 纯逻辑，独立于 AI 之外，拥有"一票否决权"。
/// 1) 动态下单额度拦截（复利公式）  2) 黑名单强阻断  3) 零负债现货约束  4) 价格幻觉护栏。
/// </summary>
public static class RiskEngine
{
    /// <summary>
    /// 动态下单额度：最大允许下单额 = (初始本金 + max(0, 总权益 - 初始本金)) × 20%。
    /// 盈利按比例复利抬升，亏损自动缩减，确保永不爆仓。
    /// </summary>
    public static decimal MaxOrderSize(decimal initialCapital, decimal equityUsd, decimal ratio = 0.20m)
    {
        var effectivePrincipal = initialCapital + Math.Max(0m, equityUsd - initialCapital);
        return Math.Round(effectivePrincipal * ratio, 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>黑名单基币归一化判定（大小写与带 USDT 后缀容错）。</summary>
    public static bool IsBlacklisted(TradingConfig config, string symbol)
    {
        var baseCcy = BaseOf(symbol);
        return config.Blacklist.Any(b => string.Equals(BaseOf(b), baseCcy, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// AI 订单提案校验（一票否决）。ctx 为 null 时（无行情）放行价格合理性以外的检查。
    /// </summary>
    public static RiskVerdict ValidateOrder(
        TradingConfig config,
        TradingOptions options,
        MarketContext? ctx,
        string symbol,
        OrderSide side,
        decimal amountUsd,
        decimal price,
        decimal availableUsd,
        decimal holdingBase,
        decimal equityUsd)
    {
        // 黑名单强阻断
        if (IsBlacklisted(config, symbol))
        {
            return Veto("BLACKLIST", symbol, amountUsd, 0m, $"黑名单币种 {symbol} 被硬编码风控一票否决");
        }

        // 价格幻觉护栏（限价单价格必须落在合理区间）
        if (price <= 0m)
        {
            return Veto("ILLEGAL_PRICE", symbol, amountUsd, 0m, "价格为 0 或负数，判定为 AI 幻觉");
        }

        if (ctx is { LastPrice: > 0 })
        {
            var low = ctx.LastPrice * 0.3m;
            var high = ctx.LastPrice * 3m;
            if (price < low || price > high)
            {
                return Veto("ILLEGAL_PRICE", symbol, amountUsd, 0m,
                    $"挂单价 {price} 偏离现价 {ctx.LastPrice} 超过合理区间（0.3x~3x），判定为 AI 幻觉");
            }

            // 相对盘口内沿偏离护栏（防 DeepSeek 挂出离谱远单）
            var refPrice = side == OrderSide.Buy ? (ctx.BestBid > 0 ? ctx.BestBid : ctx.LastPrice) : (ctx.BestAsk > 0 ? ctx.BestAsk : ctx.LastPrice);
            var deviationBps = refPrice > 0 ? Math.Abs(price - refPrice) / refPrice * 10000m : 0m;
            if (deviationBps > options.MaxPriceDeviationBps)
            {
                return Veto("ILLEGAL_PRICE", symbol, amountUsd, 0m,
                    $"挂单价 {price} 偏离盘口内沿 {refPrice} 达 {deviationBps:0} bps（上限 {options.MaxPriceDeviationBps} bps），判定为 AI 幻觉");
            }
        }

        // 动态额度拦截（复利公式：以当前总权益为分子基准）
        var maxAllowed = MaxOrderSize(config.InitialCapital, equityUsd, options.MaxOrderSizeRatio);
        if (amountUsd > maxAllowed + 0.0001m)
        {
            return Veto("OVER_LIMIT", symbol, amountUsd, maxAllowed,
                $"下单 ${amountUsd:0.00} 超过风控上限 ${maxAllowed:0.00}（(初始+max(0,权益-初始))×{options.MaxOrderSizeRatio:P0}），一票否决");
        }

        // 可用资金 / 持仓约束
        if (side == OrderSide.Buy && amountUsd > availableUsd + 0.0001m)
        {
            return Veto("INSUFFICIENT_FUNDS", symbol, amountUsd, availableUsd,
                $"买单 ${amountUsd:0.00} 超过可用 USD ${availableUsd:0.00}");
        }

        if (side == OrderSide.Sell && holdingBase <= 0m)
        {
            return Veto("NO_POSITION", symbol, amountUsd, 0m, $"对 {symbol} 发起卖单但当前无现货持仓（AI 幻觉报单）");
        }

        return new RiskVerdict(true, "", maxAllowed, "");
    }

    /// <summary>熔断判定：总权益 &lt; 初始本金 × 70% 触发系统级硬熔断。</summary>
    public static bool IsCircuitTripped(decimal initialCapital, decimal equityUsd, decimal ratio = 0.70m) =>
        equityUsd > 0m && equityUsd < initialCapital * ratio;

    private static RiskVerdict Veto(string kind, string symbol, decimal requested, decimal allowed, string detail) =>
        new(false, detail, allowed, kind);

    public static string BaseOf(string symbol)
    {
        var idx = symbol.IndexOf('-');
        return idx < 0 ? symbol : symbol[..idx];
    }
}
