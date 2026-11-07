import json
import math
from datetime import datetime

with open('data/market_history/TRUMP_1m_full.json', 'r', encoding='utf-8') as f:
    c1m = json.load(f)

# Sort by timestamp ascending
c1m.sort(key=lambda x: int(x[0]))

bars = []
for c in c1m:
    # [ts, open, high, low, close, vol, volCcy, volCcyQuote, confirm]
    ts = int(c[0])
    o = float(c[1])
    h = float(c[2])
    l = float(c[3])
    close = float(c[4])
    vol = float(c[5])
    vol_usd = float(c[6])
    bars.append({'ts': ts, 'dt': datetime.utcfromtimestamp(ts/1000), 'o': o, 'h': h, 'l': l, 'c': close, 'v': vol, 'v_usd': vol_usd})

print(f"Total 1m bars: {len(bars)}")
print(f"Time range: {bars[0]['dt']} -> {bars[-1]['dt']}")
print(f"Start price: {bars[0]['o']:.4f}, End price: {bars[-1]['c']:.4f}, Min: {min(b['l'] for b in bars):.4f}, Max: {max(b['h'] for b in bars):.4f}")

# Calculate volatility & returns
ranges = [(b['h'] - b['l']) / b['l'] * 100 for b in bars]
ranges.sort()
avg_range = sum(ranges) / len(ranges)
median_range = ranges[len(ranges)//2]
p90_range = ranges[int(len(ranges)*0.9)]

print(f"1m High-Low Range: Avg={avg_range:.3f}% ({avg_range*100:.1f} bps), Median={median_range:.3f}% ({median_range*100:.1f} bps), 90%ile={p90_range:.3f}% ({p90_range*100:.1f} bps)")

# Daily volume
total_usd = sum(b['v_usd'] for b in bars)
days = (bars[-1]['ts'] - bars[0]['ts']) / (1000 * 86400)
print(f"Total days: {days:.2f}, Daily average turnover: ${total_usd/days:,.0f}")
