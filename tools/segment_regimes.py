import json
import math
from datetime import datetime

# Load all 5m and 15m bars to identify sub-periods:
# Period 1: Uptrend / Rebound rally
# Period 2: Consolidation / Rangebound
# Period 3: Trend breakdown / Crash

def load_data(filepath):
    with open(filepath, 'r', encoding='utf-8') as f:
        raw = json.load(f)
    raw.sort(key=lambda x: int(x[0]))
    bars = []
    for c in raw:
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
    return bars

bars_5m = load_data('data/market_history/TRUMP_5m_full.json')
bars_15m = load_data('data/market_history/TRUMP_15m_full.json')

# Find 24h rolling returns across 5m bars
print(f"Total 5m bars: {len(bars_5m)}")
day_bars = 288 # 288 * 5m = 24h
sub_periods = []

for i in range(day_bars, len(bars_5m), day_bars // 2):
    p_start = bars_5m[i-day_bars]['open']
    p_end = bars_5m[i]['close']
    ret = (p_end - p_start) / p_start * 100
    t_start = bars_5m[i-day_bars]['dt']
    t_end = bars_5m[i]['dt']
    sub_periods.append((t_start, t_end, ret, p_start, p_end, i-day_bars, i))

print("=== 24-HOUR ROLLING REGIMES IN TRUMP-USDT ===")
for p in sub_periods:
    regime = "UPTREND (+)" if p[2] > 2.0 else ("DOWNTREND (-)" if p[2] < -2.0 else "RANGE (0)")
    print(f"[{regime:13}] {p[0].strftime('%m-%d %H:%M')} -> {p[1].strftime('%m-%d %H:%M')} | Ret: {p[2]:+6.2f}% (${p[3]:.4f} -> ${p[4]:.4f})")
