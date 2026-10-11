using System.Text.Json;
using Dvc.Core;
using Dvc.Core.Events;
using Dvc.Core.Trading;
using Dvc.Persistence;

namespace Dvc.Engine;

/// <summary>
/// 交易控制服务（Web 看板 → 引擎）：开关切换原子落库 + 推送 + 触发决策重评估
/// （切换即唤醒 AI 评估新配置下的第一步动作）。
/// </summary>
public sealed class TradingControlService
{
    private readonly RuntimeState _runtime;
    private readonly TradingConfigStore _configStore;
    private readonly EngineEventBus _bus;
    private readonly ILogger _log;

    public TradingControlService(RuntimeState runtime, TradingConfigStore configStore, EngineEventBus bus, ILogger log)
    {
        _runtime = runtime;
        _configStore = configStore;
        _bus = bus;
        _log = log;
    }

    /// <summary>当前观察池（看板模式面板下拉）。</summary>
    public IReadOnlyList<string> WatchSymbols => [.. _runtime.Snapshot().WatchSymbols];

    public async Task SetTradingModeAsync(TradingMode mode, CancellationToken ct = default)
    {
        _runtime.Update(c => c.TradingMode = mode);
        await PersistAndNotifyAsync("trading_mode", ct);
        _runtime.WakeHigh(DecisionTrigger.StrategyTick, "MANUAL_MODE_SWITCH");
    }

    public async Task SetAssetScopeAsync(AssetScope scope, CancellationToken ct = default)
    {
        _runtime.Update(c => c.AssetScope = scope);
        await PersistAndNotifyAsync("asset_scope", ct);
        _runtime.WakeHigh(DecisionTrigger.StrategyTick, "MANUAL_SCOPE_SWITCH");
    }

    public async Task SetActiveSymbolAsync(string symbol, CancellationToken ct = default)
    {
        _runtime.Update(c =>
        {
            if (string.IsNullOrWhiteSpace(symbol))
            {
                // "AI 自动聚焦"：解锁，聚焦标的交给 AI 按期望值切换（本次不改变标的）
                c.FocusLocked = false;
                return;
            }

            if (!c.WatchSymbols.Contains(symbol) && c.WatchSymbols.Count > 0)
            {
                symbol = c.WatchSymbols[0];
            }

            c.ActiveSymbol = symbol;
            c.FocusLocked = true; // 手动聚焦：AI 不得覆盖，直到用户切回自动
        });
        await PersistAndNotifyAsync("active_symbol", ct);
        _runtime.WakeHigh(DecisionTrigger.StrategyTick, "MANUAL_SYMBOL_SWITCH");
    }

    public async Task SetThemeAsync(string theme, CancellationToken ct = default)
    {
        _runtime.Update(c => c.Theme = theme);
        await PersistAndNotifyAsync("theme", ct);
    }

    private async Task PersistAndNotifyAsync(string changed, CancellationToken ct)
    {
        var cfg = _runtime.Snapshot();
        await _configStore.SaveAsync(cfg, ct);
        PublishTick(cfg);
        _log.LogInfo($"配置变更[{changed}]：模式={cfg.TradingMode} 范围={cfg.AssetScope} 标的={cfg.ActiveSymbol}");
    }

    /// <summary>广播当前配置快照（无需落库）。供启动时推送，确保看板 Config 永不为 null（否则盘口等模块显示"等待行情"）。</summary>
    public void BroadcastCurrentConfig()
    {
        PublishTick(_runtime.Snapshot());
    }

    private void PublishTick(TradingConfig cfg)
    {
        var tick = new UiConfigTick
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
        _bus.Publish(EngineEventKind.ModeChanged, JsonSerializer.Serialize(tick, DvcJsonContext.Default.UiConfigTick));
    }
}
