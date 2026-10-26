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
    /// 动态下单额度：最大允许买单额 = min(当前总权益 × 比例, 绝对上限)。
    /// 总权益 = 本金预算 ± 盈亏，所以盈利按比例复利抬升、亏损自动缩减，下单永远基于"这笔钱现在值多少"。
    /// absoluteCap &lt;= 0 表示不启用绝对上限。
    /// </summary>
    public static decimal MaxOrderSize(decimal equityUsd, decimal ratio = 0.20m, decimal absoluteCap = 0m)
    {
        var byEquity = Math.Max(0m, equityUsd) * ratio;
        var limit = absoluteCap > 0m ? Math.Min(byEquity, absoluteCap) : byEquity;
        return Math.Round(limit, 2, MidpointRounding.ToZero);
    }

    /// <summary>
    /// 当前还能新买多少（USD）= min(单笔买单上限, 总仓位上限剩余空间, 可用现金)。
    /// 已占用（持仓市值 + 挂买单冻结）优先用账户给出的真实值（<see cref="AccountState.CommittedUsd"/>）；
    /// 缺省才按 权益 − 可自由动用现金 推算。
    /// </summary>
    public static decimal BuyRoom(decimal equityUsd, decimal availableUsd, TradingOptions options, decimal? committedUsd = null)
    {
        var perOrder = MaxOrderSize(equityUsd, options.MaxOrderSizeRatio, options.MaxOrderUsd);
        var committed = committedUsd ?? Math.Max(0m, equityUsd - availableUsd);
        var exposureRoom = ExposureCap(equityUsd, options) - committed;
        return Math.Max(0m, Math.Round(Math.Min(perOrder, Math.Min(exposureRoom, availableUsd)), 2, MidpointRounding.ToZero));
    }

    /// <summary>总仓位上限（USD）：至少容得下一笔单笔买单，避免配置失误导致永远不能建仓。</summary>
    public static decimal ExposureCap(decimal equityUsd, TradingOptions options) =>
        Math.Max(0m, equityUsd) * Math.Max(options.MaxExposureRatio, options.MaxOrderSizeRatio);

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
        decimal equityUsd,
        decimal? committedUsd = null)
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

        // 动态额度拦截：只限制买单（买单才新增风险敞口）；卖单是减仓，受持仓数量约束即可，
        // 否则持仓增值/多次建仓后会出现"一笔卖不掉"的反向风险。
        var maxAllowed = MaxOrderSize(equityUsd, options.MaxOrderSizeRatio, options.MaxOrderUsd);
        if (side == OrderSide.Buy && amountUsd > maxAllowed + 0.0001m)
        {
            return Veto("OVER_LIMIT", symbol, amountUsd, maxAllowed,
                $"买单 ${amountUsd:0.00} 超过风控上限 ${maxAllowed:0.00}（当前总权益 ${equityUsd:0.00} × {options.MaxOrderSizeRatio:P0}"
                + (options.MaxOrderUsd > 0m ? $"，绝对上限 ${options.MaxOrderUsd:0.00}" : "") + "），一票否决");
        }

        // 总仓位上限：保留现金，保证卖出回笼后还能继续循环
        if (side == OrderSide.Buy)
        {
            var committed = committedUsd ?? Math.Max(0m, equityUsd - availableUsd);
            var cap = ExposureCap(equityUsd, options);
            if (committed + amountUsd > cap + 0.0001m)
            {
                return Veto("OVER_EXPOSURE", symbol, amountUsd, Math.Max(0m, cap - committed),
                    $"买单 ${amountUsd:0.00} 将使总仓位达 ${committed + amountUsd:0.00}，超过总仓位上限 ${cap:0.00}（权益 × {Math.Max(options.MaxExposureRatio, options.MaxOrderSizeRatio):P0}），需先卖出回笼现金");
            }
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
