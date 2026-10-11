using System.Text.Json;
using Dvc.Core;
using Dvc.Core.Events;
using Dvc.Core.Exchange;
using Dvc.Core.Market;
using Dvc.Core.Trading;
using Microsoft.Extensions.Hosting;

namespace Dvc.Engine;

/// <summary>
/// 管道一：行情接收（Market Ingestion）。
/// 订阅数据源事件 → 汇总进 MarketContextStore；按模式降频（Scalping 秒级 / Swing K线收盘）；
/// 1 秒节流向事件总线推送 UiMarketTick（看板同步）。与 AI 推理、订单执行物理隔离。
/// </summary>
public sealed class MarketIngestionService : BackgroundService
{
    private readonly IMarketDataFeed _feed;
    private readonly MarketContextStore _market;
    private readonly RuntimeState _runtime;
    private readonly EngineEventBus _bus;
    private readonly ILogger _log;

    public MarketIngestionService(
        IMarketDataFeed feed,
        MarketContextStore market,
        RuntimeState runtime,
        EngineEventBus bus,
        ILogger log)
    {
        _feed = feed;
        _market = market;
        _runtime = runtime;
        _bus = bus;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        foreach (var s in _feed.Symbols)
        {
            _market.EnsureSymbol(s);
        }

        _feed.OnTrade += _market.OnTrade;
        _feed.OnOrderBook += _market.OnOrderBook;
        _feed.OnKlineClosed += OnKlineClosed;

        await _feed.StartAsync(stoppingToken);
        _log.LogInfo($"行情管道启动：数据源={_feed.Mode} 币种={string.Join(',', _feed.Symbols)}");

        using var uiTimer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        var tickCount = 0;
        try
        {
            while (await uiTimer.WaitForNextTickAsync(stoppingToken))
            {
                var cfg = _runtime.Snapshot();
                if (cfg.RunState == SystemRunState.Halted)
                {
                    continue;
                }

                PublishMarketTick(cfg);

                // Scalping 模式：秒级节拍唤醒 AI（Swing 模式由 K 线收盘唤醒）
                if (cfg.TradingMode == TradingMode.Scalping && ++tickCount % 2 == 0)
                {
                    _runtime.WakeLow(DecisionTrigger.StrategyTick, "SCALPING_TICK:2s");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停机
        }
        finally
        {
            await _feed.StopAsync();
        }
    }

    private void OnKlineClosed(Kline k)
    {
        _market.OnKlineClosed(k);
        var cfg = _runtime.Snapshot();
        if (cfg.TradingMode == TradingMode.Swing && cfg.RunState == SystemRunState.Running)
        {
            _runtime.WakeLow(DecisionTrigger.StrategyTick, $"KLINE:{k.Granularity}:{k.Symbol}");
            _log.LogInfo($"Swing K线收盘唤醒：{k.Symbol} {k.Granularity} close={k.Close}");
        }
    }

    private void PublishMarketTick(TradingConfig cfg)
    {
        var symbols = cfg.AssetScope == AssetScope.MultiPair
            ? cfg.WatchSymbols
            : [cfg.ActiveSymbol];

        foreach (var sym in symbols)
        {
            var ctx = _market.Build(sym);
            if (ctx.LastPrice <= 0)
            {
                continue; // 数据源尚未就绪
            }

            var tick = new UiMarketTick
            {
                Symbol = sym,
                Ts = ctx.SnapshotUtc,
                LastPrice = ctx.LastPrice,
                BestBid = ctx.BestBid,
                BestAsk = ctx.BestAsk,
                SpreadBps = Math.Round(ctx.SpreadBps, 2),
                Imbalance = Math.Round(ctx.Imbalance, 4),
                Vol1H = ctx.Volatility1HPercent,
                Trend15M = ctx.TrendSignal15M,
                Change15M = ctx.Change15MPercent,
                Turnover5M = ctx.Turnover5M,
            };
            _bus.Publish(EngineEventKind.MarketUpdate,
                JsonSerializer.Serialize(tick, DvcJsonContext.Default.UiMarketTick));
        }
    }
}
