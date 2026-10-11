using Dvc.Core.Market;

namespace Dvc.Engine;

/// <summary>
/// 行情上下文存储：从 feed 事件（盘口/逐笔/K线）实时汇总每币种微观结构。
/// 供 AI 输入快照组装（盘口不平衡、价差、1H 波幅、15M 趋势、5M 资金流速）。
/// </summary>
public sealed class MarketContextStore
{
    // 采样：每秒至多一个价格样本；逐笔仅用于资金流统计。
    private readonly Dictionary<string, SymState> _states = [];
    private readonly object _lock = new();

    public void EnsureSymbol(string symbol)
    {
        lock (_lock)
        {
            _states.TryAdd(symbol, new SymState());
        }
    }

    public void OnOrderBook(OrderBookSnapshot book)
    {
        lock (_lock)
        {
            var st = Get(book.Symbol);
            st.Book = book;
            Sample(st, book.Mid, DateTime.UtcNow);
        }
    }

    public void OnTrade(TradeTick t)
    {
        lock (_lock)
        {
            var st = Get(t.Symbol);
            st.LastPrice = t.Price;
            var usd = t.Price * t.Size;
            st.Flows.Enqueue((t.TimestampUtc, usd, t.Side == "buy"));
            while (st.Flows.Count > 0 && st.Flows.Peek().T < t.TimestampUtc.AddMinutes(-5))
            {
                st.Flows.Dequeue();
            }

            Sample(st, t.Price, DateTime.UtcNow);
        }
    }

    public void OnKlineClosed(Kline k)
    {
        lock (_lock)
        {
            var st = Get(k.Symbol);
            st.LastKline = k;
        }
    }

    public MarketContext Build(string symbol)
    {
        lock (_lock)
        {
            return BuildUnsafe(Get(symbol), symbol, DateTime.UtcNow);
        }
    }

    public IReadOnlyList<MarketContext> BuildAll(IEnumerable<string> symbols)
    {
        var now = DateTime.UtcNow;
        lock (_lock)
        {
            return symbols.Select(s => BuildUnsafe(Get(s), s, now)).ToList();
        }
    }

    public void PruneStale(string symbol, TimeSpan olderThan)
    {
        lock (_lock)
        {
            if (!_states.TryGetValue(symbol, out var st))
            {
                return;
            }

            var cutoff = DateTime.UtcNow - olderThan;
            while (st.Samples.Count > 0 && st.Samples.Peek().T < cutoff)
            {
                st.Samples.Dequeue();
            }
        }
    }

    private SymState Get(string symbol)
    {
        if (!_states.TryGetValue(symbol, out var st))
        {
            st = new SymState();
            _states[symbol] = st;
        }

        return st;
    }

    private static void Sample(SymState st, decimal price, DateTime now)
    {
        st.LastPrice = price;
        if (now - st.LastSampleAt < TimeSpan.FromSeconds(1))
        {
            return;
        }

        st.LastSampleAt = now;
        st.Samples.Enqueue((now, price));
        if (st.Samples.Count > 5000)
        {
            st.Samples.Dequeue();
        }
    }

    private static MarketContext BuildUnsafe(SymState st, string symbol, DateTime now)
    {
        var book = st.Book;
        var lastPrice = st.LastPrice > 0 ? st.LastPrice : book?.Mid ?? 0m;
        decimal bestBid = 0m, bestAsk = 0m, bidDepth = 0m, askDepth = 0m;
        if (book is not null && book.Bids.Count > 0 && book.Asks.Count > 0)
        {
            bestBid = book.BestBid;
            bestAsk = book.BestAsk;
            bidDepth = book.Bids.Sum(b => b.Size);
            askDepth = book.Asks.Sum(a => a.Size);
        }

        var spreadBps = bestBid > 0 ? (bestAsk - bestBid) / bestBid * 10000m : 0m;
        var imbalance = bidDepth + askDepth > 0 ? (bidDepth - askDepth) / (bidDepth + askDepth) : 0m;

        // 1H 波幅：样本窗口内 (max-min)/min
        var vol1H = 0m;
        var cutoff1H = now.AddHours(-1);
        var window = st.Samples.Where(s => s.T >= cutoff1H).ToList();
        if (window.Count > 1)
        {
            var min = window.Min(s => s.P);
            var max = window.Max(s => s.P);
            if (min > 0)
            {
                vol1H = (max - min) / min * 100m;
            }
        }

        // 15M 涨跌幅 + 趋势信号
        var cutoff15M = now.AddMinutes(-15);
        var old = st.Samples.LastOrDefault(s => s.T <= cutoff15M).P;
        var change15M = 0m;
        if (old > 0)
        {
            change15M = (lastPrice - old) / old * 100m;
        }

        var trend = change15M switch
        {
            > 1.5m => "BULLISH_STRONG",
            > 0.5m => "BULLISH",
            < -1.5m => "BEARISH_STRONG",
            < -0.5m => "BEARISH",
            _ => "NEUTRAL",
        };

        // 5M 资金流
        var cutoff5M = now.AddMinutes(-5);
        decimal turnover = 0m, buyFlow = 0m, sellFlow = 0m;
        foreach (var (t, usd, isBuy) in st.Flows)
        {
            if (t >= cutoff5M)
            {
                turnover += usd;
                if (isBuy)
                {
                    buyFlow += usd;
                }
                else
                {
                    sellFlow += usd;
                }
            }
        }

        return new MarketContext
        {
            Symbol = symbol,
            SnapshotUtc = now,
            LastPrice = lastPrice,
            BestBid = bestBid,
            BestAsk = bestAsk,
            SpreadBps = spreadBps,
            BidDepth = bidDepth,
            AskDepth = askDepth,
            Imbalance = imbalance,
            BidLevels = book?.Bids ?? [],
            AskLevels = book?.Asks ?? [],
            Volatility1HPercent = Math.Round(vol1H, 2),
            Change15MPercent = Math.Round(change15M, 3),
            TrendSignal15M = trend,
            Turnover5M = Math.Round(turnover, 2),
            BuyFlow5M = Math.Round(buyFlow, 2),
            SellFlow5M = Math.Round(sellFlow, 2),
        };
    }

    private sealed class SymState
    {
        public OrderBookSnapshot? Book { get; set; }
        public decimal LastPrice { get; set; }
        public DateTime LastSampleAt { get; set; } = DateTime.MinValue;
        public Queue<(DateTime T, decimal P)> Samples { get; } = [];
        public Queue<(DateTime T, decimal Usd, bool IsBuy)> Flows { get; } = [];
        public Kline? LastKline { get; set; }
    }
}
