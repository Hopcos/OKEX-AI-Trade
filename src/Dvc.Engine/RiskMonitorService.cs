using System.Text.Json;
using Dvc.Core;
using Dvc.Core.Configuration;
using Dvc.Core.Events;
using Dvc.Core.Exchange;
using Dvc.Core.Trading;
using Dvc.Persistence;
using Microsoft.Extensions.Hosting;

namespace Dvc.Engine;

/// <summary>
/// 硬编码风控层巡检（独立于 AI 逻辑之外，最高执行权限）：
/// 每秒计算总权益，一旦 总权益 &lt; 初始本金 × 70% 触发系统级硬熔断 ——
/// 自动清仓所有代币换回 USD 并永久终止 AI 决策进程（RunState=Halted）。
/// </summary>
public sealed class RiskMonitorService : BackgroundService
{
    private readonly RuntimeState _runtime;
    private readonly AccountService _account;
    private readonly IOrderGateway _gateway;
    private readonly OrderExecutionService _executor;
    private readonly OrderRepository _orders;
    private readonly TradingConfigStore _configStore;
    private readonly EngineEventBus _bus;
    private readonly ILogger _log;
    private readonly TradingOptions _options;

    public RiskMonitorService(
        RuntimeState runtime,
        AccountService account,
        IOrderGateway gateway,
        OrderExecutionService executor,
        OrderRepository orders,
        TradingConfigStore configStore,
        EngineEventBus bus,
        ILogger log,
        TradingOptions options)
    {
        _runtime = runtime;
        _account = account;
        _gateway = gateway;
        _executor = executor;
        _orders = orders;
        _configStore = configStore;
        _bus = bus;
        _log = log;
        _options = options;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    if (_runtime.IsHalted)
                    {
                        // 已熔断：仅维持巡检（报告），AI 决策进程已终止
                        continue;
                    }

                    var state = await _account.RefreshAsync(stoppingToken);
                    if (!RiskEngine.IsCircuitTripped(state.InitialCapital, state.EquityUsd, _options.CircuitBreakerRatio))
                    {
                        continue;
                    }

                    await HaltAsync(state, stoppingToken);
                }
                catch (Exception ex)
                {
                    // 巡检自身任何异常都不能中断熔断保护：记日志后下一轮继续
                    _log.LogError("风控巡检异常（自动续跑）: " + ex.Message, ex);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停机
        }
    }

    private async Task HaltAsync(AccountState state, CancellationToken ct)
    {
        _log.LogError($"🚨 系统级硬熔断触发：总权益 ${state.EquityUsd:0.00} < 初始本金 × 70%（${state.InitialCapital * _options.CircuitBreakerRatio:0.00}）");

        // 1) 清仓所有代币换回 USD（风控专属紧急市价路径，与 AI 无关）
        foreach (var (baseCcy, qty) in state.Holdings)
        {
            if (qty <= 0m)
            {
                continue;
            }

            try
            {
                var order = await _gateway.EmergencyMarketAsync(
                    FindInstId(baseCcy) ?? baseCcy + "-USDT", OrderSide.Sell, qty, "CIRCUIT_BREAKER", ct);
                order.Id = await _orders.InsertAsync(order, ct);
                _log.LogWarn($"熔断清仓：{baseCcy} {qty:0.########} → 市价卖出 {order.Status}（{order.Reason}）");
            }
            catch (Exception ex)
            {
                _log.LogError($"熔断清仓失败 {baseCcy}: {ex.Message}", ex);
            }
        }

        // 2) 撤销全部残余挂单
        try
        {
            var cfg = _runtime.Snapshot();
            foreach (var sym in cfg.WatchSymbols)
            {
                foreach (var o in await _gateway.GetOpenOrdersAsync(sym, ct))
                {
                    await _gateway.CancelAsync(o.ClientOrderId, ct);
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogError("熔断撤单失败: " + ex.Message, ex);
        }

        // 2.5) 强制刷新一次余额，让结算快照反映真实清仓后的现金状况
        try
        {
            await _account.RefreshAsync(ct, forceRefresh: true);
        }
        catch (Exception ex)
        {
            _log.LogError("熔断后余额强制刷新失败: " + ex.Message, ex);
        }

        // 3) 状态置 Halted（永久终止 AI 进程）+ 结算记录 + 广播
        _runtime.Update(c => c.RunState = SystemRunState.Halted);
        await _configStore.SaveAsync(_runtime.Snapshot(), ct);
        await _account.SettleAsync("HALT", ct);

        _bus.Publish(EngineEventKind.SystemHalted,
            JsonSerializer.Serialize(new UiHaltEvent
            {
                Reason = $"总权益 ${state.EquityUsd:0.00} < 初始本金 × 70%，自动清仓并永久终止 AI",
                EquityUsd = state.EquityUsd,
                InitialCapital = state.InitialCapital,
            }, DvcJsonContext.Default.UiHaltEvent));
        _bus.Publish(EngineEventKind.FlowLog,
            JsonSerializer.Serialize(new UiFlowLog
            {
                Message = "🚨 系统级硬熔断：已清仓换回 USD，AI 决策进程永久终止",
            }, DvcJsonContext.Default.UiFlowLog));

        _log.LogError("硬熔断完成：清仓完毕，AI 决策进程永久终止（RunState=Halted）");
    }

    private string? FindInstId(string baseCcy)
    {
        var cfg = _runtime.Snapshot();
        foreach (var w in cfg.WatchSymbols)
        {
            if (RiskEngine.BaseOf(w).Equals(baseCcy, StringComparison.OrdinalIgnoreCase))
            {
                return w;
            }
        }

        return null;
    }
}
