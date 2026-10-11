using System.Text.Json;
using System.Threading.Channels;
using Dvc.Core;
using Dvc.Core.Events;
using Dvc.Core.Exchange;
using Dvc.Core.Trading;
using Dvc.Persistence;
using Microsoft.Extensions.Hosting;

namespace Dvc.Engine;

/// <summary>执行命令（决策层风控通过后下发）。</summary>
public sealed record ExecCommand(
    ExecKind Kind,
    string Symbol,
    OrderSide Side,
    decimal Price,
    decimal AmountUsd,
    string? ClientOrderIdRef,
    string Reason);

public enum ExecKind
{
    PlaceLimit,
    Cancel,
    CancelAll,
    EmergencyMarket,
}

/// <summary>
/// 管道三：订单执行（Order Execution）。
/// 消费决策层下发的已过风控指令 → 网关 LIMIT/CANCEL（物理隔离 Rate Limit）；
/// 2 秒轮询补单对账（Live）+ 订阅 Paper 成交推送；成交后触发亏损唤醒与账户刷新；
/// 挂单 TTL 超时自动撤销并回唤醒 AI 重新评估。
/// </summary>
public sealed class OrderExecutionService : BackgroundService
{
    private readonly IOrderGateway _gateway;
    private readonly OrderRepository _orders;
    private readonly AccountService _account;
    private readonly RuntimeState _runtime;
    private readonly EngineEventBus _bus;
    private readonly ILogger _log;
    private readonly Dvc.Core.Configuration.TradingOptions _options;

    private readonly Channel<ExecCommand> _commands = Channel.CreateUnbounded<ExecCommand>();
    private readonly Dictionary<string, OrderRecord> _tracked = [];

    public ChannelWriter<ExecCommand> Commands => _commands.Writer;

    public OrderExecutionService(
        IOrderGateway gateway,
        OrderRepository orders,
        AccountService account,
        RuntimeState runtime,
        EngineEventBus bus,
        ILogger log,
        Dvc.Core.Configuration.TradingOptions options)
    {
        _gateway = gateway;
        _orders = orders;
        _account = account;
        _runtime = runtime;
        _bus = bus;
        _log = log;
        _options = options;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _gateway.OrderFilled += OnGatewayFill;

        var reconcileTimer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        var commandTask = CommandLoopAsync(stoppingToken);
        var reconcileTask = ReconcileLoopAsync(reconcileTimer, stoppingToken);
        try
        {
            await Task.WhenAll(commandTask, reconcileTask);
        }
        catch (OperationCanceledException)
        {
            // 正常停机
        }
    }

    // ================= 命令执行 =================

    private async Task CommandLoopAsync(CancellationToken ct)
    {
        await foreach (var cmd in _commands.Reader.ReadAllAsync(ct))
        {
            try
            {
                await ExecuteCommandAsync(cmd, ct);
            }
            catch (Exception ex)
            {
                _log.LogError($"执行命令失败 {cmd.Kind} {cmd.Symbol}: {ex.Message}", ex);
                PublishFlow($"执行失败：{cmd.Kind} {cmd.Symbol} → {ex.Message}");
            }
        }
    }

    private async Task ExecuteCommandAsync(ExecCommand cmd, CancellationToken ct)
    {
        switch (cmd.Kind)
        {
            case ExecKind.PlaceLimit:
            {
                // 熔断二道闸：AI 主决策循环已永久退出，此处兜底拦截竞态窗口内的残余下单命令
                if (_runtime.IsHalted)
                {
                    PublishFlow($"拒绝下单：系统已硬熔断（{cmd.Symbol} {cmd.Side} ${cmd.AmountUsd:0.00}）");
                    break;
                }

                // 下单前熔断底线复核（独立于每秒巡检；fail-closed：校验失败一律拒绝）
                try
                {
                    var acct = await _account.RefreshAsync(ct);
                    if (RiskEngine.IsCircuitTripped(acct.InitialCapital, acct.EquityUsd, _options.CircuitBreakerRatio))
                    {
                        var floor = acct.InitialCapital * _options.CircuitBreakerRatio;
                        PublishFlow($"拒绝下单：总权益 ${acct.EquityUsd:0.00} 低于熔断底线 ${floor:0.00}（{cmd.Symbol} {cmd.Side} ${cmd.AmountUsd:0.00}）");
                        break;
                    }
                }
                catch (Exception ex)
                {
                    PublishFlow($"拒绝下单：下单前熔断校验异常，安全拒绝（{ex.Message}）");
                    break;
                }

                // 卖出保护：额度收敛到实际持仓市值，避免浮点取整导致的"持仓不足"拒绝
                var amountUsd = cmd.AmountUsd;
                if (cmd.Side == OrderSide.Sell && amountUsd > 0m)
                {
                    var balances = await _gateway.GetBalancesAsync(ct);
                    if (balances.Holdings.TryGetValue(RiskEngine.BaseOf(cmd.Symbol), out var held) && held > 0m)
                    {
                        var cap = held * cmd.Price;
                        if (amountUsd > cap)
                        {
                            amountUsd = Math.Max(0m, Math.Round(cap * 0.9999m, 8, MidpointRounding.ToZero));
                        }
                    }
                }

                var order = await _gateway.PlaceLimitAsync(cmd.Symbol, cmd.Side, cmd.Price, amountUsd, cmd.Reason, ct);
                order.Id = await _orders.InsertAsync(order, ct);
                if (order.Status == OrderStatus.Pending)
                {
                    lock (_tracked)
                    {
                        _tracked[order.ClientOrderId] = order;
                    }
                }

                PublishOrder(order, "挂单");
                _log.LogInfo($"[{cmd.Side}] {cmd.Symbol} 限价 {cmd.Price} x ${amountUsd:0.00} → {order.Status} ({order.Reason})");
                break;
            }

            case ExecKind.Cancel:
            {
                if (cmd.ClientOrderIdRef is null)
                {
                    break;
                }

                var ok = await _gateway.CancelAsync(cmd.ClientOrderIdRef, ct);
                lock (_tracked)
                {
                    _tracked.Remove(cmd.ClientOrderIdRef);
                }

                await UpdateDbStatusAsync(cmd.ClientOrderIdRef, OrderStatus.Cancelled, ct);
                PublishFlow(ok
                    ? $"撤单成功：{cmd.ClientOrderIdRef}"
                    : $"撤单失败（可能已成交）：{cmd.ClientOrderIdRef}");
                break;
            }

            case ExecKind.CancelAll:
            {
                var open = await _gateway.GetOpenOrdersAsync(cmd.Symbol, ct);
                foreach (var o in open)
                {
                    var ok = await _gateway.CancelAsync(o.ClientOrderId, ct);
                    lock (_tracked)
                    {
                        _tracked.Remove(o.ClientOrderId);
                    }

                    await UpdateDbStatusAsync(o.ClientOrderId, OrderStatus.Cancelled, ct);
                }

                PublishFlow(open.Count == 0
                    ? $"盘口无残余挂单（{cmd.Symbol}）"
                    : $"已撤销 {cmd.Symbol} 全部 {open.Count} 笔残余挂单，锁定 USD");
                break;
            }

            case ExecKind.EmergencyMarket:
            {
                var order = await _gateway.EmergencyMarketAsync(cmd.Symbol, cmd.Side, cmd.AmountUsd, cmd.Reason, ct);
                order.Id = await _orders.InsertAsync(order, ct);
                await _account.RefreshAsync(ct);
                PublishOrder(order, "紧急市价");
                break;
            }
        }
    }

    // ================= 成交对账 =================

    /// <summary>熔断后残余挂单清扫：撤掉所有仍处于 Pending 的跟踪单（撤失败的每 2s 重试）。</summary>
    private async Task SweepPendingOnHaltAsync(CancellationToken ct)
    {
        List<OrderRecord> pending;
        lock (_tracked)
        {
            pending = _tracked.Values.Where(o => o.Status == OrderStatus.Pending).ToList();
        }

        foreach (var p in pending)
        {
            try
            {
                lock (_tracked)
                {
                    // 先摘除跟踪，避免重复处理；撤单失败下次轮询仍会重新加入（由网关 open-orders 兜底）
                    _tracked.Remove(p.ClientOrderId);
                }

                if (await _gateway.CancelAsync(p.ClientOrderId, ct))
                {
                    await UpdateDbStatusAsync(p.ClientOrderId, OrderStatus.Cancelled, ct);
                    _log.LogWarn($"熔断后残余挂单已撤：{p.ClientOrderId}");
                }
            }
            catch (Exception ex)
            {
                _log.LogError($"熔断后撤单失败 {p.ClientOrderId}: {ex.Message}");
            }
        }
    }

    private void OnGatewayFill(OrderRecord filled)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await ReconcileFillAsync(filled);
            }
            catch (Exception ex)
            {
                _log.LogError("Paper 成交对账异常: " + ex.Message, ex);
            }
        });
    }

    private async Task ReconcileLoopAsync(PeriodicTimer timer, CancellationToken ct)
    {
        while (await timer.WaitForNextTickAsync(ct))
        {
            if (_runtime.IsHalted)
            {
                // 熔断后不再对账/唤醒，但仍持续清扫残余挂单（防熔断瞬间撤单失败的漏网单成交）
                await SweepPendingOnHaltAsync(ct);
                continue;
            }

            List<OrderRecord> pending;
            lock (_tracked)
            {
                pending = _tracked.Values.ToList();
            }

            foreach (var p in pending)
            {
                try
                {
                    // TTL 超时：自动撤销并回唤醒 AI 重新评估
                    var ttl = TimeSpan.FromSeconds(Math.Max(15, _options.OrderTtlSeconds));
                    if (DateTime.UtcNow - p.CreatedAtUtc > ttl && p.Status == OrderStatus.Pending)
                    {
                        await _gateway.CancelAsync(p.ClientOrderId, ct);
                        lock (_tracked)
                        {
                            _tracked.Remove(p.ClientOrderId);
                        }

                        await UpdateDbStatusAsync(p.ClientOrderId, OrderStatus.Cancelled, ct);
                        PublishFlow($"挂单超时自动撤销：{p.ClientOrderId}（TTL {ttl.TotalSeconds:0}s）");
                        _runtime.WakeLow(DecisionTrigger.StrategyTick, "ORDER_TIMEOUT");
                        continue;
                    }

                    var latest = await _gateway.GetOrderAsync(p.ClientOrderId, ct);
                    if (latest is null || latest.Status == p.Status)
                    {
                        continue;
                    }

                    var merged = Merge(p, latest);
                    if (merged.Status is OrderStatus.Filled or OrderStatus.Cancelled)
                    {
                        lock (_tracked)
                        {
                            _tracked.Remove(merged.ClientOrderId);
                        }
                    }

                    await _orders.UpdateAsync(merged, ct);
                    PublishOrder(merged, merged.Status == OrderStatus.Filled ? "成交" : "状态变更");

                    if (merged.Status == OrderStatus.Filled)
                    {
                        await OnFillAsync(merged, ct);
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _log.LogWarn($"对账 {p.ClientOrderId} 异常: {ex.Message}");
                }
            }
        }
    }

    private async Task ReconcileFillAsync(OrderRecord filled, CancellationToken ct = default)
    {
        lock (_tracked)
        {
            _tracked.Remove(filled.ClientOrderId);
        }

        var db = await _orders.GetByClientOrderIdAsync(filled.ClientOrderId, ct);
        if (db is null)
        {
            return; // 非本系统订单（应急单单独入库）
        }

        Merge(db, filled);
        db.Status = OrderStatus.Filled;
        await _orders.UpdateAsync(db, ct);
        PublishOrder(db, "成交");
        await OnFillAsync(db, ct);
    }

    private async Task OnFillAsync(OrderRecord filled, CancellationToken ct)
    {
        var state = await _account.RefreshAsync(ct);
        var cfg = _runtime.Snapshot();

        // 亏损事件紧急唤醒（最高优先级）：主动认赔卖出 或 权益回撤
        if (filled.Side == OrderSide.Sell && filled.RealizedPnl < 0m)
        {
            _runtime.WakeHigh(DecisionTrigger.LossEventWakeup,
                $"SELL_LOSS:{filled.Symbol} 认赔 ${filled.RealizedPnl:0.00}", filled.RealizedPnl);
            _log.LogWarn($"亏损唤醒：{filled.Symbol} 卖出认赔 ${filled.RealizedPnl:0.00}");
        }
        else if (state.EquityUsd < cfg.LastSettlementEquity)
        {
            _runtime.WakeHigh(DecisionTrigger.LossEventWakeup,
                $"EQUITY_DRAWNDOWN:${state.EquityUsd:0.00} < 结算基准 ${cfg.LastSettlementEquity:0.00}", 0m);
            _log.LogWarn($"亏损唤醒：权益 ${state.EquityUsd:0.00} 回撤至结算基准 ${cfg.LastSettlementEquity:0.00} 之下");
        }
    }

    // ================= 工具 =================

    private async Task UpdateDbStatusAsync(string clientOrderId, OrderStatus status, CancellationToken ct)
    {
        var db = await _orders.GetByClientOrderIdAsync(clientOrderId, ct);
        if (db is null)
        {
            return;
        }

        db.Status = status;
        db.UpdatedAtUtc = DateTime.UtcNow;
        await _orders.UpdateAsync(db, ct);
        PublishOrder(db, status == OrderStatus.Cancelled ? "撤单" : "状态变更");
    }

    private void PublishOrder(OrderRecord o, string tag)
    {
        _bus.Publish(EngineEventKind.OrderEvent,
            JsonSerializer.Serialize(new UiOrderEvent
            {
                ClientOrderId = o.ClientOrderId,
                Symbol = o.Symbol,
                Side = o.Side.ToString(),
                Price = o.Price,
                AmountUsd = o.AmountUsd,
                Status = o.Status.ToString(),
                RealizedPnl = o.RealizedPnl,
                Tag = tag,
                Ts = o.UpdatedAtUtc ?? o.CreatedAtUtc,
            }, DvcJsonContext.Default.UiOrderEvent));
    }

    private void PublishFlow(string message)
    {
        _bus.Publish(EngineEventKind.FlowLog,
            JsonSerializer.Serialize(new UiFlowLog { Message = message }, DvcJsonContext.Default.UiFlowLog));
        _log.LogInfo(message);
    }

    private static OrderRecord Merge(OrderRecord db, OrderRecord latest)
    {
        db.Status = latest.Status;
        db.FilledBaseAmount = latest.FilledBaseAmount;
        db.FilledAvgPrice = latest.FilledAvgPrice;
        db.ExchangeOrderId = latest.ExchangeOrderId ?? db.ExchangeOrderId;
        db.UpdatedAtUtc = DateTime.UtcNow;
        if (latest.RealizedPnl != 0m)
        {
            db.RealizedPnl = latest.RealizedPnl;
        }

        return db;
    }
}
