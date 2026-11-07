import json
import math
from datetime import datetime

# Load real 1m candles for TRUMP-USDT
with open('data/market_history/TRUMP_1m_full.json', 'r', encoding='utf-8') as f:
    raw_1m = json.load(f)
raw_1m.sort(key=lambda x: int(x[0]))

bars = []
for c in raw_1m:
    bars.append({
        'ts': int(c[0]),
        'dt': datetime.fromtimestamp(int(c[0])/1000),
        'open': float(c[1]),
        'high': float(c[2]),
        'low': float(c[3]),
        'close': float(c[4]),
        'vol': float(c[5]),
        'vol_usd': float(c[6]),
    })

print(f"Loaded {len(bars)} 1m bars from {bars[0]['dt']} to {bars[-1]['dt']}")

# OKX spot fee: 10 bps (0.1%) per side
FEE_RATE = 0.0010 

def calc_indicators(bars):
    # Calculate EMA, RSI, ATR/Range, 15m trend proxy
    closes = [b['close'] for b in bars]
    highs = [b['high'] for b in bars]
    lows = [b['low'] for b in bars]
    vols = [b['vol_usd'] for b in bars]
    n = len(bars)
    
    # 1. EMA 9, EMA 21, EMA 50
    def ema(series, period):
        res = [series[0]] * len(series)
        alpha = 2.0 / (period + 1.0)
        for i in range(1, len(series)):
            res[i] = alpha * series[i] + (1 - alpha) * res[i-1]
        return res

    ema9 = ema(closes, 9)
    ema21 = ema(closes, 21)
    ema50 = ema(closes, 50)
    
    # 2. RSI 14
    rsi = [50.0] * n
    gains = [0.0] * n
    losses = [0.0] * n
    for i in range(1, n):
        diff = closes[i] - closes[i-1]
        gains[i] = max(0.0, diff)
        losses[i] = max(0.0, -diff)
    
    period = 14
    avg_gain = sum(gains[1:period+1]) / period if period < n else 0
    avg_loss = sum(losses[1:period+1]) / period if period < n else 0
    for i in range(period, n):
        avg_gain = (avg_gain * (period - 1) + gains[i]) / period
        avg_loss = (avg_loss * (period - 1) + losses[i]) / period
        if avg_loss == 0:
            rsi[i] = 100.0
        else:
            rs = avg_gain / avg_loss
            rsi[i] = 100.0 - (100.0 / (1.0 + rs))

    # 3. 15-minute change and 1-hour range
    change_15m = [0.0] * n
    vol_1h = [0.0] * n
    vol_sum_5m = [0.0] * n
    for i in range(n):
        if i >= 15:
            change_15m[i] = (closes[i] - closes[i-15]) / closes[i-15] * 100.0
        if i >= 60:
            min_60 = min(lows[i-60:i+1])
            max_60 = max(highs[i-60:i+1])
            vol_1h[i] = (max_60 - min_60) / min_60 * 100.0 if min_60 > 0 else 0
        if i >= 5:
            vol_sum_5m[i] = sum(vols[i-5:i+1])

    for i in range(n):
        bars[i]['ema9'] = ema9[i]
        bars[i]['ema21'] = ema21[i]
        bars[i]['ema50'] = ema50[i]
        bars[i]['rsi'] = rsi[i]
        bars[i]['change_15m'] = change_15m[i]
        bars[i]['vol_1h'] = vol_1h[i]
        bars[i]['vol_5m'] = vol_sum_5m[i]

calc_indicators(bars)
print("Indicators calculated.")
