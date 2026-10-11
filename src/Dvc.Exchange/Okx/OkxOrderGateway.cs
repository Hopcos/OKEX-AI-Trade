using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dvc.Core;
using Dvc.Core.Configuration;
using Dvc.Core.Exchange;
using Dvc.Core.Trading;

namespace Dvc.Exchange.Okx;

/// <summary>
/// OKX REST v5 现货下单网关（Live 模式）。
/// - 仅现货路径：tdMode=cash，接口层物理切断 Margin/Futures。
/// - LIMIT 订单（AI 执行路径）；MARKET 订单仅暴露给硬编码风控层 EmergencyMarketAsync。
/// - 请求签名：OK-ACCESS-TIMESTAMP + METHOD + PATH + BODY，HMAC-SHA256 base64。
/// </summary>
public sealed class OkxOrderGateway : IOrderGateway
{
    private readonly OkxOptions _okx;
    private readonly HttpClient _http;

    public OkxOrderGateway(OkxOptions okx, HttpClient http)
    {
        _okx = okx;
        _http = http;
    }

    public string Mode => "Live";

    // Live 模式成交检测走 REST 轮询（OrderExecutionService.GetOrderAsync），不推送事件。
    public event Action<OrderRecord>? OrderFilled;

    public async Task<IReadOnlyList<OrderRecord>> GetOpenOrdersAsync(string symbol, CancellationToken ct)
    {
        var path = $"/api/v5/trade/orders-pending?instId={Uri.EscapeDataString(symbol)}";
        var resp = await GetSignedAsync(path, ct);
        var list = new List<OrderRecord>();
        if (resp.TryGetProperty("data", out var data))
        {
            foreach (var o in data.EnumerateArray())
            {
                list.Add(MapOrder(o));
            }
        }

        return list;
    }

    public async Task<OrderRecord?> GetOrderAsync(string clientOrderId, CancellationToken ct)
    {
        // clOrdId 编码了交易对：D<BASE+QUOTE><HHmmssfff><rand> / E<...>（纯字母数字，OKX 不允许连字符）
        if (!TryParseInstId(clientOrderId, out var instId))
        {
            return null;
        }

        var path = $"/api/v5/trade/order?instId={Uri.EscapeDataString(instId)}&clOrdId={Uri.EscapeDataString(clientOrderId)}";
        var resp = await GetSignedAsync(path, ct);
        if (resp.TryGetProperty("data", out var data) && data.GetArrayLength() > 0)
        {
            return MapOrder(data[0]);
        }

        return null;
    }

    public async Task<OrderRecord> PlaceLimitAsync(string symbol, OrderSide side, decimal price, decimal amountUsd, string reason, CancellationToken ct)
    {
        var (baseCcy, quoteCcy) = SplitSymbol(symbol);
        if (quoteCcy != "USDT")
        {
            return Reject(symbol, side, price, amountUsd, reason, "当前骨架仅支持 USDT 报价币");
        }

        var sz = RoundTo(amountUsd / price, 8);
        if (sz <= 0m)
        {
            return Reject(symbol, side, price, amountUsd, reason, "按当前价折算基础币不足交易所最小精度");
        }

        var symbolClean = symbol.Replace("-", "");
        var clOrdId = $"D{symbolClean}{DateTime.UtcNow:HHmmssfff}{Random.Shared.Next(100000, 999999)}";
        var body = new Dictionary<string, string>
        {
            ["instId"] = symbol,
            ["tdMode"] = "cash",
            ["side"] = side == OrderSide.Buy ? "buy" : "sell",
            ["ordType"] = "limit",
            ["px"] = price.ToString("0.######", CultureInfo.InvariantCulture),
            ["sz"] = sz.ToString("0.########", CultureInfo.InvariantCulture),
            ["clOrdId"] = clOrdId,
        };

        var resp = await PostSignedAsync("/api/v5/trade/order", body, ct);
        var order = new OrderRecord
        {
            ClientOrderId = clOrdId,
            Symbol = symbol,
            Side = side,
            Price = price,
            AmountUsd = amountUsd,
            BaseAmount = sz,
            Status = OrderStatus.Queued,
            Reason = reason,
            CreatedAtUtc = DateTime.UtcNow,
        };

        if (resp.TryGetProperty("data", out var data) && data.GetArrayLength() > 0)
        {
            var first = data[0];
            var sCode = first.TryGetProperty("sCode", out var sc) ? sc.GetString() : "0";
            if (sCode == "0")
            {
                order.ExchangeOrderId = first.TryGetProperty("ordId", out var oid) ? oid.GetString() : null;
                order.Status = OrderStatus.Pending;
            }
            else
            {
                order.Status = OrderStatus.Rejected;
                order.Reason += " | OKX:" + (first.TryGetProperty("sMsg", out var sm) ? sm.GetString() : sCode);
            }
        }
        else
        {
            order.Status = OrderStatus.Rejected;
            // 带出根级 code/msg（如 50112/51000），避免"无 data 返回"查不到原因
            var rc = resp.TryGetProperty("code", out var c) ? c.GetString() : "";
            var rm = resp.TryGetProperty("msg", out var m) ? m.GetString() : "";
            order.Reason += string.IsNullOrEmpty(rm)
                ? " | OKX: 无 data 返回"
                : $" | OKX: [{rc}] {rm}";
        }

        return order;
    }

    public async Task<bool> CancelAsync(string clientOrderId, CancellationToken ct)
    {
        if (!TryParseInstId(clientOrderId, out var instId))
        {
            return false;
        }

        var resp = await PostSignedAsync("/api/v5/trade/cancel-order",
            new Dictionary<string, string> { ["instId"] = instId, ["clOrdId"] = clientOrderId }, ct);
        if (!resp.TryGetProperty("data", out var data) || data.GetArrayLength() == 0)
        {
            return false;
        }

        var first = data[0];
        return first.TryGetProperty("sCode", out var sc) && sc.GetString() == "0";
    }

    public async Task<AccountBalances> GetBalancesAsync(CancellationToken ct)
    {
        var resp = await GetSignedAsync("/api/v5/account/balance", ct);
        var availableUsd = 0m;
        var holdings = new Dictionary<string, decimal>();
        var equityUsd = 0m;
        if (resp.TryGetProperty("data", out var data) && data.GetArrayLength() > 0)
        {
            var bal = data[0];
            if (bal.TryGetProperty("totalEq", out var te) && decimal.TryParse(te.GetString(), out var eq))
            {
                equityUsd = eq;
            }

            if (bal.TryGetProperty("details", out var details))
            {
                foreach (var d in details.EnumerateArray())
                {
                    var ccy = d.TryGetProperty("ccy", out var c) ? c.GetString() : null;
                    if (string.IsNullOrEmpty(ccy))
                    {
                        continue;
                    }

                    if (ccy == "USDT")
                    {
                        if (d.TryGetProperty("availBal", out var ab) && decimal.TryParse(ab.GetString(), out var av))
                        {
                            availableUsd = av;
                        }
                    }
                    else if (!IsStableCash(ccy))
                    {
                        // 仅真实可交易代币进入持仓（熔断清仓遍历的就是这个集合）
                        if (d.TryGetProperty("eq", out var eqv) && decimal.TryParse(eqv.GetString(), out var eqq) && eqq > 0m)
                        {
                            holdings[ccy] = eqq;
                        }
                    }
                }
            }
        }

        return new AccountBalances(availableUsd, holdings, equityUsd > 0m ? equityUsd : availableUsd);
    }

    public async Task<OrderRecord> EmergencyMarketAsync(string symbol, OrderSide side, decimal amountUsd, string reason, CancellationToken ct)
    {
        var body = new Dictionary<string, string>
        {
            ["instId"] = symbol,
            ["tdMode"] = "cash",
            ["side"] = side == OrderSide.Buy ? "buy" : "sell",
            ["ordType"] = "market",
            ["tgtCcy"] = side == OrderSide.Buy ? "quote_ccy" : "base_ccy",
            ["sz"] = amountUsd.ToString("0.########", CultureInfo.InvariantCulture),
        };

        var resp = await PostSignedAsync("/api/v5/trade/order", body, ct);
        var symbolClean = symbol.Replace("-", "");
        var clOrdId = $"E{symbolClean}{DateTime.UtcNow:HHmmssfff}{Random.Shared.Next(100000, 999999)}";
        var order = new OrderRecord
        {
            ClientOrderId = clOrdId,
            Symbol = symbol,
            Side = side,
            Price = 0m,
            AmountUsd = amountUsd,
            BaseAmount = 0m,
            Status = OrderStatus.Queued,
            Reason = reason + " [风控紧急市价]",
            CreatedAtUtc = DateTime.UtcNow,
        };
        if (resp.TryGetProperty("data", out var data) && data.GetArrayLength() > 0)
        {
            var first = data[0];
            var sCode = first.TryGetProperty("sCode", out var sc) ? sc.GetString() : "0";
            order.Status = sCode == "0" ? OrderStatus.Pending : OrderStatus.Rejected;
            order.Reason += sCode == "0" ? "" : " | OKX:" + (first.TryGetProperty("sMsg", out var sm) ? sm.GetString() : sCode);
        }
        else
        {
            order.Status = OrderStatus.Rejected;
            var rc = resp.TryGetProperty("code", out var c) ? c.GetString() : "";
            var rm = resp.TryGetProperty("msg", out var m) ? m.GetString() : "";
            order.Reason += string.IsNullOrEmpty(rm)
                ? " | OKX: 无 data 返回"
                : $" | OKX: [{rc}] {rm}";
        }

        return order;
    }

    // ================= 签名与请求 =================

    /// <summary>
    /// 稳定币/法币现金类：不进入持仓集合（否则熔断清仓会对它们发起不存在的 USDT 交易对卖单）。
    /// 其价值仍计入 totalEq（权益），仅不参与逐币清仓。
    /// </summary>
    private static readonly HashSet<string> StableCash = ["USDT", "USDC", "USD", "TUSD", "DAI", "FDUSD", "USDP"];

    private static bool IsStableCash(string ccy) => StableCash.Contains(ccy);

    /// <summary>
    /// OKX v5 要求的 OK-ACCESS-TIMESTAMP：ISO8601 格式（UTC）。
    /// 实测：Unix 秒 → 50112 Invalid OK-ACCESS-TIMESTAMP；ISO（含毫秒）→ 通过。
    /// 签名串必须与请求头使用同一时间戳字符串。
    /// </summary>
    private static string OkxTimestamp() =>
        DateTimeOffset.UtcNow.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    private async Task<JsonElement> GetSignedAsync(string requestPath, CancellationToken ct)
    {
        var ts = OkxTimestamp();
        var sign = Sign(ts, "GET", requestPath, "", _okx.SecretKey);
        using var req = new HttpRequestMessage(HttpMethod.Get, _okx.RestBaseUrl + requestPath);
        ApplyHeaders(req, ts, sign, false);
        using var resp = await _http.SendAsync(req, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement.Clone();

        // 签名 REST 通道不可用时显式抛出（AccountService 会记录并沿用上次快照），
        // 避免"401/50112 被静默吞成余额 0"的迷惑性表现
        if (root.TryGetProperty("code", out var codeEl) && codeEl.GetString() is { Length: > 0 } code && code != "0")
        {
            var msg = root.TryGetProperty("msg", out var msgEl) ? msgEl.GetString() : code;
            throw new InvalidOperationException($"OKX 签名请求失败 [{requestPath}] code={code} msg={msg}");
        }

        return root;
    }

    private async Task<JsonElement> PostSignedAsync(string requestPath, Dictionary<string, string> body, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(body, DvcJsonContext.Default.DictionaryStringString);
        var ts = OkxTimestamp();
        var sign = Sign(ts, "POST", requestPath, json, _okx.SecretKey);
        using var req = new HttpRequestMessage(HttpMethod.Post, _okx.RestBaseUrl + requestPath)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        ApplyHeaders(req, ts, sign, true);
        using var resp = await _http.SendAsync(req, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    private void ApplyHeaders(HttpRequestMessage req, string timestamp, string sign, bool isPost)
    {
        req.Headers.Add("OK-ACCESS-KEY", _okx.ApiKey);
        req.Headers.Add("OK-ACCESS-SIGN", sign);
        req.Headers.Add("OK-ACCESS-TIMESTAMP", timestamp);
        req.Headers.Add("OK-ACCESS-PASSPHRASE", _okx.Passphrase);
        if (isPost)
        {
            req.Content!.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }
    }

    internal static string Sign(string timestamp, string method, string requestPath, string body, string secretKey)
    {
        var msg = $"{timestamp}{method}{requestPath}{body}";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secretKey));
        return Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(msg)));
    }

    // ================= 映射 =================

    private static OrderRecord MapOrder(JsonElement o)
    {
        var side = o.TryGetProperty("side", out var s) ? s.GetString() : "";
        decimal.TryParse(o.TryGetProperty("px", out var px) ? px.GetString() : "0", out var price);
        decimal.TryParse(o.TryGetProperty("sz", out var sz) ? sz.GetString() : "0", out var size);
        decimal.TryParse(o.TryGetProperty("accFillSz", out var afs) ? afs.GetString() : "0", out var filled);
        decimal.TryParse(o.TryGetProperty("avgPx", out var ap) ? ap.GetString() : "0", out var avgPx);
        var state = o.TryGetProperty("state", out var st) ? st.GetString() : "";

        var status = state switch
        {
            "filled" => OrderStatus.Filled,
            "canceled" => OrderStatus.Cancelled,
            "live" or "partially_filled" => OrderStatus.Pending,
            _ => OrderStatus.Pending,
        };

        return new OrderRecord
        {
            Symbol = o.TryGetProperty("instId", out var iid) ? iid.GetString() ?? "" : "",
            Side = side == "buy" ? OrderSide.Buy : OrderSide.Sell,
            Price = price,
            BaseAmount = size,
            AmountUsd = price * size,
            FilledBaseAmount = filled,
            FilledAvgPrice = avgPx,
            Status = status,
            ExchangeOrderId = o.TryGetProperty("ordId", out var oid) ? oid.GetString() : null,
            ClientOrderId = o.TryGetProperty("clOrdId", out var coid) ? coid.GetString() ?? "" : "",
            UpdatedAtUtc = DateTime.UtcNow,
        };
    }

    private static (string Base, string Quote) SplitSymbol(string symbol)
    {
        var idx = symbol.IndexOf('-');
        return idx < 0 ? (symbol, "USDT") : (symbol[..idx], symbol[(idx + 1)..]);
    }

    private static decimal RoundTo(decimal v, int digits) => Math.Round(v, digits, MidpointRounding.AwayFromZero);

    /// <summary>
    /// 从 clOrdId（D&lt;BASE+QUOTE&gt;&lt;ts&gt;&lt;rand&gt; / E&lt;...&gt;，纯字母数字）还原交易对。
    /// 编码规则：尾部固定 15 位数字（HHmmssfff=9 + 随机 6），其余为去掉 "-" 的交易对（如 BTCUSDT）。
    /// </summary>
    private static bool TryParseInstId(string clOrdId, out string instId)
    {
        instId = "";
        if (string.IsNullOrEmpty(clOrdId) || (clOrdId[0] != 'D' && clOrdId[0] != 'E'))
        {
            return false;
        }

        var body = clOrdId[1..];
        var digits = 0;
        for (var i = body.Length - 1; i >= 0 && char.IsDigit(body[i]); i--)
        {
            digits++;
        }

        var cleanLen = body.Length - digits;
        if (cleanLen < 7) // 最短 BTCUSDT=7
        {
            return false;
        }

        var clean = body[..cleanLen];
        instId = $"{clean[..^4]}-{clean[^4..]}"; // 报价币固定 4 位（USDT）
        return instId.Length > 3;
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
}
