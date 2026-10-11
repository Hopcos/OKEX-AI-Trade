using System.Text.Json;
using Dvc.Core;
using Dvc.Core.Events;
using Dvc.Core.Trading;
using Dvc.Engine;

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

    public UiEventStore(EngineEventBus bus, RuntimeState runtime)
    {
        // 构造时直接用运行态快照初始化 Config：单例是延迟创建的（首个电路才建），
        // 启动时的 ModeChanged 广播可能因通道尚未创建而丢失，导致 Ui.Store.Config 一直为 null，
        // 进而"盘口微观结构"等依赖 Config.ActiveSymbol 的模块永远显示"等待行情数据"。
        _config = ToTick(runtime.Snapshot());
        _channel = bus.CreateChannel();
        _loop = Task.Run(LoopAsync);
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

                    ThoughtChunk?.Invoke(chunk);
                    FireChanged();
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

    private void FireChanged() => Changed?.Invoke();

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
