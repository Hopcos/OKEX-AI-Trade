import urllib.request, json, sys, time, statistics
sys.stdout.reconfigure(encoding='utf-8')

def get(url):
    req = urllib.request.Request(url, headers={"User-Agent": "Mozilla/5.0"})
    return json.loads(urllib.request.urlopen(req, timeout=15).read().decode())

def candles(inst, n=3000):
    out = []; after = ''
    while len(out) < n:
        u = f"https://www.okx.com/api/v5/market/history-candles?instId={inst}&bar=1m&limit=100" + (f"&after={after}" if after else '')
        d = get(u)['data']
        if not d: break
        out += d; after = d[-1][0]; time.sleep(0.12)
    out = [(int(c[0]), float(c[1]), float(c[2]), float(c[3]), float(c[4])) for c in out]
    out.sort()
    return out

FEE = 0.001
def sim(cs, spread, tp0, tp_end, decay, sl, trend_filter=None, step=5):
    res = []
    for i in range(30, len(cs) - decay - 2, step):
        # trend filter: 15m change
        chg = (cs[i][4] - cs[i-15][4]) / cs[i-15][4] * 100
        if trend_filter == 'no_bear' and chg < -0.3: continue
        if trend_filter == 'bull' and chg < 0.15: continue
        entry = cs[i][4] * (1 + spread / 2)
        cost = entry * (1 + FEE)
        exit_net = None
        for k in range(1, decay + 1):
            tp = tp0 - (tp0 - tp_end) * min(1, k / decay)
            target = cost * (1 + tp / 10000) / (1 - FEE)
            _, o, h, l, c = cs[i + k]
            bid = c * (1 - spread / 2)
            if h >= target:  # optimistic limit fill
                exit_net = target * (1 - FEE) / cost - 1; break
            if (cost - bid * (1 - FEE)) / cost * 100 >= sl:  # stop on loss
                exit_net = bid * (1 - FEE) / cost - 1; break
        if exit_net is None:  # timeout: market out
            bid = cs[i + decay][4] * (1 - spread / 2)
            exit_net = bid * (1 - FEE) / cost - 1
        res.append(exit_net * 10000)
    return res

def summarize(name, r):
    if not r: print(name, 'no trades'); return
    w = sum(1 for x in r if x > 0) / len(r)
    print(f"{name:42s} n={len(r):4d} win={w:5.1%} avg={statistics.mean(r):7.1f}bp  worst={min(r):7.0f}")

for inst, sp in [("TRUMP-USDT", 0.00084), ("BTC-USDT", 0.00001), ("ETH-USDT", 0.00004), ("SOL-USDT", 0.0001), ("DOGE-USDT", 0.0004), ("XRP-USDT", 0.0003)]:
    try:
        cs = candles(inst)
    except Exception as e:
        print(inst, 'ERR', e); continue
    days = (cs[-1][0] - cs[0][0]) / 86400000
    print(f"== {inst} {len(cs)} bars {days:.1f}d spread={sp*1e4:.1f}bp")
    summarize("current: tp40->2/30m SL2% no filter", sim(cs, sp, 40, 2, 30, 2.0))
    summarize("tp40->2/30m SL2% no_bear", sim(cs, sp, 40, 2, 30, 2.0, 'no_bear'))
    summarize("tp40->2/30m SL2% bull", sim(cs, sp, 40, 2, 30, 2.0, 'bull'))
    summarize("tp25->2/20m SL1% no_bear", sim(cs, sp, 25, 2, 20, 1.0, 'no_bear'))
    summarize("tp15->2/15m SL0.7% no_bear", sim(cs, sp, 15, 2, 15, 0.7, 'no_bear'))
    summarize("tp60->5/60m SL1.5% bull", sim(cs, sp, 60, 5, 60, 1.5, 'bull'))
