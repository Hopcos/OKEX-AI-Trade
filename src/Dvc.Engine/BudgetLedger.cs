using Dvc.Core.Trading;

namespace Dvc.Engine;

/// <summary>预算账本重放结果：现金、各交易对持仓（数量 + 含手续费均价）、累计已实现盈亏。</summary>
public sealed record LedgerView(
    decimal Cash,
    IReadOnlyDictionary<string, (decimal Qty, decimal AvgCost)> Positions,
    decimal RealizedPnl,
    IReadOnlyDictionary<long, decimal> SellRealizedByOrderId);

/// <summary>
/// 预算账本：Live 模式下交易所账户往往远大于"本金预算"，直接读交易所权益会把无关资金算进盈亏。
/// 账本只按本系统自己的成交流水重放（本金 → 买卖 → 现金/持仓/已实现盈亏），
/// 使盈亏、权益、下单上限、熔断线都只围绕"给定的这笔钱"。纯函数，便于单测。
/// </summary>
public static class BudgetLedger
{
    public static LedgerView Replay(IEnumerable<OrderRecord> fills, decimal initialCapital, decimal feeRate, decimal? sellFeeRate = null)
    {
        var sellFee = sellFeeRate ?? feeRate; // 买入按 maker，卖出按 taker（可分别配置）
        var cash = initialCapital;
        var realized = 0m;
        var pos = new Dictionary<string, (decimal Qty, decimal AvgCost)>();
        var sellRealized = new Dictionary<long, decimal>();

        foreach (var o in fills)
        {
            var qty = o.FilledBaseAmount;
            var px = o.FilledAvgPrice > 0m ? o.FilledAvgPrice : o.Price;
            if (qty <= 0m || px <= 0m)
            {
                continue;
            }

            pos.TryGetValue(o.Symbol, out var p);
            if (o.IsBuy)
            {
                var cost = qty * px * (1m + feeRate);
                cash -= cost;
                var totalQty = p.Qty + qty;
                pos[o.Symbol] = (totalQty, (p.Qty * p.AvgCost + cost) / totalQty);
            }
            else
            {
                var sellQty = Math.Min(qty, p.Qty);
                if (sellQty <= 0m)
                {
                    continue; // 账本里没有这份持仓（预算外的卖出），忽略
                }

                var proceeds = sellQty * px * (1m - sellFee);
                cash += proceeds;
                var pnl = proceeds - sellQty * p.AvgCost;
                realized += pnl;
                sellRealized[o.Id] = pnl;
                var left = p.Qty - sellQty;
                if (left <= 0.00000001m)
                {
                    pos.Remove(o.Symbol);
                }
                else
                {
                    pos[o.Symbol] = (left, p.AvgCost);
                }
            }
        }

        return new LedgerView(cash, pos, realized, sellRealized);
    }
}
