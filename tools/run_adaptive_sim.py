import json
import math
from datetime import datetime
from backtest_framework import load_data, add_indicators, Engine

bars_1m = add_indicators(load_data('data/market_history/TRUMP_1m_full.json'))
bars_5m = add_indicators(load_data('data/market_history/TRUMP_5m_full.json'))
bars_15m = add_indicators(load_data('data/market_history/TRUMP_15m_full.json'))

print(f"Loaded datasets: 1m={len(bars_1m)}, 5m={len(bars_5m)}, 15m={len(bars_15m)}")

# Map 5m and 15m indicators into fast lookup dicts by timestamp bucket
map_5m = {}
for b in bars_5m:
    bucket = b['ts'] // 300000
    map_5m[bucket] = b

map_15m = {}
for b in bars_15m:
    bucket = b['ts'] // 900000
    map_15m[bucket] = b

# Let's test a comprehensive Adaptive Dynamic Strategy:
# 1. Regime Identification:
#    - Bullish: 15m EMA9 > EMA21 and 5m EMA9 > EMA21 -> Aggressive trend continuation & dip buying
#    - Range-bound: 15m ADX/volatility low, EMA flat -> Mean-reversion grid / BB bounce
#    - Bearish / Waterfall: 15m EMA9 < EMA21 & Price < EMA50 -> STRICT DEFENSE (no buying, hold USD cash, or only sniper quick scalp on extreme panic oversold with instant trailing exit)
# 2. Execution rules:
#    - Profit target: dynamic (min 35 bps to 80 bps net)
#    - Trailing take profit: locks gains once +0.4% is reached
#    - Tight risk stop: 1.0% - 1.2% maximum loss

def run_adaptive_strategy(
    bars,
    tp_min_bps=40,
    trail_activate_pct=0.45,
    trail_dist_pct=0.18,
    hard_sl_pct=1.1,
    bear_trade_allowed=False,
    max_order_ratio=0.30,
    max_exposure_ratio=0.85
):
    sim = Engine(bars, initial_capital=100.0, fee_bps=10.0, max_order_ratio=max_order_ratio, max_exposure_ratio=max_exposure_ratio)
    peak_p = 0.0

    for i in range(60, len(bars)):
        b = bars[i]
        price = b['close']
        ts_5m_bucket = b['ts'] // 300000
        ts_15m_bucket = b['ts'] // 900000
        
        b5 = map_5m.get(ts_5m_bucket)
        b15 = map_15m.get(ts_15m_bucket)

        # Regimes
        is_15m_bull = b15 is not None and (b15['ema9'] > b15['ema21'] and b15['close'] > b15['ema50'])
        is_15m_bear = b15 is not None and (b15['ema9'] < b15['ema21'] and b15['close'] < b15['ema50'])
        is_5m_bull = b5 is not None and (b5['ema9'] > b5['ema21'] and b5['close'] > b5['ema21'])
        is_5m_bear = b5 is not None and (b5['ema9'] < b5['ema21'] and b5['close'] < b5['ema21'])

        # --- POSITION MANAGEMENT & EXITS ---
        if sim.holdings > 0:
            cost = sim.avg_cost
            pnl_pct = (price - cost) / cost * 100.0
            if price > peak_p:
                peak_p = price
            peak_pnl = (peak_p - cost) / cost * 100.0
            dd_from_peak = (peak_p - price) / peak_p * 100.0

            # Exit 1: Trailing Profit Lock
            if peak_pnl >= trail_activate_pct and dd_from_peak >= trail_dist_pct and pnl_pct >= (tp_min_bps / 100.0):
                sim.sell(price, sim.holdings, i, reason=f"TrailingTP (+{pnl_pct:.2f}%)")
            # Exit 2: High target take-profit (+1.2%)
            elif pnl_pct >= 1.2:
                sim.sell(price, sim.holdings, i, reason=f"HighTargetTP (+{pnl_pct:.2f}%)")
            # Exit 3: Hard Stop-Loss
            elif pnl_pct <= -hard_sl_pct:
                sim.sell(price, sim.holdings, i, reason=f"HardSL ({pnl_pct:.2f}%)")
            # Exit 4: Trend reversal emergency exit (if 5m turns strictly bearish and position has at least fee covered)
            elif is_5m_bear and pnl_pct >= 0.22:
                sim.sell(price, sim.holdings, i, reason="5m Bearish Reversal TakeProfit")
            # Exit 5: Quick Breakeven Time Decay (held > 30 bars, price not moving up)
            elif (i - sim.entry_bar) >= 30 and pnl_pct >= 0.20:
                sim.sell(price, sim.holdings, i, reason="Time Decay Breakeven")

        # --- ENTRY CONDITIONS ---
        if sim.holdings == 0:
            # Condition A: Bullish Regime Continuation (15m neutral/bull + 5m bull + 1m oversold dip)
            if (is_15m_bull or not is_15m_bear) and (is_5m_bull or (b5 and b5['rsi'] > 48)):
                # 1m entry on micro-pullback
                if b['rsi'] <= 38 and b['close'] > b['low'] and b['close'] >= b['ema200'] * 0.98:
                    sim.buy(price, 30.0, i, reason=f"Bull Pullback (RSI={b['rsi']:.1f})")
                    peak_p = price

            # Condition B: High-Volume Momentum Breakout (even in neutral/rebound regime)
            elif not is_15m_bear:
                hh_15 = max(bars[j]['high'] for j in range(i-15, i))
                avg_vol_15 = sum(bars[j]['vol_usd'] for j in range(i-15, i)) / 15.0
                if price > hh_15 and b['vol_usd'] > avg_vol_15 * 2.2 and b['rsi'] > 55 and b['rsi'] < 72:
                    sim.buy(price, 30.0, i, reason="Momentum Breakout")
                    peak_p = price

            # Condition C: Extreme Panic Capitulation Rebound (if bear trading allowed)
            elif bear_trade_allowed and is_15m_bear:
                # Extreme oversold bounce: RSI < 20 and severe rejection
                if b['rsi'] <= 18 and (b['close'] - b['low']) > 0.4 * (b['high'] - b['low']):
                    sim.buy(price, 20.0, i, reason="Panic Capitulation Rebound")
                    peak_p = price

        sim.update(price)

    return sim.report(f"Adaptive Strategy (tp_min={tp_min_bps}bps, trail_act={trail_activate_pct}%, sl={hard_sl_pct}%, bear_allowed={bear_trade_allowed})")

print("\n--- Testing Adaptive Strategy Variations on 1m Real TRUMP data ---")
run_adaptive_strategy(bars_1m, tp_min_bps=35, trail_activate_pct=0.40, trail_dist_pct=0.15, hard_sl_pct=1.0, bear_trade_allowed=False)
run_adaptive_strategy(bars_1m, tp_min_bps=40, trail_activate_pct=0.45, trail_dist_pct=0.18, hard_sl_pct=1.1, bear_trade_allowed=False)
run_adaptive_strategy(bars_1m, tp_min_bps=50, trail_activate_pct=0.50, trail_dist_pct=0.20, hard_sl_pct=1.2, bear_trade_allowed=False)
run_adaptive_strategy(bars_1m, tp_min_bps=50, trail_activate_pct=0.50, trail_dist_pct=0.20, hard_sl_pct=1.2, bear_trade_allowed=True)
