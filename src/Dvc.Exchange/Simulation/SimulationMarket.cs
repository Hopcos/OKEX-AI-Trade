using System.Collections.Concurrent;
using Dvc.Core;
using Dvc.Core.Configuration;
using Dvc.Core.Exchange;
using Dvc.Core.Market;
using Dvc.Core.Trading;

namespace Dvc.Exchange.Simulation;

/// <summary>
/// 纸面交易市场：几何布朗运动行情发生器 + 盘口簿 + 纸面限价撮合 + 纸面账户。
/// 默认离线模式（$20 冷启动跑通全闭环）。同时实现 IMarketDataFeed 与 IOrderGateway，
/// 共享账户/挂单簿状态，保证"行情推进 → 撮合 → 资产变动"自洽。
/// </summary>
public sealed class SimulationMarket : IMarketDataFeed, IOrderGateway
{
    private static readonly (string Symbol, decimal BasePrice)[] DefaultPricing =
    [
        ("BTC-USDT", 96000m), ("ETH-USDT", 3500m), ("SOL-USDT", 185m), ("XRP-USDT", 0.58m),
        ("BNB-USDT", 620m), ("DOGE-USDT", 0.19m), ("ADA-USDT", 0.46m), ("AVAX-USDT", 34m),
        ("LINK-USDT", 17.5m), ("TON-USDT", 5.9m),
    ];

    private readonly SimulationOptions _sim;
    private readonly string[] _symbols;
    private readonly Random _rng = new(20260601);
    private readonly CancellationTokenSource _cts = new();

    private readonly Dictionary<string, SymState> _states = [];
    private readonly Dictionary<string, OrderBookSnapshot> _latestBooks = [];
    private readonly object _lock = new();

    // ---- 纸面账户 ----
    private decimal _availableUsd;
    private readonly Dictionary<string, Holding> _holdings = [];
    private decimal _realizedPnl;

    // ---- 挂单簿 ----
    private readonly List<OrderRecord> _openOrders = [];
    private readonly ConcurrentDictionary<string, OrderRecord> _history = new();

    public SimulationMarket(string[] symbols, SimulationOptions sim, decimal initialCapital)
    {
        _symbols = symbols.Length == 0 ? ["BTC-USDT"] : symbols;
        _sim = sim;
        _availableUsd = initialCapital;
        var basePriceOf = DefaultPricing.ToDictionary(p => p.Symbol, p => p.BasePrice);
        foreach (var s in _symbols)
        {
            var basePrice = basePriceOf.TryGetValue(s, out var bp) ? bp : (basePriceOf["BTC-USDT"] * 0.001m);
            if (s == "BTC-USDT" && _sim.BasePrice > 0)
            {
                basePrice = _sim.BasePrice;
            }

            _states[s] = new SymState(s, basePrice, _sim.SpreadBps, _rng);
        }
    }

    public string Mode => "Paper";
    public IReadOnlyList<string> Symbols => _symbols;
    public event Action<TradeTick>? OnTrade;
    public event Action<OrderBookSnapshot>? OnOrderBook;
    public event Action<Kline>? OnKlineClosed;
    public event Action<OrderRecord>? OrderFilled;

    public Task StartAsync(CancellationToken ct)
    {
        _ = Task.Run(() => RunAsync(ct), CancellationToken.None);
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        _cts.Cancel();
        return Task.CompletedTask;
    }

    public bool TryGetOrderBook(string symbol, out OrderBookSnapshot book) =>
        _latestBooks.TryGetValue(symbol, out book!);

    public decimal GetLastPrice(string symbol) =>
        _states.TryGetValue(symbol, out var st) ? st.LastPrice : 0m;

    // ================= 行情推进 =================

    private async Task RunAsync(CancellationToken ct)
    {
        var tickMs = Math.Max(50, _sim.TickIntervalMs);
        var regimeChangeEvery = Math.Max(10, _sim.RegimeSwitchSeconds);
        var tick = 0;
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(tickMs));
        try
        {
            while (await timer.WaitForNextTickAsync(ct) && !_cts.IsCancellationRequested)
            {
                tick++;
                var now = DateTime.UtcNow;
                double dtSec = tickMs / 1000.0;

                if (tick % Math.Max(1, (int)(regimeChangeEvery / (tickMs / 1000.0))) == 0)
                {
                    foreach (var st in _states.Values)
                    {
                        st.Regime = (Regime)_rng.Next(0, 3);
                        st.RegimeRemaining = regimeChangeEvery;
                    }
                }

                foreach (var st in _states.Values)
                {
                    st.RegimeRemaining -= tickMs / 1000.0;
                    if (st.RegimeRemaining <= 0)
                    {
                        st.Regime = (Regime)_rng.Next(0, 3);
                        st.RegimeRemaining = regimeChangeEvery;
                    }

                    StepSymbol(st, now, dtSec, tickMs);
                }

                await Task.Yield();
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停机
        }
    }

    private void StepSymbol(SymState st, DateTime now, double dtSec, int tickMs)
    {
        // 1) 价格推进（GBM + 范围回归 + 宏观 regime 漂移）
        var sigmaSec = _sim.AnnualVolatility / Math.Sqrt(31536000.0);
        // 趋势 regime 用"秒级漂移"表达：±0.03%/s → 60s 趋势段 ≈ ±1.8%、15 分钟 ≈ ±3-5%，
        // 足以让 Trend_Signal_15M 脱离 NEUTRAL。若用"年化/秒"(0.9/31536000≈3e-8)，
        // 一小时漂移仅 0.01%，趋势段永远无法形成，AI 只能看到 NEUTRAL → 永远 HOLD。
        const double trendDriftPerSec = 0.0003;
        double mu = st.Regime switch
        {
            Regime.TrendUp => trendDriftPerSec,
            Regime.TrendDown => -trendDriftPerSec,
            _ => 0.0,
        };

        // 温和均值回归，防止模拟价格长期漂移归零/爆炸
        var ratio = (double)st.LastPrice / (double)st.BasePrice;
        if (ratio < 0.5) mu += 2.0 / 31536000.0;
        else if (ratio > 1.6) mu -= 2.0 / 31536000.0;

        var z = NextGaussian();
        var shock = Math.Exp((mu - 0.5 * sigmaSec * sigmaSec) * dtSec + sigmaSec * Math.Sqrt(dtSec) * z);
        st.LastPrice = Math.Max(0.00000001m, st.LastPrice * (decimal)shock);

        // 2) 重建盘口（五档围绕中间价）
        var mid = st.LastPrice;
        var halfSpreadBps = _sim.SpreadBps / 2m;
        var bids = new List<PriceLevel>();
        var asks = new List<PriceLevel>();
        for (var i = 0; i < 5; i++)
        {
            var gapBps = halfSpreadBps + i * halfSpreadBps;
            var bidPx = RoundPrice(mid * (1m - gapBps / 10000m));
            var askPx = RoundPrice(mid * (1m + gapBps / 10000m));
            var bidSz = RoundSize(0.001m + (decimal)_rng.NextDouble() * 2.2m);
            var askSz = RoundSize(0.001m + (decimal)_rng.NextDouble() * 2.2m);
            bids.Add(new PriceLevel(bidPx, bidSz));
            asks.Add(new PriceLevel(askPx, askSz));
        }

        st.BidDepth = bids.Sum(b => b.Size);
        st.AskDepth = asks.Sum(a => a.Size);

        var book = new OrderBookSnapshot(st.Symbol, now, bids, asks);
        lock (_lock)
        {
            _latestBooks[st.Symbol] = book;
        }

        OnOrderBook?.Invoke(book);

        // 3) 逐笔成交（1-3 笔）
        var tradeCount = _rng.Next(1, 4);
        for (var i = 0; i < tradeCount; i++)
        {
            var isBuy = _rng.Next(2) == 0;
            var px = isBuy ? asks[0].Price : bids[0].Price;
            var sz = RoundSize(0.0001m + (decimal)_rng.NextDouble() * 0.6m);
            var tick = new TradeTick(st.Symbol, now, px, sz, isBuy ? "buy" : "sell");
            st.LastTradePrice = px;
            OnTrade?.Invoke(tick);
        }

        // 4) K 线聚合（1m/15m/1H 仅在收盘边界推送）
        foreach (var g in SymStateGranularities)
        {
            var closed = st.AccumulateKline(g, now, st.LastPrice, (decimal)(_rng.NextDouble() * 0.5 + 0.5));
            if (closed is not null)
            {
                OnKlineClosed?.Invoke(closed);
            }
        }

        // 5) 撮合（限价单价格穿越盘口即成交）
        MatchOrders(st.Symbol, now, book, tickMs);
    }

    private void MatchOrders(string symbol, DateTime now, OrderBookSnapshot book, int tickMs)
    {
        lock (_lock)
        {
            if (_openOrders.Count == 0)
            {
                return;
            }

            for (var i = _openOrders.Count - 1; i >= 0; i--)
            {
                var o = _openOrders[i];
                if (o.Symbol != symbol)
                {
                    continue;
                }

                decimal fillPrice = 0m;
                if (o.IsBuy && o.Price >= book.BestAsk)
                {
                    fillPrice = Math.Min(o.Price, book.BestAsk);
                }
                else if (!o.IsBuy && o.Price <= book.BestBid)
                {
                    fillPrice = Math.Max(o.Price, book.BestBid);
                }

                if (fillPrice <= 0m)
                {
                    continue;
                }

                _openOrders.RemoveAt(i);
                ApplyFill(o, fillPrice);
                o.Status = OrderStatus.Filled;
                o.FilledBaseAmount = o.BaseAmount;
                o.FilledAvgPrice = fillPrice;
                o.UpdatedAtUtc = now;
                _history[o.ClientOrderId] = o;
                OnOrderFilledSafe(o);
            }
        }
    }

    private void ApplyFill(OrderRecord o, decimal fillPrice)
    {
        if (o.IsBuy)
        {
            var cost = o.BaseAmount * fillPrice;
            _availableUsd -= cost;
            if (_holdings.TryGetValue(o.Symbol, out var h))
            {
                var totalQty = h.Quantity + o.BaseAmount;
                var totalCost = h.Quantity * h.AvgCost + cost;
                h.Quantity = totalQty;
                h.AvgCost = totalCost / totalQty;
            }
            else
            {
                _holdings[o.Symbol] = new Holding { Quantity = o.BaseAmount, AvgCost = fillPrice };
            }
        }
        else
        {
            var proceeds = o.BaseAmount * fillPrice;
            _availableUsd += proceeds;
            if (_holdings.TryGetValue(o.Symbol, out var h))
            {
                var realized = (fillPrice - h.AvgCost) * o.BaseAmount;
                _realizedPnl += realized;
                o.RealizedPnl = realized;
                h.Quantity -= o.BaseAmount;
                if (h.Quantity <= 0.00000001m)
                {
                    _holdings.Remove(o.Symbol);
                }
            }
        }
    }

    private void OnOrderFilledSafe(OrderRecord o)
    {
        try
        {
            OrderFilled?.Invoke(o);
        }
        catch
        {
            // 订阅方异常不得影响撮合循环
        }
    }

    // ================= IOrderGateway =================

    public Task<IReadOnlyList<OrderRecord>> GetOpenOrdersAsync(string symbol, CancellationToken ct)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<OrderRecord>>(
                _openOrders.Where(o => o.Symbol == symbol).Select(o => Clone(o)!).ToList());
        }
    }

    public Task<OrderRecord?> GetOrderAsync(string clientOrderId, CancellationToken ct)
    {
        lock (_lock)
        {
            var found = Clone(_openOrders.FirstOrDefault(o => o.ClientOrderId == clientOrderId))
                ?? Clone(_history.GetValueOrDefault(clientOrderId));
            return Task.FromResult(found);
        }
    }

    public Task<OrderRecord> PlaceLimitAsync(string symbol, OrderSide side, decimal price, decimal amountUsd, string reason, CancellationToken ct)
    {
        lock (_lock)
        {
            var baseAmount = RoundSize(amountUsd / price);
            if (baseAmount <= 0m)
            {
                return Task.FromResult(Reject(symbol, side, price, amountUsd, reason, "下单金额过小（按当前价折算基础币为 0）"));
            }

            var o = new OrderRecord
            {
                ClientOrderId = $"P{DateTime.UtcNow:yyyyMMddHHmmssfff}{_rng.Next(1000, 9999)}",
                Symbol = symbol,
                Side = side,
                Price = price,
                AmountUsd = amountUsd,
                BaseAmount = baseAmount,
                Status = OrderStatus.Queued,
                Reason = reason,
                CreatedAtUtc = DateTime.UtcNow,
            };

            if (side == OrderSide.Buy && amountUsd > _availableUsd + 0.000001m)
            {
                o.Status = OrderStatus.Rejected;
                o.Reason += " | 可用USD不足";
                _history[o.ClientOrderId] = o;
                return Task.FromResult(o);
            }

            if (side == OrderSide.Sell)
            {
                var hold = _holdings.TryGetValue(symbol, out var h) ? h.Quantity : 0m;
                if (baseAmount > hold + 0.00000001m)
                {
                    o.Status = OrderStatus.Rejected;
                    o.Reason += " | 持仓不足";
                    _history[o.ClientOrderId] = o;
                    return Task.FromResult(o);
                }
            }

            o.Status = OrderStatus.Pending;
            _openOrders.Add(o);
            return Task.FromResult(Clone(o)!);
        }
    }

    public Task<bool> CancelAsync(string clientOrderId, CancellationToken ct)
    {
        lock (_lock)
        {
            var o = _openOrders.FirstOrDefault(x => x.ClientOrderId == clientOrderId);
            if (o is null)
            {
                return Task.FromResult(false);
            }

            _openOrders.Remove(o);
            o.Status = OrderStatus.Cancelled;
            o.UpdatedAtUtc = DateTime.UtcNow;
            _history[o.ClientOrderId] = o;
            return Task.FromResult(true);
        }
    }

    public Task<AccountBalances> GetBalancesAsync(CancellationToken ct)
    {
        lock (_lock)
        {
            // 持仓统一返回 基础币 键（与 Live/OKX 网关口径一致，熔断清仓按基础币遍历）
            var holdings = new Dictionary<string, decimal>();
            var valuation = 0m;
            foreach (var (sym, h) in _holdings)
            {
                if (h.Quantity <= 0m)
                {
                    continue;
                }

                var dash = sym.IndexOf('-');
                var baseCcy = dash > 0 ? sym[..dash] : sym;
                holdings[baseCcy] = h.Quantity;
                valuation += h.Quantity * GetLastPrice(sym);
            }

            return Task.FromResult(new AccountBalances(
                Math.Max(0m, _availableUsd), holdings, _availableUsd + valuation, _realizedPnl));
        }
    }

    public Task<OrderRecord> EmergencyMarketAsync(string symbol, OrderSide side, decimal amountUsd, string reason, CancellationToken ct)
    {
        lock (_lock)
        {
            _latestBooks.TryGetValue(symbol, out var book);
            var slippage = 1.001m;
            var fillPrice = RoundPrice(
                side == OrderSide.Buy
                    ? (book?.BestAsk ?? GetLastPrice(symbol)) * slippage
                    : (book?.BestBid ?? GetLastPrice(symbol)) / slippage);

            // Buy: amountUsd=名义USD金额；Sell: amountUsd 承载基础币数量直接市价卖出
            var baseAmount = side == OrderSide.Buy
                ? RoundSize(amountUsd / fillPrice)
                : RoundSize(Math.Min(amountUsd, _holdings.TryGetValue(symbol, out var h) ? h.Quantity : 0m));
            if (baseAmount <= 0m)
            {
                return Task.FromResult(Reject(symbol, side, fillPrice, amountUsd, reason, "无可卖持仓"));
            }

            if (side == OrderSide.Buy)
            {
                amountUsd = baseAmount * fillPrice;
            }

            var o = new OrderRecord
            {
                ClientOrderId = $"E{DateTime.UtcNow:yyyyMMddHHmmssfff}{_rng.Next(1000, 9999)}",
                Symbol = symbol,
                Side = side,
                Price = fillPrice,
                AmountUsd = amountUsd,
                BaseAmount = baseAmount,
                FilledBaseAmount = baseAmount,
                FilledAvgPrice = fillPrice,
                Status = OrderStatus.Filled,
                Reason = reason + " [风控紧急市价]",
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow,
            };
            ApplyFill(o, fillPrice);
            _history[o.ClientOrderId] = o;
            OnOrderFilledSafe(o);
            return Task.FromResult(o);
        }
    }

    private static OrderRecord Reject(string symbol, OrderSide side, decimal price, decimal amountUsd, string reason, string why) =>
        new()
        {
            ClientOrderId = $"R{DateTime.UtcNow:yyyyMMddHHmmssfff}",
            Symbol = symbol,
            Side = side,
            Price = price,
            AmountUsd = amountUsd,
            Status = OrderStatus.Rejected,
            Reason = reason + " | " + why,
            CreatedAtUtc = DateTime.UtcNow,
        };

    public ValueTask DisposeAsync()
    {
        _cts.Cancel();
        return ValueTask.CompletedTask;
    }

    // ================= 工具 =================

    private static readonly string[] SymStateGranularities = ["1m", "15m", "1H"];

    private static decimal Max(decimal a, decimal b) => a > b ? a : b;

    private static decimal RoundPrice(decimal px) => Math.Round(px, 6, MidpointRounding.AwayFromZero);

    private static decimal RoundSize(decimal sz) => Math.Round(sz, 8, MidpointRounding.AwayFromZero);

    private double NextGaussian()
    {
        var u1 = 1.0 - _rng.NextDouble();
        var u2 = _rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
    }

    private static OrderRecord? Clone(OrderRecord? o) => o is null ? null : new OrderRecord()
    {
        Id = o.Id,
        ClientOrderId = o.ClientOrderId,
        Symbol = o.Symbol,
        Side = o.Side,
        Price = o.Price,
        AmountUsd = o.AmountUsd,
        BaseAmount = o.BaseAmount,
        FilledBaseAmount = o.FilledBaseAmount,
        FilledAvgPrice = o.FilledAvgPrice,
        Status = o.Status,
        ExchangeOrderId = o.ExchangeOrderId,
        Reason = o.Reason,
        RealizedPnl = o.RealizedPnl,
        CreatedAtUtc = o.CreatedAtUtc,
        UpdatedAtUtc = o.UpdatedAtUtc,
    };

    private enum Regime
    {
        Range = 0,
        TrendUp = 1,
        TrendDown = 2,
    }

    private sealed class SymState
    {
        private readonly Random _rng;
        private readonly decimal _spreadBps;

        public SymState(string symbol, decimal basePrice, decimal spreadBps, Random rng)
        {
            Symbol = symbol;
            BasePrice = basePrice;
            LastPrice = basePrice;
            _spreadBps = spreadBps;
            _rng = rng;
        }

        public string Symbol { get; }
        public decimal BasePrice { get; }
        public decimal LastPrice { get; set; }
        public decimal LastTradePrice { get; set; }
        public decimal BidDepth { get; set; }
        public decimal AskDepth { get; set; }
        public Regime Regime { get; set; }
        public double RegimeRemaining { get; set; }

        private readonly Dictionary<string, KlineAccum> _klines = [];

        public Kline? AccumulateKline(string granularity, DateTime now, decimal price, decimal vol)
        {
            var interval = granularity switch
            {
                "1m" => TimeSpan.FromMinutes(1),
                "15m" => TimeSpan.FromMinutes(15),
                _ => TimeSpan.FromHours(1),
            };

            if (!_klines.TryGetValue(granularity, out var acc))
            {
                acc = new KlineAccum(now);
                _klines[granularity] = acc;
            }

            acc.Update(price, vol);

            if (now - acc.OpenTime >= interval)
            {
                var closed = acc.CloseKline(Symbol, granularity);
                _klines[granularity] = new KlineAccum(now);
                return closed;
            }

            return null;
        }

        private sealed class KlineAccum
        {
            public DateTime OpenTime { get; }
            public decimal Open { get; private set; }
            public decimal High { get; private set; }
            public decimal Low { get; private set; }
            public decimal Close { get; private set; }
            public decimal Volume { get; private set; }

            public KlineAccum(DateTime openTime)
            {
                OpenTime = openTime;
            }

            public void Update(decimal price, decimal vol)
            {
                if (Open == 0m)
                {
                    Open = price;
                    High = price;
                    Low = price;
                }

                Close = price;
                High = Math.Max(High, price);
                Low = Low == 0m ? price : Math.Min(Low, price);
                Volume += vol;
            }

            public Kline CloseKline(string symbol, string granularity) =>
                new(symbol, granularity, OpenTime, Open, High, Low, Close, Volume);
        }
    }

    private sealed class Holding
    {
        public decimal Quantity { get; set; }
        public decimal AvgCost { get; set; }
    }
}
