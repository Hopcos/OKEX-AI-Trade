using System.Globalization;
using System.Text.Json;
using Dvc.Core;
using Dvc.Core.Configuration;
using Dvc.Core.Events;
using Dvc.Core.Trading;
using Dvc.Persistence;
using Microsoft.Extensions.Hosting;

namespace Dvc.Engine;

/// <summary>
/// 持仓管家：买入只是开始，卖出才是盈利与现金回笼。每 2 秒独立巡检（不依赖 AI 调用，LLM 慢/挂了也照常运行），
/// 让每一笔持仓都有退出计划——
/// ① 买一价 ≥ 保本价：立即止盈落袋；
/// ② 否则在保本价挂止盈卖单等待成交（已有未成交卖单则不重复挂）；
/// ③ 浮亏触及止损线：止损离场，释放被套资金。
/// 覆盖全部持仓（含已不是聚焦标的的币），不会出现"聚焦切走后旧仓位无人管"。
/// </summary>
public sealed class PositionManagerService : BackgroundService
{
    /// <summary>低于该市值的碎仓不处理（低于交易所最小下单额，卖不出去）。</summary>
    private const decimal DustUsd = 1m;

    private readonly RuntimeState _runtime;
    private readonly AccountService _account;
    private readonly MarketContextStore _market;
    private readonly OrderExecutionService _executor;
    private readonly OrderRepository _orders;
    private readonly BrainService _brain;
    private readonly EngineEventBus _bus;
    private readonly ILogger _log;
    private readonly TradingOptions _options;

    private readonly Dictionary<string, DateTime> _lastPlacedAt = [];
    private DateTime _lastErrorLog = DateTime.MinValue;

    public PositionManagerService(
        RuntimeState runtime,
        AccountService account,
        MarketContextStore market,
        OrderExecutionService executor,
        OrderRepository orders,
        BrainService brain,
        EngineEventBus bus,
        ILogger log,
        TradingOptions options)
    {
        _runtime = runtime;
        _account = account;
        _market = market;
        _executor = executor;
        _orders = orders;
        _brain = brain;
        _bus = bus;
        _log = log;
        _options = options;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await TickAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    if (DateTime.UtcNow - _lastErrorLog > TimeSpan.FromSeconds(30))
                    {
                        _lastErrorLog = DateTime.UtcNow;
                        _log.LogError("持仓管家巡检异常（自动续跑）: " + ex.Message, ex);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停机
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        if (!_options.Profit.Enabled || !_options.Profit.AutoExit || _runtime.IsHalted)
        {
            return;
        }

        var state = await _account.RefreshAsync(ct);
        if (state.Holdings.Count == 0)
        {
            return;
        }

        var pendingSells = (await _orders.GetOpenAsync(null, ct))
            .Where(o => o.Side == OrderSide.Sell)
            .ToLookup(o => o.Symbol);

        var cfg = _runtime.Snapshot();
        var directive = _brain.Current;
        var stopLoss = ProfitPolicy.StopLossPercent(_options, directive);

        foreach (var (baseCcy, qty) in state.Holdings)
        {
            if (qty <= 0m || !state.AvgCosts.TryGetValue(baseCcy, out var avg) || avg <= 0m)
            {
                continue;
            }

            var symbol = FindSymbol(cfg, baseCcy);
            if (symbol is null)
            {
                continue;
            }

            if (_lastPlacedAt.TryGetValue(symbol, out var last) && DateTime.UtcNow - last < TimeSpan.FromSeconds(8))
            {
                continue; // 刚下过单，等执行管道落地，防重复挂单
            }

            var ctx = _market.Build(symbol);
            if (ctx.BestBid <= 0m || ctx.LastPrice <= 0m || qty * ctx.BestBid < DustUsd)
            {
                continue;
            }

            // 止盈目标随持仓时间衰减：持仓越久越贴近"刚好覆盖手续费"，保证仓位能周转
            var held = state.PositionAgeMinutes.TryGetValue(baseCcy, out var heldMin) ? heldMin : 0m;
            var minNet = ProfitPolicy.MinNetProfitBps(_options, directive, held);
            var floor = Math.Ceiling(ProfitPolicy.MinProfitSellPrice(avg, _options.FeeBps, minNet) * 1_000_000m) / 1_000_000m;
            var lossPct = (avg - ctx.LastPrice) / avg * 100m;

            // 已有未成交卖单：覆盖整仓且价格不低于当前保本价才算"有退出计划"；
            // 否则（后续又买入加仓、均价上移导致旧挂单价偏低）撤掉重挂，保证整仓都有计划
            var resting = pendingSells[symbol].ToList();
            if (resting.Count > 0)
            {
                var coveredQty = resting.Sum(o => Math.Max(0m, o.BaseAmount - o.FilledBaseAmount));
                var tooLow = resting.Any(o => o.Price < floor * 0.9999m) && lossPct < stopLoss;
                // 目标价已随持仓时间下调：挂在旧高价的卖单撤掉按新目标重挂（留 3bp 容差，避免频繁撤挂）
                var tooHigh = resting.Any(o => o.Price > floor * 1.0003m);
                if (coveredQty >= qty * 0.999m && !tooLow && !tooHigh)
                {
                    continue;
                }

                foreach (var o in resting)
                {
                    await _executor.Commands.WriteAsync(new ExecCommand(
                        ExecKind.Cancel, symbol, OrderSide.Sell, 0m, 0m, o.ClientOrderId,
                        "持仓管家重排退出计划"), ct);
                }

                _lastPlacedAt[symbol] = DateTime.UtcNow;
                var replanMsg = $"📤 持仓管家 {symbol}：持仓 {qty:0.########} 与止盈挂单（覆盖 {coveredQty:0.########}、价低于保本价={tooLow}、目标已下调需降价={tooHigh}；已持有 {held:0} 分钟，目标净利润 {minNet:0.#}bp）不匹配，撤单后按整仓重挂";
                _log.LogInfo(replanMsg);
                _bus.Publish(EngineEventKind.FlowLog,
                    JsonSerializer.Serialize(new UiFlowLog { Message = replanMsg }, DvcJsonContext.Default.UiFlowLog));
                continue;
            }

            decimal price;
            string reason;
            if (ctx.BestBid >= floor)
            {
                price = ctx.BestBid;
                reason = string.Create(CultureInfo.InvariantCulture,
                    $"止盈落袋：买一 {ctx.BestBid:0.######} ≥ 保本价 {floor:0.######}（成本 {avg:0.######}）");
            }
            else if (lossPct >= stopLoss)
            {
                price = ctx.BestBid;
                reason = string.Create(CultureInfo.InvariantCulture,
                    $"止损离场：浮亏 {lossPct:0.00}% ≥ 止损线 {stopLoss:0.##}%，释放被套资金");
            }
            else
            {
                var ceiling = (ctx.BestAsk > 0m ? ctx.BestAsk : ctx.LastPrice) *
                              (1m + Math.Max(0, _options.MaxPriceDeviationBps - 10) / 10000m);
                if (floor > ceiling)
                {
                    continue; // 保本价远在盘口之外，挂了也会被价格偏差护栏否决，等行情靠近再说
                }

                price = floor;
                reason = string.Create(CultureInfo.InvariantCulture,
                    $"止盈挂单：在保本价 {floor:0.######} 挂卖整仓等待成交（成本 {avg:0.######}）");
            }

            _lastPlacedAt[symbol] = DateTime.UtcNow;
            await _executor.Commands.WriteAsync(new ExecCommand(
                ExecKind.PlaceLimit, symbol, OrderSide.Sell, price, qty * price, null, "持仓管家 " + reason), ct);

            var msg = $"📤 持仓管家 {symbol}：{reason}";
            _log.LogInfo(msg);
            _bus.Publish(EngineEventKind.FlowLog,
                JsonSerializer.Serialize(new UiFlowLog { Message = msg }, DvcJsonContext.Default.UiFlowLog));
        }
    }

    private static string? FindSymbol(TradingConfig cfg, string baseCcy)
    {
        foreach (var s in cfg.WatchSymbols.Append(cfg.ActiveSymbol))
        {
            if (RiskEngine.BaseOf(s).Equals(baseCcy, StringComparison.OrdinalIgnoreCase))
            {
                return s;
            }
        }

        return null;
    }
}
