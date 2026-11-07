import json
import math
from datetime import datetime

# Load real TRUMP candle datasets
with open('data/market_history/TRUMP_1m_full.json', 'r', encoding='utf-8') as f:
    raw_1m = json.load(f)
raw_1m.sort(key=lambda x: int(x[0]))

bars_1m = []
for c in raw_1m:
    bars_1m.append({
        'ts': int(c[0]),
        'open': float(c[1]),
        'high': float(c[2]),
        'low': float(c[3]),
        'close': float(c[4]),
        'vol': float(c[5]),
        'vol_usd': float(c[6]),
    })

print(f"Loaded {len(bars_1m)} 1-minute bars of TRUMP-USDT.")

# Simulate basic buy & hold or naive strategy to set baseline
start_p = bars_1m[0]['open']
end_p = bars_1m[-1]['close']
print(f"TRUMP Baseline (Buy & Hold): Start=${start_p:.4f}, End=${end_p:.4f}, Return={(end_p-start_p)/start_p*100:.2f}%")
