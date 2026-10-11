using System.Threading.Channels;
using Dvc.Core;
using Dvc.Core.Trading;

namespace Dvc.Engine;

/// <summary>一次决策唤醒请求。</summary>
public sealed record DecisionRequest(DecisionTrigger Trigger, string Detail = "", decimal LastLoss = 0m);

/// <summary>
/// 运行时状态（内存态）：当前策略开关快照 + 高/低优先级决策唤醒通道。
/// 亏损事件（LOSS_EVENT_WAKEUP）走高优先级通道，无视定时器立即硬唤醒 ——
/// 优先级通道保证"物理触发"语义。
/// </summary>
public sealed class RuntimeState
{
    private readonly object _lock = new();
    private TradingConfig _config;

    private readonly Channel<DecisionRequest> _highPriority = Channel.CreateUnbounded<DecisionRequest>();
    private readonly Channel<DecisionRequest> _lowPriority = Channel.CreateUnbounded<DecisionRequest>();

    public RuntimeState(TradingConfig config)
    {
        _config = config;
    }

    public TradingConfig Config => Snapshot();

    public TradingConfig Snapshot()
    {
        lock (_lock)
        {
            return Clone(_config);
        }
    }

    /// <summary>原子更新配置（每次开关切换后落库 + 推送 ModeChanged）。</summary>
    public void Update(Action<TradingConfig> mutate)
    {
        lock (_lock)
        {
            mutate(_config);
        }
    }

    public bool IsHalted => Snapshot().RunState == SystemRunState.Halted;

    /// <summary>高优先级唤醒（亏损 / 宏观 / 人工控制）。</summary>
    public void WakeHigh(DecisionTrigger trigger, string detail = "", decimal lastLoss = 0m) =>
        _highPriority.Writer.TryWrite(new DecisionRequest(trigger, detail, lastLoss));

    /// <summary>低优先级唤醒（策略节拍 / K 线收盘）。</summary>
    public void WakeLow(DecisionTrigger trigger, string detail = "") =>
        _lowPriority.Writer.TryWrite(new DecisionRequest(trigger, detail));

    public ChannelReader<DecisionRequest> HighPriority => _highPriority.Reader;

    public ChannelReader<DecisionRequest> LowPriority => _lowPriority.Reader;

    private static TradingConfig Clone(TradingConfig c) => new()
    {
        RunState = c.RunState,
        TradingMode = c.TradingMode,
        AssetScope = c.AssetScope,
        ActiveSymbol = c.ActiveSymbol,
        FocusLocked = c.FocusLocked,
        DataSourceMode = c.DataSourceMode,
        AiProvider = c.AiProvider,
        InitialCapital = c.InitialCapital,
        LastSettlementEquity = c.LastSettlementEquity,
        ShieldCount = c.ShieldCount,
        Blacklist = [.. c.Blacklist],
        WatchSymbols = [.. c.WatchSymbols],
        Theme = c.Theme,
    };
}
