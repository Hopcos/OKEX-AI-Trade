using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Dvc.Core.Configuration;
using Dvc.Core.Exchange;
using Dvc.Core.Market;

namespace Dvc.Exchange.Okx;

/// <summary>
/// OKX 公共行情源（Live 模式）。
/// - WebSocket：books5（盘口五档）+ trades（逐笔）。
/// - K 线（candle15m/candle1H）：走 REST /api/v5/market/candles 轮询（15s/次）——
///   实测当前网关/OKX 通道列表拒绝 candle* WS 订阅（60018），REST 通道稳定可用。
/// 断线自动重连（指数退避）。行情只读管道，与交易 REST 路径物理分离。
/// </summary>
public sealed class OkxMarketFeed : IMarketDataFeed
{
    private readonly OkxOptions _okx;
    private readonly string[] _symbols;
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<string, OrderBookSnapshot> _books = [];
    private readonly Dictionary<string, decimal> _lastPrice = [];
    private readonly object _lock = new();
    private readonly HttpClient _http;
    private readonly object _klineLock = new();
    private readonly Dictionary<(string Symbol, string Bar), long> _lastCandleTs = [];

    private static readonly string[] CandleBars = ["15m", "1H"];

    public OkxMarketFeed(OkxOptions okx, string[] symbols)
    {
        _okx = okx;
        _symbols = symbols.Length == 0 ? ["BTC-USDT"] : symbols;
        _http = new HttpClient
        {
            BaseAddress = new Uri(okx.RestBaseUrl),
            Timeout = TimeSpan.FromSeconds(15),
        };
    }

    public string Mode => "Live";
    public IReadOnlyList<string> Symbols => _symbols;
    public event Action<TradeTick>? OnTrade;
    public event Action<OrderBookSnapshot>? OnOrderBook;
    public event Action<Kline>? OnKlineClosed;

    public Task StartAsync(CancellationToken ct)
    {
        // WS 行情 + REST K 线轮询 双后台任务，互不阻塞
        _ = Task.Run(() => RunLoopAsync(ct), CancellationToken.None);
        _ = Task.Run(() => CandlePollerLoopAsync(ct), CancellationToken.None);
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        _cts.Cancel();
        return Task.CompletedTask;
    }

    public bool TryGetOrderBook(string symbol, out OrderBookSnapshot book)
    {
        lock (_lock)
        {
            return _books.TryGetValue(symbol, out book!);
        }
    }

    public decimal GetLastPrice(string symbol)
    {
        lock (_lock)
        {
            return _lastPrice.TryGetValue(symbol, out var p) ? p : 0m;
        }
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        var attempt = 0;
        while (!_cts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            using var ws = new ClientWebSocket();
            try
            {
                await ws.ConnectAsync(new Uri(_okx.PublicWsUrl), ct);
                attempt = 0;
                await SubscribeAsync(ws, ct);
                await ReceiveLoopAsync(ws, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception)
            {
                // 断线重连
            }

            if (_cts.IsCancellationRequested || ct.IsCancellationRequested)
            {
                break;
            }

            attempt++;
            var delay = Math.Min(30_000, _okx.ReconnectDelayMs * attempt);
            try
            {
                await Task.Delay(delay, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task SubscribeAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var args = new List<string>();
        foreach (var sym in _symbols)
        {
            args.Add($$"""{"channel":"{{_okx.OrderBookChannel}}","instId":"{{sym}}"}""");
            args.Add($$"""{"channel":"trades","instId":"{{sym}}"}""");
        }

        var msg = $$"""{"op":"subscribe","args":[{{string.Join(',', args)}}]}""";
        await ws.SendAsync(Encoding.UTF8.GetBytes(msg), WebSocketMessageType.Text, true, ct);
    }

    // ============ REST K 线轮询（candle* WS 通道 60018 不可用的兜底方案） ============

    private async Task CandlePollerLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                foreach (var sym in _symbols)
                {
                    foreach (var bar in CandleBars)
                    {
                        await PollCandlesAsync(sym, bar, ct);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停机
        }
        catch (Exception)
        {
            // 单轮失败静默，下一轮重试
        }
    }

    private async Task PollCandlesAsync(string symbol, string bar, CancellationToken ct)
    {
        try
        {
            using var resp = await _http.GetAsync(
                $"/api/v5/market/candles?instId={Uri.EscapeDataString(symbol)}&bar={bar}&limit=20", ct);
            resp.EnsureSuccessStatusCode();
            var text = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(text);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            // OKX 返回按时间倒序（最新在前）；逐根升序刷新并跳过未收盘（confirm!=1）与重复
            foreach (var k in data.EnumerateArray())
            {
                if (k.GetArrayLength() < 9 || k[0].ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                if (k[8].GetString() != "1")
                {
                    continue; // 当前未收盘 K 线
                }

                EmitKline(symbol, bar, k);
            }
        }
        catch (Exception)
        {
            // 网络/解析异常静默，下轮重试
        }
    }

    private void EmitKline(string symbol, string bar, JsonElement k)
    {
        var openTimeMs = long.Parse(k[0].GetString()!);
        var key = (symbol, bar);
        lock (_klineLock)
        {
            if (_lastCandleTs.TryGetValue(key, out var last) && last >= openTimeMs)
            {
                return; // 已发射过
            }

            _lastCandleTs[key] = openTimeMs;
        }

        var openTime = DateTimeOffset.FromUnixTimeMilliseconds(openTimeMs).UtcDateTime;
        var open = decimal.Parse(k[1].GetString()!);
        var high = decimal.Parse(k[2].GetString()!);
        var low = decimal.Parse(k[3].GetString()!);
        var close = decimal.Parse(k[4].GetString()!);
        var vol = decimal.Parse(k[5].GetString()!);
        OnKlineClosed?.Invoke(new Kline(symbol, bar, openTime, open, high, low, close, vol));
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        var sb = new StringBuilder();
        while (ws.State == WebSocketState.Open && !_cts.IsCancellationRequested)
        {
            var result = await ws.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                break;
            }

            sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            if (!result.EndOfMessage)
            {
                continue;
            }

            var text = sb.ToString();
            sb.Clear();
            try
            {
                HandleMessage(text);
            }
            catch
            {
                // 单条消息解析失败不影响订阅流
            }
        }
    }

    private void HandleMessage(string text)
    {
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        if (root.TryGetProperty("event", out var ev) && ev.GetString() == "error")
        {
            Console.WriteLine($"[OKX-WS] 订阅错误: {text}");
            return;
        }

        if (!root.TryGetProperty("arg", out var arg) || !arg.TryGetProperty("channel", out var ch) ||
            !arg.TryGetProperty("instId", out var inst) || !root.TryGetProperty("data", out var data))
        {
            return;
        }

        var channel = ch.GetString() ?? "";
        var symbol = inst.GetString() ?? "";
        if (symbol.Length == 0 || data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0)
        {
            return;
        }

        switch (channel)
        {
            case "books5":
                HandleBook(symbol, data);
                break;
            case "trades":
                HandleTrades(symbol, data);
                break;
            case "candle15m":
            case "candle1H":
                HandleKline(symbol, channel, data);
                break;
        }
    }

    private void HandleBook(string symbol, JsonElement data)
    {
        var first = data[0];
        var asks = ParseLevels(first, "asks");
        var bids = ParseLevels(first, "bids");
        if (asks.Count == 0 || bids.Count == 0)
        {
            return;
        }

        var book = new OrderBookSnapshot(symbol, DateTime.UtcNow, bids, asks);
        lock (_lock)
        {
            _books[symbol] = book;
            if (book.IsValid)
            {
                _lastPrice[symbol] = book.Mid;
            }
        }

        OnOrderBook?.Invoke(book);
    }

    private static List<PriceLevel> ParseLevels(JsonElement book, string prop)
    {
        var list = new List<PriceLevel>();
        if (!book.TryGetProperty(prop, out var levels))
        {
            return list;
        }

        foreach (var lvl in levels.EnumerateArray())
        {
            if (lvl.GetArrayLength() < 2)
            {
                continue;
            }

            if (decimal.TryParse(lvl[0].GetString(), out var px) &&
                decimal.TryParse(lvl[1].GetString(), out var sz))
            {
                list.Add(new PriceLevel(px, sz));
            }
        }

        return list;
    }

    private void HandleTrades(string symbol, JsonElement data)
    {
        foreach (var t in data.EnumerateArray())
        {
            if (!decimal.TryParse(t.GetProperty("px").GetString(), out var px) ||
                !decimal.TryParse(t.GetProperty("sz").GetString(), out var sz))
            {
                continue;
            }

            var side = t.GetProperty("side").GetString() == "buy" ? "buy" : "sell";
            var ts = t.TryGetProperty("ts", out var tsv) ? tsv.GetInt64() : 0L;
            lock (_lock)
            {
                _lastPrice[symbol] = px;
            }

            OnTrade?.Invoke(new TradeTick(
                symbol,
                ts > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(ts).UtcDateTime : DateTime.UtcNow,
                px, sz, side));
        }
    }

    private void HandleKline(string symbol, string channel, JsonElement data)
    {
        // WS 通道名形如 candle15m / candle1H → 周期 bar = channel[6..]
        var bar = channel.Length > 6 ? channel[6..] : channel;
        foreach (var k in data.EnumerateArray())
        {
            if (k.ValueKind != JsonValueKind.Array || k.GetArrayLength() < 9)
            {
                continue;
            }

            EmitKline(symbol, bar, k);
        }
    }

    public ValueTask DisposeAsync()
    {
        _cts.Cancel();
        return ValueTask.CompletedTask;
    }
}
