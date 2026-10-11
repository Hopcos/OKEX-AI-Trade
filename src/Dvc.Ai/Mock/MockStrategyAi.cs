using System.Globalization;
using System.Text.Json;
using Dvc.Core;
using Dvc.Core.Ai;
using Dvc.Core.Configuration;

namespace Dvc.Ai.Mock;

/// <summary>
/// 内置规则 MockAI（离线演示/测试，Provider=Mock）。
/// 产出与 DeepSeek 完全同构的"思考链 → 标准 JSON 决策"流，字符级延迟模拟打字机。
/// 规则取向：数学期望值优先、零情绪；亏损事件优先判断"概率摩擦 or 宏观扭转"。
/// </summary>
public sealed class MockStrategyAi : IStrategyAi
{
    private readonly SimulationOptions _sim;

    public MockStrategyAi(SimulationOptions sim)
    {
        _sim = sim;
    }

    public string ProviderName => "MockAI";

    public async IAsyncEnumerable<AiStreamPart> DecideAsync(AiRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var input = request.Input;
        var sb = new System.Text.StringBuilder();

        var tick = BuildContext(input);

        // ---------- 思考链（规则推导的中文叙述） ----------
        var thinking = BuildThinking(tick);

        foreach (var c in thinking)
        {
            ct.ThrowIfCancellationRequested();
            sb.Append(c);
            yield return new AiStreamPart(true, c.ToString());
            await Task.Delay(Math.Max(1, _sim.MockAiCharDelayMs), ct);
        }

        // ---------- 最终 JSON 决策 ----------
        var decision = Decide(tick);
        var json = JsonSerializer.Serialize(decision, DvcJsonContext.Default.AiOutputDecision);
        yield return new AiStreamPart(false, json);
    }

    // ================= 上下文提取 =================

    private sealed record Ctx(
        string Trigger,
        decimal Equity,
        decimal Initial,
        decimal MaxAllowed,
        decimal LastLoss,
        decimal PositionBaseQty,
        decimal LastPrice,
        decimal BestBid,
        decimal BestAsk,
        decimal SpreadBps,
        decimal Imbalance,
        decimal Vol1H,
        decimal Change15M,
        string Trend,
        decimal Turnover5M,
        string TradingMode,
        string AssetScope,
        string ActiveSymbol,
        IReadOnlyCollection<string> Blacklist,
        Dictionary<string, (decimal Turn, decimal Imp, string Trend)>? Pairs)
    {
        public decimal Profit => Equity - Initial;

        public bool Holding => PositionBaseQty > 0.00000001m;

        public bool IsBearish => Trend is "BEARISH" or "BEARISH_STRONG";

        public bool IsBullish => Trend is "BULLISH" or "BULLISH_STRONG";
    }

    private static Ctx BuildContext(AiInputSnapshot i)
    {
        var fm = i.FinancialMetrics;
        var cfg = i.CurrentConfig;
        var ms = i.MarketSnapshot;

        var bids = ms.ActiveSymbolOrderbook?.Bid1To5 ?? [];
        var asks = ms.ActiveSymbolOrderbook?.Ask1To5 ?? [];
        var bidSum = bids.Sum(l => l.Count > 1 ? l[1] : 0m);
        var askSum = asks.Sum(l => l.Count > 1 ? l[1] : 0m);
        var bestBid = bids.Count > 0 ? bids[0][0] : ms.LastPrice;
        var bestAsk = asks.Count > 0 ? asks[0][0] : ms.LastPrice;
        var spreadBps = bestBid > 0 ? (bestAsk - bestBid) / bestBid * 10000m : 0m;
        var imbalance = bidSum + askSum > 0 ? (bidSum - askSum) / (bidSum + askSum) : 0m;

        Dictionary<string, (decimal Turn, decimal Imp, string Trend)>? pairs = null;
        if (i.PairSnapshots is { Count: > 0 })
        {
            pairs = i.PairSnapshots.ToDictionary(
                p => p.Symbol,
                p => (p.Turnover5MUsd, p.Imbalance, p.TrendSignal15M));
        }

        return new Ctx(
            i.TriggerReason,
            fm.CurrentTotalEquity,
            fm.InitialCapital,
            fm.MaxAllowedOrderSize,
            fm.LastTradeLossAmount,
            fm.PositionBaseQty,
            ms.LastPrice,
            bestBid,
            bestAsk,
            spreadBps,
            imbalance,
            ms.Volatility1HPercent,
            ms.Change15MPercent,
            ms.TrendSignal15M,
            ms.Turnover5MUsd,
            cfg.TradingMode,
            cfg.AssetScope,
            cfg.ActiveSymbol,
            cfg.Blacklist,
            pairs);
    }

    // ================= 思考链文本 =================

    private static string BuildThinking(Ctx c)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"触发源: {c.Trigger} | 时间: {DateTime.UtcNow:u}");
        sb.AppendLine();

        if (c.LastLoss < 0m || c.Trigger == "LOSS_EVENT_WAKEUP")
        {
            sb.AppendLine($"—— 亏损复盘 —— 最近一笔亏损 ${Math.Abs(c.LastLoss):0.00}，总权益 ${c.Equity:0.00} vs 初始 ${c.Initial:0.00}。");
            if (c.IsBearish)
            {
                sb.AppendLine("盘面判断：15M 强空头 + 1H 波幅放大，这不是普通概率摩擦，而是环境扭转。单边下跌中高频剥头皮等于反复接飞刀，数学期望为负。");
                sb.AppendLine("对策：切换至 Swing 模式追踪趋势/避让，并撤掉所有残余挂单锁定 USD 现金，等待空头动能衰竭的企稳信号。");
            }
            else
            {
                sb.AppendLine("盘面判断：趋势信号中性/偏多，此次亏损落在统计噪声带内，属于正常概率摩擦。维持高频模式，但收紧挂单价差并降低单笔暴露。");
            }
        }
        else
        {
            sb.AppendLine("—— 宏观评估 —— " + (c.Trigger == "MACRO_HOURLY" ? "到达 1 小时宏观轮询点，评估市场状态。" : "进入策略节拍，评估盘口微观结构。"));
        }

        sb.AppendLine(
            $"—— 盘口微观 —— {c.ActiveSymbol} 买一 {c.BestBid:0.######} / 卖一 {c.BestAsk:0.######}，价差 {c.SpreadBps:0.0} bps，" +
            $"不平衡度 {(c.Imbalance >= 0 ? "+" : "")}{c.Imbalance:0.000}，5M 成交额 ${c.Turnover5M:0}，15M 涨跌 {c.Change15M:+#0.00;-#0.00;0.00}%，1H 波幅 {c.Vol1H:0.00}%。");

        if (c.AssetScope == "Multi-Pair" && c.Pairs is { Count: > 1 })
        {
            var best = c.Pairs.Where(p => !c.Blacklist.Contains(BaseOf(p.Key)))
                .OrderByDescending(p => p.Value.Turn * (1 + p.Value.Imp))
                .FirstOrDefault();
            sb.AppendLine($"—— 资金调度 —— 多币种轮动扫描 {c.Pairs.Count} 个候选，按(成交额×资金流)排序，最优聚焦 {best.Key ?? "无"}（避开黑名单）。");
        }

        sb.AppendLine();
        sb.AppendLine($"—— 决策结论 —— ");
        return sb.ToString();
    }

    // ================= 决策规则 =================

    private static AiOutputDecision Decide(Ctx c)
    {
        var d = new AiOutputDecision
        {
            DecisionLogic = new AiDecisionLogic(),
            TargetConfigStatus = new AiTargetConfig
            {
                TradingMode = c.TradingMode,
                AssetScope = c.AssetScope,
                ActiveSymbol = c.ActiveSymbol,
            },
            ImmediateExecutionOrder = new AiExecutionOrder
            {
                ExecuteNow = true,
                Action = "HOLD",
                TargetSymbol = c.ActiveSymbol,
                ProposedOrderSizeUsd = 0m,
                TtlSeconds = 120,
            },
        };

        if (c.Equity <= c.Initial * 0.35m)
        {
            // 接近熔断线：AI 主动退守现金
            d.DecisionLogic.Diagnosis = "权益逼近硬熔断红线，AI 主动清仓退守现金观望";
            d.DecisionLogic.ActionType = "MODE_SWITCH";
            d.ImmediateExecutionOrder.Action = "CANCEL_ALL_AND_WAIT";
            return d;
        }

        // 1) 亏损唤醒：环境扭转判定优先
        if (c.Trigger == "LOSS_EVENT_WAKEUP" || c.LastLoss < 0m)
        {
            if (c.IsBearish)
            {
                d.DecisionLogic.Diagnosis = "市场进入单边空头，高频剥头皮期望为负，切换 Swing 追踪趋势并锁定现金";
                d.DecisionLogic.ActionType = "MODE_SWITCH";
                d.TargetConfigStatus.TradingMode = "Swing";
                d.ImmediateExecutionOrder.Action = "CANCEL_ALL_AND_WAIT";
                d.ImmediateExecutionOrder.ProposedOrderSizeUsd = 0m;
                return d;
            }

            d.DecisionLogic.Diagnosis = "亏损判定为正常概率摩擦：趋势仍中性/偏多，维持高频模式，收紧价差小步试探";
            d.DecisionLogic.ActionType = "HOLD";
            d.ImmediateExecutionOrder.Action = "HOLD";
            return d;
        }

        // 2) 小时宏观：波动率与单边强度决定模式
        if (c.Trigger == "MACRO_HOURLY")
        {
            if (c.Vol1H > 4.5m && (c.Trend is "BULLISH_STRONG" or "BEARISH_STRONG"))
            {
                d.DecisionLogic.Diagnosis = $"1H 波幅 {c.Vol1H:0.0}% 且单边强度大，切 Swing 追踪趋势";
                d.DecisionLogic.ActionType = "MODE_SWITCH";
                d.TargetConfigStatus.TradingMode = "Swing";
                d.ImmediateExecutionOrder.Action = c.IsBearish ? "CANCEL_ALL_AND_WAIT" : "HOLD";
                return d;
            }

            if (c.TradingMode == "Swing" && c.Vol1H < 1.5m && c.Trend == "NEUTRAL")
            {
                d.DecisionLogic.Diagnosis = "市场进入低波动震荡，Swing 无利可图，回到 Scalping 吃盘口价差";
                d.DecisionLogic.ActionType = "MODE_SWITCH";
                d.TargetConfigStatus.TradingMode = "Scalping";
                d.ImmediateExecutionOrder.Action = "HOLD";
                return d;
            }

            d.DecisionLogic.Diagnosis = "宏观状态无显著变化，维持当前模式";
            d.DecisionLogic.ActionType = "HOLD";
            d.ImmediateExecutionOrder.Action = "HOLD";
            return d;
        }

        // 3) 策略节拍（Scalping / Swing）与多币种轮动
        if (c.AssetScope == "Multi-Pair" && c.Pairs is { Count: > 1 })
        {
            var candidate = c.Pairs
                .Where(p => !c.Blacklist.Contains(BaseOf(p.Key)))
                .OrderByDescending(p => p.Value.Turn * (1 + p.Value.Imp))
                .FirstOrDefault();
            if (candidate.Key is not null)
            {
                d.TargetConfigStatus.ActiveSymbol = candidate.Key;
                d.DecisionLogic.Diagnosis = $"资金调度：聚焦 {candidate.Key}（成交额高+资金流入），其余币种现金观望";
                d.DecisionLogic.ActionType = "MODE_SWITCH";
                d.ImmediateExecutionOrder.TargetSymbol = candidate.Key;
                d.ImmediateExecutionOrder.Action = "PLACE_LIMIT_BUY";
                d.ImmediateExecutionOrder.ProposedOrderSizeUsd = CapUsd(c, c.MaxAllowed);
                return d;
            }
        }

        var held = c.Holding;
        var size = CapUsd(c, c.MaxAllowed);

        if (c.TradingMode == "Swing")
        {
            if (c.IsBullish && !held)
            {
                d.DecisionLogic.Diagnosis = "Swing 多头信号成立，挂单吃回调建仓";
                d.DecisionLogic.ActionType = "PLACE_LIMIT_BUY";
                d.ImmediateExecutionOrder.Action = "PLACE_LIMIT_BUY";
                d.ImmediateExecutionOrder.Side = "BUY";
                d.ImmediateExecutionOrder.Price = RoundPx(c.BestBid);
                d.ImmediateExecutionOrder.ProposedOrderSizeUsd = size;
                return d;
            }

            if (c.IsBearish && held)
            {
                d.DecisionLogic.Diagnosis = "Swing 空头信号确认，止盈卖出锁定利润";
                d.DecisionLogic.ActionType = "PLACE_LIMIT_SELL";
                d.ImmediateExecutionOrder.Action = "PLACE_LIMIT_SELL";
                d.ImmediateExecutionOrder.Side = "SELL";
                d.ImmediateExecutionOrder.Price = RoundPx(c.BestAsk);
                d.ImmediateExecutionOrder.ProposedOrderSizeUsd = size;
                return d;
            }

            d.DecisionLogic.Diagnosis = "Swing 信号不足（无强趋势），按兵不动";
            d.DecisionLogic.ActionType = "HOLD";
            d.ImmediateExecutionOrder.Action = "HOLD";
            return d;
        }

        // Scalping：盘口不平衡 + 趋势过滤
        if (!held && c.Imbalance > 0.12m && !c.IsBearish && c.SpreadBps < 20m)
        {
            d.DecisionLogic.Diagnosis = $"买盘不平衡 {(c.Imbalance):0.000}，挂买一价吃微套";
            d.DecisionLogic.ActionType = "PLACE_LIMIT_BUY";
            d.ImmediateExecutionOrder.Action = "PLACE_LIMIT_BUY";
            d.ImmediateExecutionOrder.Side = "BUY";
            d.ImmediateExecutionOrder.Price = RoundPx(c.BestBid);
            d.ImmediateExecutionOrder.ProposedOrderSizeUsd = size;
            return d;
        }

        if (held && (c.Imbalance < -0.10m || c.IsBearish))
        {
            d.DecisionLogic.Diagnosis = c.IsBearish ? "趋势转空，卖出离场规避下跌" : "卖盘压力增强，微套离场";
            d.DecisionLogic.ActionType = "PLACE_LIMIT_SELL";
            d.ImmediateExecutionOrder.Action = "PLACE_LIMIT_SELL";
            d.ImmediateExecutionOrder.Side = "SELL";
            d.ImmediateExecutionOrder.Price = RoundPx(c.BestAsk);
            d.ImmediateExecutionOrder.ProposedOrderSizeUsd = size;
            return d;
        }

        d.DecisionLogic.Diagnosis = $"微观结构中性（不平衡 {c.Imbalance:0.000}），等待更优期望值窗口";
        d.DecisionLogic.ActionType = "HOLD";
        d.ImmediateExecutionOrder.Action = "HOLD";
        return d;
    }

    private static decimal CapUsd(Ctx c, decimal request)
    {
        var cap = Math.Min(request, c.MaxAllowed);
        return Math.Max(0m, Math.Round(cap, 2, MidpointRounding.AwayFromZero));
    }

    private static decimal RoundPx(decimal px) => Math.Round(px, 6, MidpointRounding.AwayFromZero);

    private static string BaseOf(string symbol) => symbol.Split('-')[0];
}
