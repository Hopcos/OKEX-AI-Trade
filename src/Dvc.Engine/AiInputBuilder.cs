using Dvc.Core;
using Dvc.Core.Ai;
using Dvc.Core.Market;
using Dvc.Core.Trading;

namespace Dvc.Engine;

/// <summary>
/// 动态输入结构（User Message JSON 快照）组装器 —— 与《最终版技术方案》第四节的
/// 输入结构逐字段对齐（含 Multi-Pair 扩展）。
/// </summary>
public static class AiInputBuilder
{
    public static AiInputSnapshot Build(
        DecisionTrigger trigger,
        TradingConfig config,
        AccountState account,
        MarketContext active,
        IReadOnlyList<MarketContext>? candidates,
        decimal lastLoss)
    {
        var triggerName = trigger switch
        {
            DecisionTrigger.MacroHourly => "MACRO_HOURLY",
            DecisionTrigger.LossEventWakeup => "LOSS_EVENT_WAKEUP",
            DecisionTrigger.Startup => "STARTUP",
            _ => "STRATEGY_TICK",
        };

        var book = new AiOrderbook
        {
            Bid1To5 = active.BestBid > 0 ? ToLevels(active, isBid: true) : [],
            Ask1To5 = active.BestAsk > 0 ? ToLevels(active, isBid: false) : [],
        };

        var input = new AiInputSnapshot
        {
            TriggerReason = triggerName,
            SystemTime = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            FinancialMetrics = new AiFinancialMetrics
            {
                InitialCapital = Math.Round(config.InitialCapital, 2),
                CurrentTotalEquity = Math.Round(account.EquityUsd, 2),
                AccumulatedProfit = Math.Round(account.AccumulatedProfit, 2),
                MaxAllowedOrderSize = Math.Round(account.MaxOrderSize, 2),
                LastTradeLossAmount = Math.Round(lastLoss, 2),
                PositionBaseQty = account.Holdings.TryGetValue(RiskEngine.BaseOf(active.Symbol), out var qty) ? qty : 0m,
                RealizedPnl = Math.Round(account.RealizedPnl, 2),
            },
            CurrentConfig = new AiCurrentConfig
            {
                TradingMode = config.TradingMode == TradingMode.Swing ? "Swing" : "Scalping",
                AssetScope = config.AssetScope == AssetScope.MultiPair ? "Multi-Pair" : "Single-Pair",
                ActiveSymbol = config.ActiveSymbol,
                Blacklist = [.. config.Blacklist],
            },
            MarketSnapshot = new AiMarketSnapshot
            {
                ActiveSymbolOrderbook = book,
                LastPrice = active.LastPrice,
                Volatility1HPercent = active.Volatility1HPercent,
                TrendSignal15M = active.TrendSignal15M,
                Change15MPercent = active.Change15MPercent,
                Turnover5MUsd = active.Turnover5M,
            },
        };

        if (config.AssetScope == AssetScope.MultiPair && candidates is { Count: > 0 })
        {
            input.PairSnapshots = candidates
                .Select(c => new AiPairSnapshot
                {
                    Symbol = c.Symbol,
                    LastPrice = c.LastPrice,
                    SpreadBps = Math.Round(c.SpreadBps, 2),
                    Turnover5MUsd = Math.Round(c.Turnover5M, 2),
                    Volatility1HPercent = Math.Round(c.Volatility1HPercent, 2),
                    Change15MPercent = Math.Round(c.Change15MPercent, 3),
                    Imbalance = Math.Round(c.Imbalance, 3),
                    TrendSignal15M = c.TrendSignal15M,
                })
                .ToList();
        }

        return input;
    }

    /// <summary>盘口五档 → [[price, size], ...]，无真实档位时用对称价位近似。</summary>
    private static List<List<decimal>> ToLevels(MarketContext ctx, bool isBid)
    {
        var levels = new List<List<decimal>>();
        var source = isBid ? ctx.BidLevels : ctx.AskLevels;
        if (source.Count > 0)
        {
            foreach (var lvl in source.Take(5))
            {
                levels.Add([Math.Round(lvl.Price, 6, MidpointRounding.AwayFromZero), Math.Round(lvl.Size, 6)]);
            }

            return levels;
        }

        var anchor = isBid ? ctx.BestBid : ctx.BestAsk;
        if (anchor <= 0)
        {
            return levels;
        }

        var stepBps = Math.Max(ctx.SpreadBps / 2m, 0.5m);
        for (var i = 0; i < 5; i++)
        {
            var price = isBid
                ? anchor * (1m - i * stepBps / 10000m)
                : anchor * (1m + i * stepBps / 10000m);
            var size = 0.001m + (decimal)(Math.Sin(i * 31.4) * 0.5 + 0.5) * 2m;
            levels.Add([Math.Round(price, 6, MidpointRounding.AwayFromZero), Math.Round(size, 6)]);
        }

        return levels;
    }
}
