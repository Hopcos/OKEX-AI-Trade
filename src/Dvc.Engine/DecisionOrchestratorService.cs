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

        var candidates = cfg.AssetScope == AssetScope.MultiPair
            ? _market.BuildAll(cfg.WatchSymbols)
            : null;

        var input = AiInputBuilder.Build(req.Trigger, cfg, state, activeContext, candidates, req.LastLoss);
        var request = new AiRequest(SystemPromptFactory.Build(), input);
        // 强制开关：Trading:ForceInternDecision=true → 每轮决策强制 InternDecision（无视看板/DB 热切换）
        var provider = _aiFactory(_options.ForceInternDecision ? AiProvider.InternDecision : cfg.AiProvider);
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
                    var ctx = targetSymbol == cfg.ActiveSymbol ? activeContext : _market.Build(targetSymbol);
                    var explicitPrice = exec.Price;
                    var price = explicitPrice is > 0
                        ? explicitPrice.Value
                        : side == OrderSide.Buy ? ctx.BestBid : ctx.BestAsk;
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

                    var verdict = RiskEngine.ValidateOrder(
                        cfg, _options, ctx, targetSymbol, side, amount, price,
                        state.AvailableUsd, holdingBase, state.EquityUsd);

                    if (!verdict.Allowed)
                    {
                        vetoed = true;
                        vetoReason = verdict.Reason;
                        await RecordInterceptAsync(targetSymbol, amount, verdict, sessionId, ct);
                        break;
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
