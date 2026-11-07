import json
import math
from datetime import datetime

# Load real TRUMP 1m and 5m candles
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

print(f"Loaded {len(bars_1m)} 1m bars and {len(bars_5m)} 5m bars.")
print(f"1m Date Range: {bars_1m[0]['dt']} -> {bars_1m[-1]['dt']}")
print(f"5m Date Range: {bars_5m[0]['dt']} -> {bars_5m[-1]['dt']}")
