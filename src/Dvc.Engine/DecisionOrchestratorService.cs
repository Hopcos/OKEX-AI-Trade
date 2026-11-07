using System.Text.Json;
using System.Threading.Channels;
using Dvc.Ai;
using Dvc.Core;
using Dvc.Core.Ai;
using Dvc.Core.Events;
using Dvc.Core.Trading;
using Dvc.Persistence;
using Microsoft.Extensions.Hosting;

namespace Dvc.Engine;

/// <summary>
/// 决策中心（Intern-Decision）—— 唯一 AI 串行化推理点。
/// - 高优先级唤醒（亏损事件/宏观轮询/人工切换）优先于低频节拍，满足"无视定时器物理触发"。
/// - 完整流水线：账户刷新 → 微观结构快照组装（User Message JSON）→ AI 思考链流 →
///   标准 JSON 决策 → 硬编码风控一票否决 → 执行命令下发 → 思考日志落库。
/// - 每小时 PeriodicTimer 触发 MACRO_HOURLY（时间轮询轨）。
/// </summary>
public sealed class DecisionOrchestratorService : BackgroundService
{
    private readonly RuntimeState _runtime;
    private readonly AccountService _account;
    private readonly MarketContextStore _market;
    private readonly Func<AiProvider, IStrategyAi> _aiFactory;
    private readonly OrderExecutionService _executor;
    private readonly ThoughtRepository _thoughts;
    private readonly RiskInterceptRepository _intercepts;
    private readonly TradingConfigStore _configStore;
    private readonly OrderRepository _orders;
    private readonly BrainService _brain;
    private readonly DateTime _startedAtUtc = DateTime.UtcNow;
    private DateTime _lastPlacedAtUtc = DateTime.MinValue;
    private readonly EngineEventBus _bus;
    private readonly ILogger _log;
    private readonly Dvc.Core.Configuration.TradingOptions _options;

    private readonly SemaphoreSlim _serial = new(1, 1);

    /// <summary>最近一次 AI 调用启动时刻（节流判定基准）。</summary>
    private DateTime _lastDecisionStartedAt = DateTime.MinValue;

    public DecisionOrchestratorService(
        RuntimeState runtime,
        AccountService account,
        MarketContextStore market,
        Func<AiProvider, IStrategyAi> aiFactory,
        OrderExecutionService executor,
        ThoughtRepository thoughts,
        RiskInterceptRepository intercepts,
        TradingConfigStore configStore,
        OrderRepository orders,
        BrainService brain,
        EngineEventBus bus,
        ILogger log,
        Dvc.Core.Configuration.TradingOptions options)
    {
        _runtime = runtime;
        _account = account;
        _market = market;
        _aiFactory = aiFactory;
        _executor = executor;
        _thoughts = thoughts;
        _intercepts = intercepts;
        _configStore = configStore;
        _orders = orders;
        _brain = brain;
        _bus = bus;
        _log = log;
        _options = options;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 启动自检：结算一次基准，唤醒第一次决策（立即观察盘面）
        await _account.SettleAsync("STARTUP", stoppingToken);
        _runtime.WakeHigh(DecisionTrigger.Startup, "SYSTEM_START");

        using var macroTimer = new PeriodicTimer(TimeSpan.FromHours(1));
        var macroTask = MacroLoopAsync(macroTimer, stoppingToken);
        var decisionTask = DecisionLoopAsync(stoppingToken);
        try
        {
            await Task.WhenAll(macroTask, decisionTask);
        }
        catch (OperationCanceledException)
        {
            // 正常停机
        }
    }

    /// <summary>时间轮询轨：每小时宏观评估（先结算基准再唤醒 AI）。</summary>
    private async Task MacroLoopAsync(PeriodicTimer timer, CancellationToken ct)
    {
        while (await timer.WaitForNextTickAsync(ct))
        {
            if (_runtime.IsHalted)
            {
                continue;
            }

            try
            {
                await _account.SettleAsync("HOURLY", ct);
                _runtime.WakeHigh(DecisionTrigger.MacroHourly, "MACRO_1H");
                _log.LogInfo("1 小时宏观轮询：结算完成，唤醒 AI 宏观评估");
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.LogError("宏观轮询结算失败: " + ex.Message, ex);
            }
        }
    }

    /// <summary>串行推理主循环（高优先级通道优先）。</summary>
    private async Task DecisionLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (_runtime.IsHalted)
            {
                // 硬熔断：永久终止 AI 决策进程
                _log.LogWarn("硬熔断已触发：AI 决策进程永久停止");
                return;
            }

            DecisionRequest req;
            var fromHigh = true;
            if (_runtime.HighPriority.TryRead(out var highReq))
            {
                req = highReq!;
            }
            else if (_runtime.LowPriority.TryRead(out var lowReq))
            {
                req = lowReq!;
                fromHigh = false;
            }
            else
            {
                // 双通道阻塞等待（高优先先到先得）
                var highTask = _runtime.HighPriority.ReadAsync(ct).AsTask();
                var lowTask = _runtime.LowPriority.ReadAsync(ct).AsTask();
                var finished = await Task.WhenAny(highTask, lowTask);
                req = await finished;
                fromHigh = finished == highTask;
            }

            if (ct.IsCancellationRequested || _runtime.IsHalted)
            {
                continue;
            }

            // AI 调用节流：LLM 提供方下，低优先级策略节拍距上轮不足最小间隔则丢弃本次节拍
            // （高优先级亏损/宏观/控制唤醒不节流；Mock 本地提供方不节流）
            if (!fromHigh && _options.AiProvider != AiProvider.Mock &&
                DateTime.UtcNow - _lastDecisionStartedAt <
                TimeSpan.FromSeconds(Math.Max(1.0, _options.AiMinIntervalSeconds)))
            {
                continue;
            }

            await _serial.WaitAsync(ct);
            try
            {
                _lastDecisionStartedAt = DateTime.UtcNow;
                await ProcessDecisionAsync(req, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.LogError($"决策处理异常 [{req.Trigger}]: {ex.Message}", ex);
                PublishFlow($"决策异常 [{req.Trigger}]：{ex.Message}");
            }
            finally
            {
                _serial.Release();
            }
        }
    }

    private async Task ProcessDecisionAsync(DecisionRequest req, CancellationToken ct)
    {
        var cfg = _runtime.Snapshot();
        var state = await _account.RefreshAsync(ct);

        var activeContext = _market.Build(cfg.ActiveSymbol);
        if (activeContext.LastPrice <= 0)
        {
            PublishFlow($"行情未就绪（{cfg.ActiveSymbol}），本次 {req.Trigger} 决策跳过");
            return;
        }

        // 强制开关：Trading:ForceInternDecision=true → 每轮决策强制 InternDecision（无视看板/DB 热切换）
        var effectiveProvider = _options.ForceInternDecision ? AiProvider.InternDecision : cfg.AiProvider;

        // 关键阶段（启动 / 每小时宏观 / 亏损事件）先让大脑（LLM）复盘，赋予本轮决策"灵魂"；
        // 其余节拍只读取最近一份有效指引。大脑仅在 InternDecision 模式下生效。
        BrainDirective? directive = null;
        if (effectiveProvider == AiProvider.InternDecision)
        {
            directive = req.Trigger == DecisionTrigger.StrategyTick
                ? _brain.Current
                : await _brain.ReviewOnKeyStageAsync(req.Trigger, ct) ?? _brain.Current;
        }

        // 空转时长：距上一次买单（含被拒/撤销的）或程序启动已过多久；一直不入场就没有收益，供 InternDecision 兜底试单。
        // 只看买单：持仓管家反复撤挂卖单不算"在交易"
        var lastOrderAt = (await _orders.ListRecentAsync(30, ct)).FirstOrDefault(o => o.Side == OrderSide.Buy)?.CreatedAtUtc ?? DateTime.MinValue;
        var idleSince = new[] { lastOrderAt, _lastPlacedAtUtc, _startedAtUtc }.Max();
        var idleMinutes = (decimal)(DateTime.UtcNow - idleSince).TotalMinutes;

        // 候选池：Multi-Pair 常驻；Single-Pair 下一旦空转到该试单了也要提供（行情仍订阅着 WatchSymbols），否则已持仓时无处可试
        var probeDue = _options.Profit.Enabled && _options.Profit.IdleProbeMinutes > 0 &&
                       idleMinutes >= _options.Profit.IdleProbeMinutes;
        var candidates = cfg.AssetScope == AssetScope.MultiPair || probeDue
            ? _market.BuildAll(cfg.WatchSymbols)
            : null;

        var input = AiInputBuilder.Build(req.Trigger, cfg, state, activeContext, candidates,req.LastLoss, _options, directive, idleMinutes);
        var request = new AiRequest(SystemPromptFactory.Build(), input);
        var provider = _aiFactory(effectiveProvider);
        var sessionId = Guid.NewGuid().ToString("N");

        _log.LogInfo($"[决策开始] {sessionId[..8]} trigger={req.Trigger} provider={provider.ProviderName}");

        // ---------- AI 推理（思考链流式转发到看板打字机） ----------
        var thinking = new System.Text.StringBuilder();
        var decisionJson = new System.Text.StringBuilder();
        await foreach (var part in provider.DecideAsync(request, ct))
        {
            if (part.IsThinking)
            {
                thinking.Append(part.Text);
                _bus.Publish(EngineEventKind.ThoughtChunk,
                    JsonSerializer.Serialize(new UiThoughtChunk
                    {
                        SessionId = sessionId,
                        Trigger = req.Trigger.ToString(),
                        Text = part.Text,
                    }, DvcJsonContext.Default.UiThoughtChunk));
            }
            else
            {
                decisionJson.Append(part.Text);
            }
        }

        var finalJson = decisionJson.ToString();
        AiOutputDecision? decision = null;
        var parseFailed = false;
        try
        {
            decision = JsonSerializer.Deserialize(finalJson, DvcJsonContext.Default.AiOutputDecision);
        }
        catch (JsonException)
        {
            parseFailed = true;
        }

        if (decision is null || parseFailed)
        {
            var thought = await SaveThoughtAsync(sessionId, req, cfg, input, thinking.ToString(), finalJson,
                "PARSE_FAILED", "PARSE_FAILED", "HOLD", vetoed: false, "", state.EquityUsd, ct);
            _log.LogError($"AI 输出 JSON 解析失败，已记录思考日志 #{thought.Id}");
            PublishFlow($"AI 输出解析失败（已记录日志 #{thought.Id}）");
            return;
        }

        var diagnosis = decision.DecisionLogic.Diagnosis ?? "";
        var actionType = decision.DecisionLogic.ActionType ?? "HOLD";
        var targetSymbol = string.IsNullOrWhiteSpace(decision.ImmediateExecutionOrder.TargetSymbol)
            ? cfg.ActiveSymbol
            : decision.ImmediateExecutionOrder.TargetSymbol;

        // ---------- 配置变更（模式/范围/聚焦标的） ----------
        var configChanged = ApplyTargetConfig(decision);
        if (configChanged)
        {
            cfg = _runtime.Snapshot();
        }

        // ---------- 执行订单 & 风控一票否决 ----------
        var vetoed = false;
        var vetoReason = "";
        var exec = decision.ImmediateExecutionOrder;
        if (exec.ExecuteNow)
        {
            switch (exec.Action?.ToUpperInvariant())
            {
                case "PLACE_LIMIT_BUY":
                case "PLACE_LIMIT_SELL":
                {
                    var side = string.Equals(exec.Side, "SELL", StringComparison.OrdinalIgnoreCase)
                        ? OrderSide.Sell
                        : OrderSide.Buy;
                    // 下单前重新读盘口：AI 推理要几秒，决策开始时读到的买一/卖一价可能早已过期（尤其是跨价差买入）
                    var ctx = _market.Build(targetSymbol);
                    if (ctx.LastPrice <= 0m)
                    {
                        ctx = targetSymbol == cfg.ActiveSymbol ? activeContext : ctx;
                    }
                    var explicitPrice = exec.Price;
                    var price = explicitPrice is > 0
                        ? explicitPrice.Value
                        : side == OrderSide.Buy
                            // 跨价差买入：限价挂在卖一价上方留 5bp 缓冲——决策到下单有延迟，卖一价会跳，刚好挂在旧卖一价常常追不上；
                            // 仍是限价单，按卖盘价成交，最多多付 5bp
                            ? (exec.CrossSpread && ctx.BestAsk > 0m ? ctx.BestAsk * 1.0005m : ctx.BestBid)
                            : ctx.BestAsk;
                    if (price <= 0)
                    {
                        price = ctx.LastPrice;
                    }

                    var amount = exec.ProposedOrderSizeUsd;
                    var holdingBase = state.Holdings.TryGetValue(RiskEngine.BaseOf(targetSymbol), out var hb) ? hb : 0m;
                    if (amount <= 0m && side == OrderSide.Sell)
                    {
                        amount = holdingBase * ctx.LastPrice;
                    }

                    // ---- 灵魂护栏（先于风控）：永远朝盈利方向 ----
                    // 尘埃（< $1）不算持仓；跨价差买入 = 空转试单，只被硬止血拦（LLM 软禁买不拦，否则永远没有成交）
                    var holdingNow = holdingBase * ctx.LastPrice >= 1m;
                    if (side == OrderSide.Buy && directive is not null && directive.BlocksBuys(holdingNow && !exec.CrossSpread))
                    {
                        PublishFlow($"🧠 大脑指引禁止新开买单（{directive.Posture}）：{targetSymbol} 买单暂缓，只许卖出/观望");
                        diagnosis += " | 大脑止血：本轮禁止新买";
                        break;
                    }

                    // ---- 买入质量护栏：宁可少做，也不逆势/摊低成本/连环扫货 ----
                    if (side == OrderSide.Buy && _options.Profit.Enabled)
                    {
                        var pf = _options.Profit;
                        var baseCcyG = RiskEngine.BaseOf(targetSymbol);
                        string? guard = null;
                        if (pf.BlockBuysInDowntrend && ctx.TrendSignal15M is "BEARISH" or "BEARISH_STRONG")
                        {
                            guard = $"15M 趋势 {ctx.TrendSignal15M}（{ctx.Change15MPercent:0.00}%）下跌中不接刀";
                        }
                        else if (pf.NoAverageDownPercent > 0m && holdingNow &&
                                 state.AvgCosts.TryGetValue(baseCcyG, out var avgG) && avgG > 0m && ctx.LastPrice > 0m &&
                                 (avgG - ctx.LastPrice) / avgG * 100m > pf.NoAverageDownPercent)
                        {
                            guard = $"持仓浮亏 {(avgG - ctx.LastPrice) / avgG * 100m:0.00}% > {pf.NoAverageDownPercent:0.##}%，禁止摊低成本";
                        }
                        else if (pf.BuyCooldownSeconds > 0 &&
                                 state.PositionAgeMinutes.TryGetValue(baseCcyG, out var ageG) && holdingNow &&
                                 ageG * 60m < pf.BuyCooldownSeconds)
                        {
                            guard = $"距上次买入仅 {ageG * 60m:0}s < 冷却 {pf.BuyCooldownSeconds}s";
                        }

                        if (guard is not null)
                        {
                            PublishFlow($"🛡 买入护栏：{targetSymbol} {guard}，本轮不买");
                            diagnosis += $" | 买入护栏：{guard}";
                            break;
                        }
                    }

                    // 买入前看仓位余地：仓位已满就专注卖出回笼现金；有余地则把额度收敛到余地内
                    if (side == OrderSide.Buy)
                    {
                        var room = RiskEngine.BuyRoom(state.EquityUsd, state.AvailableUsd, _options, state.CommittedUsd);
                        if (room < 1m)
                        {
                            PublishFlow($"🔁 仓位已满（可新买余地 ${room:0.00}），本轮不再买入，专注卖出回笼现金");
                            diagnosis += " | 仓位已满，专注卖出";
                            break;
                        }

                        if (amount > room)
                        {
                            PublishFlow($"🔁 买单额度 ${amount:0.00} 收敛到仓位余地 ${room:0.00}（保留现金以便循环）");
                            amount = room;
                        }
                    }

                    // 已有未成交卖单（持仓管家的止盈挂单等）：不重复挂卖
                    if (side == OrderSide.Sell &&
                        (await _orders.GetOpenAsync(targetSymbol, ct)).Any(o => o.Side == OrderSide.Sell))
                    {
                        PublishFlow($"📤 {targetSymbol} 已有未成交的卖单在等待成交，本轮不重复挂卖");
                        break;
                    }

                    if (side == OrderSide.Sell && _options.Profit.Enabled &&
                        state.AvgCosts.TryGetValue(RiskEngine.BaseOf(targetSymbol), out var avgCost) && avgCost > 0m)
                    {
                        var heldMin = state.PositionAgeMinutes.TryGetValue(RiskEngine.BaseOf(targetSymbol), out var hm) ? hm : 0m;
                        var minNet = ProfitPolicy.MinNetProfitBps(_options, directive, heldMin);
                        var stopLoss = ProfitPolicy.StopLossPercent(_options, directive);
                        var floor = Math.Ceiling(ProfitPolicy.MinProfitSellPrice(avgCost, _options.FeeBps, minNet) * 1_000_000m) / 1_000_000m;
                        var lossPct = ctx.LastPrice > 0m ? (avgCost - ctx.LastPrice) / avgCost * 100m : 0m;
                        if (price < floor && lossPct < stopLoss)
                        {
                            // 风控价格偏离护栏内可挂出的最高卖价（留 10bps 余量）
                            var ceiling = (ctx.BestAsk > 0m ? ctx.BestAsk : ctx.LastPrice) *
                                          (1m + Math.Max(0, _options.MaxPriceDeviationBps - 10) / 10000m);
                            if (floor <= ceiling)
                            {
                                PublishFlow($"💰 盈利护栏：{targetSymbol} 卖价 {price:0.######} 低于保本价 {floor:0.######}（成本 {avgCost:0.######}），改挂保本价等待成交");
                                price = floor;
                            }
                            else
                            {
                                PublishFlow($"💰 盈利护栏：{targetSymbol} 保本价 {floor:0.######} 远高于盘口 {ctx.BestAsk:0.######}，且浮亏 {Math.Max(0m, lossPct):0.00}% 未触及止损 {stopLoss:0.##}%，继续持有不亏钱卖出");
                                diagnosis += " | 盈利护栏：未达保本价，继续持有";
                                break;
                            }
                        }
                    }

                    var verdict = RiskEngine.ValidateOrder(
                        cfg, _options, ctx, targetSymbol, side, amount, price,
                        state.AvailableUsd, holdingBase, state.EquityUsd, state.CommittedUsd);

                    if (!verdict.Allowed)
                    {
                        vetoed = true;
                        vetoReason = verdict.Reason;
                        await RecordInterceptAsync(targetSymbol, amount, verdict, sessionId, ct);
                        break;
                    }

                    if (side == OrderSide.Buy)
                    {
                        _lastPlacedAtUtc = DateTime.UtcNow; // 订单落库有延迟，内存里先记一笔，避免空转兜底在落库前重复试单
                    }

                    await _executor.Commands.WriteAsync(new ExecCommand(
                        ExecKind.PlaceLimit, targetSymbol, side, price, amount, null,
                        $"AI决策 {actionType} | session={sessionId[..8]}"), ct);
                    break;
                }

                case "CANCEL_ORDER":
                case "CANCEL_ALL_AND_WAIT":
                {
                    var symbol = string.IsNullOrWhiteSpace(exec.TargetSymbol) ? "" : exec.TargetSymbol;
                    await _executor.Commands.WriteAsync(new ExecCommand(
                        ExecKind.CancelAll, symbol, OrderSide.Buy, 0m, 0m, null,
                        $"AI决策 {actionType} | session={sessionId[..8]}"), ct);
                    break;
                }
            }
        }

        // ---------- 思考日志 + 推送 ----------
        await SaveThoughtAsync(sessionId, req, cfg, input, thinking.ToString(), finalJson,
            diagnosis, actionType, exec.Action ?? "HOLD", vetoed, vetoReason, state.EquityUsd, ct);

        _bus.Publish(EngineEventKind.Decision,
            JsonSerializer.Serialize(new UiDecision
            {
                SessionId = sessionId,
                Diagnosis = diagnosis,
                ActionType = actionType,
                Vetoed = vetoed,
                VetoReason = vetoReason,
                TargetSymbol = targetSymbol,
                EquityAfter = state.EquityUsd,
                ThinkingSummary = Summarize(thinking.ToString()),
                ActionProbabilities = decision.ActionProbabilities,
                StopWaitProbabilities = decision.StopWaitProbabilities,
            }, DvcJsonContext.Default.UiDecision));

        _log.LogInfo($"[决策完成] {sessionId[..8]} action={actionType} vetoed={vetoed} equity=${state.EquityUsd:0.00}"
                     + (vetoed ? $" 拦截:{vetoReason}" : $" target={targetSymbol}"));
    }

    /// <summary>应用 AI 输出的目标配置（模式/范围/标的），返回是否有变更。</summary>
    private bool ApplyTargetConfig(AiOutputDecision decision)
    {
        var changed = false;
        var t = decision.TargetConfigStatus;
        var desired = new
        {
            Mode = t.TradingMode switch { "Swing" => TradingMode.Swing, "Scalping" => TradingMode.Scalping, _ => (TradingMode?)null },
            Scope = t.AssetScope switch { "Multi-Pair" => AssetScope.MultiPair, "Single-Pair" => AssetScope.SinglePair, _ => (AssetScope?)null },
            Symbol = string.IsNullOrWhiteSpace(t.ActiveSymbol) ? null : t.ActiveSymbol,
        };

        _runtime.Update(c =>
        {
            if (desired.Mode is { } m && m != c.TradingMode)
            {
                c.TradingMode = m;
                changed = true;
                _log.LogInfo($"AI 切换交易模式 → {m}");
            }

            if (desired.Scope is { } s && s != c.AssetScope)
            {
                c.AssetScope = s;
                changed = true;
                _log.LogInfo($"AI 切换标的选择范围 → {s}");
            }

            if (desired.Symbol is { } sym && !c.FocusLocked && sym != c.ActiveSymbol &&
                (c.AssetScope == AssetScope.SinglePair || c.WatchSymbols.Contains(sym)))
            {
                c.ActiveSymbol = sym;
                changed = true;
                _log.LogInfo($"AI 聚焦标的 → {sym}");
            }
        });

        if (changed)
        {
            var cfg = _runtime.Snapshot();
            _configStore.SaveAsync(cfg).GetAwaiter().GetResult();
            _bus.Publish(EngineEventKind.ModeChanged,
                JsonSerializer.Serialize(new UiConfigTick
                {
                    TradingMode = cfg.TradingMode.ToString(),
                    AssetScope = cfg.AssetScope == AssetScope.MultiPair ? "Multi-Pair" : "Single-Pair",
                    ActiveSymbol = cfg.ActiveSymbol,
                    FocusLocked = cfg.FocusLocked,
                    DataSourceMode = cfg.DataSourceMode.ToString(),
                    AiProvider = cfg.AiProvider.ToString(),
                    RunState = cfg.RunState.ToString(),
                    Theme = cfg.Theme,
                }, DvcJsonContext.Default.UiConfigTick));
        }

        return changed;
    }

    private async Task RecordInterceptAsync(string symbol, decimal requestedAmount, RiskVerdict verdict, string sessionId, CancellationToken ct)
    {
        await _intercepts.InsertAsync(new RiskInterceptRecord
        {
            Kind = verdict.Kind,
            Symbol = symbol,
            RequestedAmount = requestedAmount,
            AllowedAmount = verdict.AllowedAmount,
            Detail = $"{verdict.Reason} [{sessionId[..8]}]",
        }, ct);

        var shield = _runtime.Snapshot().ShieldCount + 1;
        _runtime.Update(c => c.ShieldCount = shield);
        await _configStore.SaveAsync(_runtime.Snapshot(), ct);

        _bus.Publish(EngineEventKind.RiskIntercept,
            JsonSerializer.Serialize(new UiFlowLog
            {
                Message = $"🛡 风控拦截 #{shield}：{verdict.Reason}",
            }, DvcJsonContext.Default.UiFlowLog));
        _account.PublishAccountTickNow(); // 立即刷新盾牌计数到看板
    }

    private async Task<ThoughtRecord> SaveThoughtAsync(
        string sessionId, DecisionRequest req, TradingConfig cfg, AiInputSnapshot input,
        string thinking, string finalJson, string diagnosis, string actionType, string execAction,
        bool vetoed, string vetoReason, decimal equityAfter, CancellationToken ct)
    {
        var record = new ThoughtRecord
        {
            SessionId = sessionId,
            TriggerReason = req.Trigger.ToString(),
            RequestJson = JsonSerializer.Serialize(input, DvcJsonContext.Default.AiInputSnapshot),
            ThinkingText = thinking,
            DecisionJson = finalJson,
            Diagnosis = diagnosis,
            ActionType = actionType,
            RiskVetoed = vetoed,
            RiskReason = vetoReason,
            EquityAfter = equityAfter,
        };
        record.Id = await _thoughts.InsertAsync(record, ct);
        return record;
    }

    private void PublishFlow(string message)
    {
        _bus.Publish(EngineEventKind.FlowLog,
            JsonSerializer.Serialize(new UiFlowLog { Message = message }, DvcJsonContext.Default.UiFlowLog));
        _log.LogInfo(message);
    }

    private static string Summarize(string thinking)
    {
        var lines = thinking.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return lines.Length > 3 ? string.Join('\n', lines[^3..]) : thinking;
    }
}
