import json
from datetime import datetime
from backtest_framework import load_data, add_indicators, Engine

bars_5m = add_indicators(load_data('data/market_history/TRUMP_5m_full.json'))

# Let's test our strategy on specific subsets of TRUMP data:
# 1. Strong Bullish / Rally period: Nov 01 to Nov 02 (Price: $1.58 -> $1.78, +12.6%)
# 2. Sideways / Rangebound period: Oct 30 to Nov 01 (Price: $1.55 -> $1.59, +2.5%)
# 3. Crash / Waterfall drop period: Nov 04 to Nov 05 (Price: $1.59 -> $1.26, -20.7%)

def slice_bars(bars, start_dt_str, end_dt_str):
    t_start = datetime.strptime(start_dt_str, "%Y-%m-%d %H:%M")
    t_end = datetime.strptime(end_dt_str, "%Y-%m-%d %H:%M")
    return [b for b in bars if t_start <= b['dt'] <= t_end]

bars_rally = slice_bars(bars_5m, "2026-11-01 00:00", "2026-11-02 23:59")
bars_range = slice_bars(bars_5m, "2026-10-30 00:00", "2026-11-01 00:00")
bars_crash = slice_bars(bars_5m, "2026-11-04 00:00", "2026-11-05 23:59")

print(f"Rally bars: {len(bars_rally)} bars | Start: ${bars_rally[0]['open']:.4f} -> End: ${bars_rally[-1]['close']:.4f} ({(bars_rally[-1]['close']-bars_rally[0]['open'])/bars_rally[0]['open']*100:+.2f}%)")
print(f"Range bars: {len(bars_range)} bars | Start: ${bars_range[0]['open']:.4f} -> End: ${bars_range[-1]['close']:.4f} ({(bars_range[-1]['close']-bars_range[0]['open'])/bars_range[0]['open']*100:+.2f}%)")
print(f"Crash bars: {len(bars_crash)} bars | Start: ${bars_crash[0]['open']:.4f} -> End: ${bars_crash[-1]['close']:.4f} ({(bars_crash[-1]['close']-bars_crash[0]['open'])/bars_crash[0]['open']*100:+.2f}%)")

def run_adaptive_test(bars, name):
    sim = Engine(bars, initial_capital=100.0, fee_bps=10.0, max_order_ratio=0.30, max_exposure_ratio=0.85)
    peak_p = 0.0

    for i in range(20, len(bars)):
        b = bars[i]
        price = b['close']
        is_bull = b['ema9'] > b['ema21'] and b['close'] > b['ema21']
        is_bear = b['ema9'] < b['ema21'] and b['close'] < b['ema21']

        if sim.holdings > 0:
            cost = sim.avg_cost
            pnl_pct = (price - cost) / cost * 100.0
            if price > peak_p:
                peak_p = price
            peak_pnl = (peak_p - cost) / cost * 100.0
            dd = (peak_p - price) / peak_p * 100.0

            if peak_pnl >= 0.8 and dd >= 0.3 and pnl_pct >= 0.4:
                sim.sell(price, sim.holdings, i, reason="TrailingTP")
            elif pnl_pct >= 1.5:
                sim.sell(price, sim.holdings, i, reason="HardTP")
            elif pnl_pct <= -1.8:
                sim.sell(price, sim.holdings, i, reason="StopLoss")
            elif is_bear and pnl_pct >= 0.25:
                sim.sell(price, sim.holdings, i, reason="BearishProtect")

        if sim.holdings == 0:
            candle_range = b['high'] - b['low']
            bounce = (b['close'] - b['low']) >= 0.25 * candle_range if candle_range > 0 else True
            # Trend filter
            if is_bull and b['rsi'] <= 50 and bounce:
                sim.buy(price, 30.0, i, reason="TrendDipBuy")
                peak_p = price

        sim.update(price)

    return sim.report(name)

print("\n--- RESULTS ACROSS REGIMES ---")
run_adaptive_test(bars_rally, "1. Rally Regime")
run_adaptive_test(bars_range, "2. Rangebound Regime")
run_adaptive_test(bars_crash, "3. Crash Regime")
