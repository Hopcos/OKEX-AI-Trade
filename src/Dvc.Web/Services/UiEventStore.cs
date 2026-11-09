using System.Text.Json;
using Dvc.Core;
using Dvc.Core.Ai;
using Dvc.Core.Configuration;
using Dvc.Core.Events;
using Dvc.Core.Trading;
using Dvc.Engine;
using Dvc.Persistence;

namespace Dvc.Web.Services;

/// <summary>
/// 看板状态仓库（Blazor Server 组件的内存订阅源）：
/// 单读循环消费引擎事件总线 → 维护最新快照/历史序列 → 按事件种类回调组件。
/// 组件只订阅本仓库事件，避免多个组件竞争读取同一 Channel。
/// </summary>
public sealed class UiEventStore : IDisposable
{
    public sealed record AccountPoint(DateTime T, decimal Equity, decimal MaxOrderSize, decimal Available);

    private readonly System.Threading.Channels.Channel<EngineEvent> _channel;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private readonly object _lock = new();

    private readonly Dictionary<string, UiMarketTick> _markets = [];
    private UiAccountTick? _account;
    private UiConfigTick? _config;
    private readonly List<UiThoughtChunk> _thoughtBuffer = [];
    private readonly List<UiDecision> _decisions = [];
    private readonly List<UiOrderEvent> _orders = [];
    private readonly List<UiFlowLog> _flows = [];
    private readonly List<UiHaltEvent> _halts = [];
    private readonly List<AccountPoint> _accountHistory = [];

    public event Action? Changed;
    public event Action<UiThoughtChunk>? ThoughtChunk;
    public event Action<UiOrderEvent>? OrderArrived;
    public event Action<UiHaltEvent>? Halted;

    public UiEventStore(
        EngineEventBus bus,
        RuntimeState runtime,
        OrderRepository? orderRepo = null,
        ThoughtRepository? thoughtRepo = null,
        SettlementRepository? settlementRepo = null,
        RiskInterceptRepository? interceptRepo = null,
        TradingOptions? options = null)
    {
        // 构造时直接用运行态快照初始化 Config：单例是延迟创建的（首个电路才建），
        // 启动时的 ModeChanged 广播可能因通道尚未创建而丢失，导致 Ui.Store.Config 一直为 null，
        // 进而"盘口微观结构"等依赖 Config.ActiveSymbol 的模块永远显示"等待行情数据"。
        _config = ToTick(runtime.Snapshot());
        HydrateFromDb(orderRepo, thoughtRepo, settlementRepo, interceptRepo, options?.LedgerStartUtc);
        _channel = bus.CreateChannel();
        _loop = Task.Run(LoopAsync);
    }

    private void HydrateFromDb(
        OrderRepository? orderRepo,
        ThoughtRepository? thoughtRepo,
        SettlementRepository? settlementRepo,
        RiskInterceptRepository? interceptRepo,
        DateTime? sinceUtc)
    {
        if (orderRepo is null && thoughtRepo is null && settlementRepo is null && interceptRepo is null)
        {
            return;
        }

        try
        {
            // 1. 订单流水（Trade_Order，过滤 >= LedgerStartUtc）
            if (orderRepo is not null)
            {
                var orders = orderRepo.ListSinceAsync(sinceUtc, limit: 80).GetAwaiter().GetResult();
                foreach (var o in orders)
                {
                    var tag = o.Status switch
                    {
                        OrderStatus.Filled => "成交",
                        OrderStatus.Cancelled => "撤单",
                        OrderStatus.Rejected => "被拒",
                        OrderStatus.Pending => "挂单",
                        OrderStatus.Queued => "排队",
                        _ => "",
                    };

                    _orders.Add(new UiOrderEvent
                    {
                        ClientOrderId = o.ClientOrderId,
                        Symbol = o.Symbol,
                        Side = o.Side.ToString(),
                        Price = o.FilledAvgPrice > 0 ? o.FilledAvgPrice : o.Price,
                        AmountUsd = o.AmountUsd,
                        Status = o.Status.ToString(),
                        RealizedPnl = o.RealizedPnl,
                        Tag = tag,
                        Ts = o.UpdatedAtUtc ?? o.CreatedAtUtc,
                    });
                }
            }

            // 2. 决策记录（Agent_Thought_Log）与思考链缓冲（过滤 >= LedgerStartUtc）
            IReadOnlyList<ThoughtRecord> thoughts = [];
            if (thoughtRepo is not null)
            {
                thoughts = thoughtRepo.ListSinceAsync(sinceUtc, limit: 40).GetAwaiter().GetResult();
                foreach (var t in thoughts)
                {
                    AiOutputDecision? aiDecision = null;
                    if (!string.IsNullOrWhiteSpace(t.DecisionJson))
                    {
                        try
                        {
                            aiDecision = JsonSerializer.Deserialize(t.DecisionJson, DvcJsonContext.Default.AiOutputDecision);
                        }
                        catch
                        {
                            // 忽略脏数据
                        }
                    }

                    var targetSymbol = aiDecision?.ImmediateExecutionOrder.TargetSymbol;
                    if (string.IsNullOrEmpty(targetSymbol))
                    {
                        targetSymbol = aiDecision?.TargetConfigStatus.ActiveSymbol ?? "";
                    }

                    _decisions.Add(new UiDecision
                    {
                        SessionId = t.SessionId,
                        Diagnosis = !string.IsNullOrEmpty(t.Diagnosis) ? t.Diagnosis : (aiDecision?.DecisionLogic.Diagnosis ?? ""),
                        ActionType = !string.IsNullOrEmpty(t.ActionType) ? t.ActionType : (aiDecision?.DecisionLogic.ActionType ?? "HOLD"),
                        Vetoed = t.RiskVetoed,
                        VetoReason = t.RiskReason,
                        TargetSymbol = targetSymbol,
                        EquityAfter = t.EquityAfter,
                        ThinkingSummary = Summarize(t.ThinkingText),
                        ActionProbabilities = aiDecision?.ActionProbabilities,
                        StopWaitProbabilities = aiDecision?.StopWaitProbabilities,
                        Ts = t.CreatedAtUtc,
                    });
                }

                // 思考链回显：取最近 3 条思考记录注入缓冲（按时间正序回放）
                foreach (var t in thoughts.Take(3).Reverse())
                {
                    if (!string.IsNullOrWhiteSpace(t.ThinkingText))
                    {
                        _thoughtBuffer.Add(new UiThoughtChunk
                        {
                            SessionId = t.SessionId,
                            Text = t.ThinkingText,
                            Trigger = t.TriggerReason,
                        });
                    }
                }
            }

            // 3. 账户结算历史（Asset_Snapshot）与复利阶梯图（过滤 >= LedgerStartUtc）
            IReadOnlyList<AccountSettlement> settlements = [];
            if (settlementRepo is not null)
            {
                settlements = settlementRepo.ListSinceAsync(sinceUtc, limit: 240).GetAwaiter().GetResult();
                // 曲线图要求按时间升序（从旧到新）
                foreach (var s in settlements.Reverse())
                {
                    _accountHistory.Add(new AccountPoint(s.SnapshotAtUtc, s.EquityUsd, s.MaxOrderSize, s.AvailableUsd));
                }

                if (_account is null && settlements.Count > 0)
                {
                    var latest = settlements[0];
                    Dictionary<string, decimal> holdings = [];
                    try
                    {
                        holdings = JsonSerializer.Deserialize(latest.HoldingsJson, DvcJsonContext.Default.DictionaryStringDecimal) ?? [];
                    }
                    catch
                    {
                        // 忽略
                    }

                    _account = new UiAccountTick
                    {
                        InitialCapital = latest.InitialCapital,
                        AvailableUsd = latest.AvailableUsd,
                        EquityUsd = latest.EquityUsd,
                        AccumulatedProfit = Math.Round(latest.EquityUsd - latest.InitialCapital, 2),
                        RealizedPnl = Math.Round(latest.RealizedPnl, 2),
                        UnrealizedPnl = Math.Round(latest.EquityUsd - latest.InitialCapital - latest.RealizedPnl, 2),
                        MaxOrderSize = latest.MaxOrderSize,
                        Holdings = holdings,
                        RunState = _config?.RunState ?? "Running",
                    };
                }
            }

            // 4. 风控拦截与中枢流水（FlowLogs & RiskShield，过滤 >= LedgerStartUtc）
            var flowItems = new List<(DateTime Ts, string Msg)>();

            if (interceptRepo is not null)
            {
                var intercepts = interceptRepo.ListSinceAsync(sinceUtc, limit: 20).GetAwaiter().GetResult();
                foreach (var r in intercepts)
                {
                    flowItems.Add((r.CreatedAtUtc, $"🛡 风控拦截 [{r.Kind}]：{r.Detail} ({r.Symbol} ${r.RequestedAmount:0.00})"));
                }
            }

            foreach (var t in thoughts)
            {
                var shortId = t.SessionId.Length > 8 ? t.SessionId[..8] : t.SessionId;
                if (t.RiskVetoed)
                {
                    flowItems.Add((t.CreatedAtUtc, $"🛡 决策否决 [{shortId}]：{t.RiskReason} (动作 {t.ActionType})"));
                }
                else if (!string.IsNullOrEmpty(t.Diagnosis))
                {
                    flowItems.Add((t.CreatedAtUtc, $"[决策] [{shortId}] {t.ActionType} · {t.Diagnosis}"));
                }
            }

            foreach (var o in _orders)
            {
                flowItems.Add((o.Ts, $"[订单] {o.Symbol} {o.Side} ${o.AmountUsd:0.00} 状态={o.Status}"));
            }

            // 按时间倒序（最新在前）加入 _flows
            foreach (var item in flowItems.OrderByDescending(f => f.Ts).Take(100))
            {
                _flows.Add(new UiFlowLog { Message = item.Msg });
            }
        }
        catch (Exception ex)
        {
            // 预加载容错降级，不阻碍主服务启动
            Console.WriteLine($"[UiEventStore] 启动预加载历史数据异常: {ex.Message}");
        }
    }

    private static string Summarize(string thinking)
    {
        if (string.IsNullOrWhiteSpace(thinking))
        {
            return "";
        }

        var lines = thinking.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return lines.Length > 3 ? string.Join('\n', lines[^3..]) : thinking;
    }

    private static UiConfigTick ToTick(TradingConfig cfg) => new()
    {
        TradingMode = cfg.TradingMode.ToString(),
        AssetScope = cfg.AssetScope == AssetScope.MultiPair ? "Multi-Pair" : "Single-Pair",
        ActiveSymbol = cfg.ActiveSymbol,
        FocusLocked = cfg.FocusLocked,
        DataSourceMode = cfg.DataSourceMode.ToString(),
        AiProvider = cfg.AiProvider.ToString(),
        RunState = cfg.RunState.ToString(),
        Theme = cfg.Theme,
    };

    public UiAccountTick? Account
    {
        get
        {
            lock (_lock)
            {
                return _account;
            }
        }
    }

    public UiConfigTick? Config
    {
        get
        {
            lock (_lock)
            {
                return _config;
            }
        }
    }

    public IReadOnlyList<UiDecision> Decisions => Copy(_decisions);
    public IReadOnlyList<UiOrderEvent> Orders => Copy(_orders);
    public IReadOnlyList<UiFlowLog> Flows => Copy(_flows);
    public IReadOnlyList<UiHaltEvent> Halts => Copy(_halts);
    public IReadOnlyList<AccountPoint> AccountHistory => Copy(_accountHistory);

    public UiMarketTick? GetMarket(string symbol)
    {
        lock (_lock)
        {
            return _markets.TryGetValue(symbol, out var m) ? m : null;
        }
    }

    public IReadOnlyList<UiMarketTick> AllMarkets()
    {
        lock (_lock)
        {
            return _markets.Values.OrderBy(m => m.Symbol).ToList();
        }
    }

    public IReadOnlyList<UiThoughtChunk> AllThoughtChunks()
    {
        lock (_lock)
        {
            return [.. _thoughtBuffer];
        }
    }

    public string? LastThinking(string sessionId)
    {
        lock (_lock)
        {
            var session = _thoughtBuffer.Where(t => t.SessionId == sessionId).ToList();
            return session.Count == 0 ? null : string.Concat(session.Select(t => t.Text));
        }
    }

    private async Task LoopAsync()
    {
        try
        {
            await foreach (var ev in _channel.Reader.ReadAllAsync(_cts.Token))
            {
                Handle(ev);
            }
        }
        catch (OperationCanceledException)
        {
            // 停机
        }
    }

    private void Handle(EngineEvent ev)
    {
        switch (ev.Kind)
        {
            case EngineEventKind.MarketUpdate:
                var market = JsonSerializer.Deserialize(ev.PayloadJson, DvcJsonContext.Default.UiMarketTick);
                if (market is not null)
                {
                    lock (_lock)
                    {
                        _markets[market.Symbol] = market;
                    }
                }

                FireChanged();
                break;

            case EngineEventKind.AccountSnapshot:
                var account = JsonSerializer.Deserialize(ev.PayloadJson, DvcJsonContext.Default.UiAccountTick);
                if (account is not null)
                {
                    lock (_lock)
                    {
                        _account = account;
                        _accountHistory.Add(new AccountPoint(ev.TimestampUtc, account.EquityUsd, account.MaxOrderSize, account.AvailableUsd));
                        if (_accountHistory.Count > 240)
                        {
                            _accountHistory.RemoveAt(0);
                        }
                    }
                }

                FireChanged();
                break;

            case EngineEventKind.ModeChanged:
                var config = JsonSerializer.Deserialize(ev.PayloadJson, DvcJsonContext.Default.UiConfigTick);
                if (config is not null)
                {
                    lock (_lock)
                    {
                        _config = config;
                    }
                }

                FireChanged();
                break;

            case EngineEventKind.ThoughtChunk:
                var chunk = JsonSerializer.Deserialize(ev.PayloadJson, DvcJsonContext.Default.UiThoughtChunk);
                if (chunk is not null)
                {
                    lock (_lock)
                    {
                        _thoughtBuffer.Add(chunk);
                        if (_thoughtBuffer.Count > 4000)
                        {
                            _thoughtBuffer.RemoveRange(0, 500);
                        }
                    }

                    // 分片仅推送给思考流组件（增量追加），不触发全局 Changed，避免逐 token 全站重绘
                    ThoughtChunk?.Invoke(chunk);
                }

                break;

            case EngineEventKind.Decision:
                var decision = JsonSerializer.Deserialize(ev.PayloadJson, DvcJsonContext.Default.UiDecision);
                if (decision is not null)
                {
                    lock (_lock)
                    {
                        _decisions.Insert(0, decision);
                        if (_decisions.Count > 60)
                        {
                            _decisions.RemoveAt(_decisions.Count - 1);
                        }
                    }

                    FireChanged();
                }

                break;

            case EngineEventKind.OrderEvent:
                var order = JsonSerializer.Deserialize(ev.PayloadJson, DvcJsonContext.Default.UiOrderEvent);
                if (order is not null)
                {
                    lock (_lock)
                    {
                        _orders.Insert(0, order);
                        if (_orders.Count > 80)
                        {
                            _orders.RemoveAt(_orders.Count - 1);
                        }
                    }

                    OrderArrived?.Invoke(order);
                    FireChanged();
                }

                break;

            case EngineEventKind.RiskIntercept:
            case EngineEventKind.FlowLog:
                var flow = JsonSerializer.Deserialize(ev.PayloadJson, DvcJsonContext.Default.UiFlowLog);
                if (flow is not null)
                {
                    lock (_lock)
                    {
                        _flows.Insert(0, flow);
                        if (_flows.Count > 120)
                        {
                            _flows.RemoveAt(_flows.Count - 1);
                        }
                    }

                    FireChanged();
                }

                break;

            case EngineEventKind.SystemHalted:
                var halt = JsonSerializer.Deserialize(ev.PayloadJson, DvcJsonContext.Default.UiHaltEvent);
                if (halt is not null)
                {
                    lock (_lock)
                    {
                        _halts.Insert(0, halt);
                    }

                    Halted?.Invoke(halt);
                    FireChanged();
                }

                break;
        }
    }

    // 通知节流：行情（每秒多币种）与思考分片（逐 token）会产生极高频事件。
    // 若每个事件都同步触发 Changed，所有订阅组件都会各自 StateHasChanged，导致整页卡顿、
    // Tab 切换与主题切换响应变慢。这里把同一时间窗口内的变更合并为一次通知（脏标记 + 节流）。
    private int _notifyPending;
    private long _lastNotifyTicks;
    private static readonly long NotifyIntervalTicks = TimeSpan.FromMilliseconds(250).Ticks;

    private void FireChanged()
    {
        // 已有待发送的通知则合并，不再重复排队
        if (Interlocked.CompareExchange(ref _notifyPending, 1, 0) != 0)
        {
            return;
        }

        var elapsed = DateTime.UtcNow.Ticks - Interlocked.Read(ref _lastNotifyTicks);
        if (elapsed >= NotifyIntervalTicks)
        {
            RaiseChangedNow();
        }
        else
        {
            var delayMs = (int)((NotifyIntervalTicks - elapsed) / TimeSpan.TicksPerMillisecond);
            _ = Task.Delay(Math.Max(1, delayMs)).ContinueWith(_ => RaiseChangedNow(), TaskScheduler.Default);
        }
    }

    private void RaiseChangedNow()
    {
        Interlocked.Exchange(ref _notifyPending, 0);
        Interlocked.Exchange(ref _lastNotifyTicks, DateTime.UtcNow.Ticks);
        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[UiEventStore] 通知订阅者异常: {ex.Message}");
        }
    }

    private List<T> Copy<T>(List<T> source)
    {
        // 必须与写入方（Handle 中的 Add/RemoveAt）共用同一把 _lock：
        // 若 lock 列表对象本身则与写锁不是同一把锁，List.CopyTo 会在并发扩容/缩容时
        // 抛出 "Destination array was not long enough"（已由线上堆栈证实）。
        lock (_lock)
        {
            return [.. source];
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _loop.Wait(2000);
        }
        catch
        {
            // 忽略
        }
    }
}
