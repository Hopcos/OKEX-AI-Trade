import json
from datetime import datetime

for bar, fname in [('1m', 'data/market_history/TRUMP_1m_full.json'),
                   ('5m', 'data/market_history/TRUMP_5m_full.json'),
                   ('15m', 'data/market_history/TRUMP_15m_full.json'),
                   ('1H', 'data/market_history/TRUMP_1h_full.json')]:
    with open(fname, 'r', encoding='utf-8') as f:
        data = json.load(f)
    data.sort(key=lambda x: int(x[0]))
    t_start = datetime.fromtimestamp(int(data[0][0])/1000)
    t_end = datetime.fromtimestamp(int(data[-1][0])/1000)
    p_start = float(data[0][1])
    p_end = float(data[-1][4])
    p_min = min(float(x[3]) for x in data)
    p_max = max(float(x[2]) for x in data)
    print(f"[{bar}] {len(data)} bars: {t_start} -> {t_end} | Start=${p_start:.4f}, End=${p_end:.4f}, Min=${p_min:.4f}, Max=${p_max:.4f}, Chg={(p_end-p_start)/p_start*100:+.2f}%")
