import json
import math
from datetime import datetime

# Load 1m and 5m candles
with open('data/market_history/TRUMP_1m_full.json', 'r', encoding='utf-8') as f:
    raw_1m = json.load(f)
raw_1m.sort(key=lambda x: int(x[0]))

bars_1m = []
for c in raw_1m:
    bars_1m.append({
        'ts': int(c[0]),
        'dt': datetime.fromtimestamp(int(c[0])/1000),
        'open': float(c[1]),
        'high': float(c[2]),
        'low': float(c[3]),
        'close': float(c[4]),
        'vol': float(c[5]),
        'vol_usd': float(c[6]),
    })

with open('data/market_history/TRUMP_5m_full.json', 'r', encoding='utf-8') as f:
    raw_5m = json.load(f)
raw_5m.sort(key=lambda x: int(x[0]))

bars_5m = []
for c in raw_5m:
    bars_5m.append({
        'ts': int(c[0]),
        'dt': datetime.fromtimestamp(int(c[0])/1000),
        'open': float(c[1]),
        'high': float(c[2]),
        'low': float(c[3]),
        'close': float(c[4]),
        'vol': float(c[5]),
        'vol_usd': float(c[6]),
    })

print(f"Data summary:")
print(f"1m: {len(bars_1m)} bars, {bars_1m[0]['dt']} -> {bars_1m[-1]['dt']}")
print(f"5m: {len(bars_5m)} bars, {bars_5m[0]['dt']} -> {bars_5m[-1]['dt']}")

# Check 5m price change and find large positive swings (opportunities)
swings = []
for i in range(12, len(bars_5m)):
    # 1 hour return (12 * 5m)
    p_now = bars_5m[i]['close']
    p_prev = bars_5m[i-12]['close']
    ret = (p_now - p_prev) / p_prev * 100
    if ret > 3.0:
        swings.append((bars_5m[i]['dt'], ret, p_now))

print(f"Found {len(swings)} 5m bars where 1-hour pump exceeded +3.0%.")
