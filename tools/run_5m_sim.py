import json
import math
from datetime import datetime
from backtest_framework import load_data, add_indicators, Engine

# Load longer horizon: 5m dataset (2900 bars = ~10 days) and 15m dataset (2900 bars = ~30 days)
bars_5m = add_indicators(load_data('data/market_history/TRUMP_5m_full.json'))
bars_15m = add_indicators(load_data('data/market_history/TRUMP_15m_full.json'))

print("=== LONG HORIZON DATA SUMMARY ===")
print(f"5m bars: {len(bars_5m)} bars from {bars_5m[0]['dt']} to {bars_5m[-1]['dt']}")
print(f"5m Price: ${bars_5m[0]['open']:.4f} -> ${bars_5m[-1]['close']:.4f} ({(bars_5m[-1]['close']-bars_5m[0]['open'])/bars_5m[0]['open']*100:+.2f}%)")

print(f"15m bars: {len(bars_15m)} bars from {bars_15m[0]['dt']} to {bars_15m[-1]['dt']}")
print(f"15m Price: ${bars_15m[0]['open']:.4f} -> ${bars_15m[-1]['close']:.4f} ({(bars_15m[-1]['close']-bars_15m[0]['open'])/bars_15m[0]['open']*100:+.2f}%)")

# Let's test the 5m horizon strategy with adaptive parameters:
# In 5m timeframe:
# Each bar is 5 minutes. OKX fee is 10 bps per side (20 bps roundtrip).
# Suitable profit targets are 0.8% - 1.8% with trailing take profit and 1.5% - 2.5% stop loss.

def run_5m_strategy(bars, tp_pct=1.2, trail_act=0.8, trail_dist=0.3, sl_pct=2.0, use_trend_filter=True):
    sim = Engine(bars, initial_capital=100.0, fee_bps=10.0, max_order_ratio=0.30, max_exposure_ratio=0.85)
    peak_p = 0.0

    for i in range(50, len(bars)):
        b = bars[i]
        price = b['close']

        is_bull = b['ema9'] > b['ema21'] and b['close'] > b['ema50']
        is_bear = b['ema9'] < b['ema21'] and b['close'] < b['ema50']

        if sim.holdings > 0:
            cost = sim.avg_cost
            pnl_pct = (price - cost) / cost * 100.0
            if price > peak_p:
                peak_p = price
            peak_pnl = (peak_p - cost) / cost * 100.0
            dd_from_peak = (peak_p - price) / peak_p * 100.0

            # 1. Trailing profit exit
            if peak_pnl >= trail_act and dd_from_peak >= trail_dist and pnl_pct >= 0.4:
                sim.sell(price, sim.holdings, i, reason=f"5m TrailingTP (+{pnl_pct:.2f}%)")
            # 2. Hard profit target
            elif pnl_pct >= tp_pct:
                sim.sell(price, sim.holdings, i, reason=f"5m HardTP (+{pnl_pct:.2f}%)")
            # 3. Stop loss
            elif pnl_pct <= -sl_pct:
                sim.sell(price, sim.holdings, i, reason=f"5m StopLoss ({pnl_pct:.2f}%)")
            # 4. Bearish trend breakdown exit (cut early if trend turns completely against us)
            elif is_bear and pnl_pct >= 0.25:
                sim.sell(price, sim.holdings, i, reason="5m Bearish Reversal TakeProfit")
            elif (i - sim.entry_bar) >= 24 and pnl_pct >= 0.3: # 2 hours time decay
                sim.sell(price, sim.holdings, i, reason="5m Time Decay Breakeven")

        if sim.holdings == 0:
            # Entry:
            # 1. Trend alignment: either EMA9 > EMA21 or price recovering above EMA21
            # 2. Pullback / rejection: RSI between 30 and 48
            candle_range = b['high'] - b['low']
            bounce = (b['close'] - b['low']) >= 0.3 * candle_range if candle_range > 0 else True

            if use_trend_filter:
                entry_ok = (is_bull or b['ema9'] > b['ema21']) and b['rsi'] <= 46 and bounce
            else:
                entry_ok = b['rsi'] <= 35 and bounce

            if entry_ok:
                sim.buy(price, 30.0, i, reason=f"5m Buy (RSI={b['rsi']:.1f})")
                peak_p = price

        sim.update(price)

    return sim.report(f"5m Horizon (TP={tp_pct}%, Trail={trail_act}%, SL={sl_pct}%, TrendFilter={use_trend_filter})")

print("\n--- 5M Strategy Parameter Sweep ---")
run_5m_strategy(bars_5m, tp_pct=1.0, trail_act=0.6, trail_dist=0.25, sl_pct=1.8, use_trend_filter=True)
run_5m_strategy(bars_5m, tp_pct=1.2, trail_act=0.8, trail_dist=0.30, sl_pct=2.0, use_trend_filter=True)
run_5m_strategy(bars_5m, tp_pct=1.5, trail_act=0.9, trail_dist=0.35, sl_pct=2.2, use_trend_filter=True)
run_5m_strategy(bars_5m, tp_pct=1.2, trail_act=0.8, trail_dist=0.30, sl_pct=2.0, use_trend_filter=False)
