import json
import math
from datetime import datetime

# Load real TRUMP datasets
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

print(f"Loaded {len(bars)} 1m bars.")
print(f"Time: {bars[0]['dt']} -> {bars[-1]['dt']}")
print(f"Start price: ${bars[0]['open']:.4f}, End price: ${bars[-1]['close']:.4f} (Drop: {(bars[-1]['close']-bars[0]['open'])/bars[0]['open']*100:.2f}%)")

fee_rate = 0.0010  # 10 bps maker / taker (OKX spot standard)
roundtrip_fee = 0.0020 # 20 bps

# Let's inspect price behavior over this period
# The asset dropped by -19.2% over these 3 days.
# Any naive buy-and-hold loses -19.2%.
# If a strategy can generate positive return (or beat the asset significantly while generating net profit during volatile swings), what are the winning dynamics?
