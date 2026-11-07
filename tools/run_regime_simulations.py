import json
import math
from datetime import datetime

# Load data helper
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

def add_indicators(bars):
    n = len(bars)
    closes = [b['close'] for b in bars]
    highs = [b['high'] for b in bars]
    lows = [b['low'] for b in bars]
    vols = [b['vol_usd'] for b in bars]

    def calc_ema(series, period):
        res = [series[0]] * len(series)
        k = 2.0 / (period + 1.0)
        for i in range(1, len(series)):
            res[i] = k * series[i] + (1 - k) * res[i-1]
        return res

    ema9 = calc_ema(closes, 9)
    ema21 = calc_ema(closes, 21)
    ema50 = calc_ema(closes, 50)
    ema100 = calc_ema(closes, 100)
    ema200 = calc_ema(closes, 200)

    # RSI 14
    rsi = [50.0] * n
    gains = [0.0] * n
    losses = [0.0] * n
    for i in range(1, n):
        diff = closes[i] - closes[i-1]
        gains[i] = max(0.0, diff)
        losses[i] = max(0.0, -diff)

    period = 14
    avg_g = sum(gains[1:period+1]) / period if period < n else 0.001
    avg_l = sum(losses[1:period+1]) / period if period < n else 0.001
    for i in range(period, n):
        avg_g = (avg_g * (period - 1) + gains[i]) / period
        avg_l = (avg_l * (period - 1) + losses[i]) / period
        rs = avg_g / avg_l if avg_l > 0 else 1000.0
        rsi[i] = 100.0 - (100.0 / (1.0 + rs))

    # ATR 14
    atr = [0.0] * n
    for i in range(1, n):
        tr = max(highs[i] - lows[i], abs(highs[i] - closes[i-1]), abs(lows[i] - closes[i-1]))
        if i < 14:
            atr[i] = tr
        elif i == 14:
            atr[i] = sum(max(highs[j]-lows[j], abs(highs[j]-closes[j-1]), abs(lows[j]-closes[j-1])) for j in range(1, 15)) / 14
        else:
            atr[i] = (atr[i-1] * 13 + tr) / 14

    # Bollinger Bands (20, 2)
    bb_mid = [0.0] * n
    bb_up = [0.0] * n
    bb_low = [0.0] * n
    for i in range(n):
        if i >= 19:
            window = closes[i-19:i+1]
            m = sum(window) / 20.0
            var = sum((x - m)**2 for x in window) / 20.0
            s = math.sqrt(var)
            bb_mid[i] = m
            bb_up[i] = m + 2.0 * s
            bb_low[i] = m - 2.0 * s
        else:
            bb_mid[i] = closes[i]
            bb_up[i] = closes[i] * 1.01
            bb_low[i] = closes[i] * 0.99

    for i in range(n):
        bars[i]['ema9'] = ema9[i]
        bars[i]['ema21'] = ema21[i]
        bars[i]['ema50'] = ema50[i]
        bars[i]['ema100'] = ema100[i]
        bars[i]['ema200'] = ema200[i]
        bars[i]['rsi'] = rsi[i]
        bars[i]['atr'] = atr[i]
        bars[i]['bb_mid'] = bb_mid[i]
        bars[i]['bb_up'] = bb_up[i]
        bars[i]['bb_low'] = bb_low[i]

    return bars

class Engine:
    def __init__(self, bars, initial_capital=100.0, fee_bps=10.0, max_order_ratio=0.25, max_exposure_ratio=0.75):
        self.bars = bars
        self.initial_capital = initial_capital
        self.fee_rate = fee_bps / 10000.0
        self.cash = initial_capital
        self.holdings = 0.0
        self.avg_cost = 0.0
        self.entry_bar = 0
        self.trades = []
        self.equity_curve = []
        self.max_order_ratio = max_order_ratio
        self.max_exposure_ratio = max_exposure_ratio
        self.peak_equity = initial_capital
        self.max_dd = 0.0

    def total_equity(self, price):
        return self.cash + self.holdings * price

    def buy(self, price, amount_usd, bar_idx, reason=""):
        equity = self.total_equity(price)
        max_order = equity * self.max_order_ratio
        order_usd = min(amount_usd, max_order, self.cash)
        if (self.holdings * price + order_usd) > equity * self.max_exposure_ratio:
            order_usd = max(0.0, equity * self.max_exposure_ratio - self.holdings * price)
        if order_usd < 5.0:
            return False

        fee = order_usd * self.fee_rate
        net_usd = order_usd - fee
        qty = net_usd / price

        total_qty = self.holdings + qty
        total_cost = self.holdings * self.avg_cost + order_usd
        self.avg_cost = total_cost / total_qty
        self.holdings = total_qty
        self.cash -= order_usd
        self.entry_bar = bar_idx

        self.trades.append({
            'bar': bar_idx, 'time': self.bars[bar_idx]['dt'], 'side': 'BUY',
            'price': price, 'qty': qty, 'usd': order_usd, 'fee': fee, 'reason': reason
        })
        return True

    def sell(self, price, qty, bar_idx, reason=""):
        if qty <= 0 or self.holdings <= 0:
            return False
        qty = min(qty, self.holdings)
        gross_usd = qty * price
        fee = gross_usd * self.fee_rate
        net_usd = gross_usd - fee
        cost_basis = qty * self.avg_cost
        pnl = net_usd - cost_basis
        pnl_pct = (pnl / cost_basis) * 100.0 if cost_basis > 0 else 0

        self.cash += net_usd
        self.holdings -= qty
        if self.holdings < 1e-6:
            self.holdings = 0.0
            self.avg_cost = 0.0

        self.trades.append({
            'bar': bar_idx, 'time': self.bars[bar_idx]['dt'], 'side': 'SELL',
            'price': price, 'qty': qty, 'usd': gross_usd, 'fee': fee,
            'pnl': pnl, 'pnl_pct': pnl_pct, 'reason': reason
        })
        return True

    def update(self, price):
        eq = self.total_equity(price)
        self.equity_curve.append(eq)
        if eq > self.peak_equity:
            self.peak_equity = eq
        dd = (self.peak_equity - eq) / self.peak_equity * 100
        if dd > self.max_dd:
            self.max_dd = dd

    def report(self, name):
        final_eq = self.equity_curve[-1] if self.equity_curve else self.initial_capital
        ret = (final_eq - self.initial_capital) / self.initial_capital * 100
        sells = [t for t in self.trades if t['side'] == 'SELL']
        win_sells = [t for t in sells if t.get('pnl', 0) > 0]
        loss_sells = [t for t in sells if t.get('pnl', 0) <= 0]
        win_rate = (len(win_sells) / len(sells) * 100) if sells else 0
        total_pnl = sum(t.get('pnl', 0) for t in sells)
        total_fees = sum(t.get('fee', 0) for t in self.trades)
        
        start_p = self.bars[0]['open']
        end_p = self.bars[-1]['close']
        bench_ret = (end_p - start_p) / start_p * 100
        alpha = ret - bench_ret

        print(f"[{name}]")
        print(f"  Return: {ret:+.2f}% | Alpha: {alpha:+.2f}% (Bench: {bench_ret:+.2f}%)")
        print(f"  Final Equity: ${final_eq:.2f} (Start: ${self.initial_capital:.2f}) | Max DD: {self.max_dd:.2f}%")
        print(f"  Trades: {len(self.trades)} (Sells: {len(sells)}) | Win Rate: {win_rate:.1f}% ({len(win_sells)} W / {len(loss_sells)} L)")
        print(f"  Realized PnL: ${total_pnl:+.2f} | Fees Paid: ${total_fees:.2f}")
        return {
            'name': name, 'ret': ret, 'alpha': alpha, 'bench_ret': bench_ret,
            'final_eq': final_eq, 'max_dd': self.max_dd, 'win_rate': win_rate,
            'trades': len(self.trades), 'sells': len(sells), 'pnl': total_pnl, 'fees': total_fees
        }

# ==========================================
# TEST DIFFERENT ADVANCED TRADING STRATEGIES
# ==========================================

# Strategy A: Adaptive Mean-Reversion Scalp (RSI Oversold + Volume Exhaustion + Dynamic Trailing TP)
def test_strategy_A_mean_rev(bars, rsi_buy_thresh=26, tp_pct=0.7, sl_pct=1.5, trailing_activate=0.5, trailing_step=0.2):
    sim = Engine(bars, initial_capital=100.0, max_order_ratio=0.25, max_exposure_ratio=0.75)
    peak_p = 0.0
    for i in range(50, len(bars)):
        b = bars[i]
        price = b['close']

        if sim.holdings > 0:
            cost = sim.avg_cost
            pnl_pct = (price - cost) / cost * 100.0
            if price > peak_p:
                peak_p = price
            drawdown_from_peak = (peak_p - price) / peak_p * 100.0

            # 1. Trailing Stop Profit
            peak_pnl = (peak_p - cost) / cost * 100.0
            if peak_pnl >= trailing_activate and drawdown_from_peak >= trailing_step and pnl_pct >= 0.25:
                sim.sell(price, sim.holdings, i, reason=f"TrailingTP (+{pnl_pct:.2f}%)")
            # 2. Hard Take Profit
            elif pnl_pct >= tp_pct:
                sim.sell(price, sim.holdings, i, reason=f"HardTP (+{pnl_pct:.2f}%)")
            # 3. Stop Loss
            elif pnl_pct <= -sl_pct:
                sim.sell(price, sim.holdings, i, reason=f"StopLoss ({pnl_pct:.2f}%)")
            # 4. Breakeven time exit
            elif (i - sim.entry_bar) >= 30 and pnl_pct >= 0.25:
                sim.sell(price, sim.holdings, i, reason="TimeExit Breakeven")

        # Buy condition:
        # RSI oversold + rejection candle (close > low by at least 25% of candle range)
        if sim.holdings == 0:
            c_range = b['high'] - b['low']
            bounce = (b['close'] - b['low']) > 0.25 * c_range if c_range > 0 else True
            # Not in severe structural breakdown (e.g. not under EMA200 by more than 8%)
            not_freefall = price >= b['ema200'] * 0.94
            if b['rsi'] <= rsi_buy_thresh and bounce and not_freefall:
                sim.buy(price, 25.0, i, reason=f"RSI Oversold ({b['rsi']:.1f})")
                peak_p = price

        sim.update(price)
    return sim.report(f"Strategy A (RSI<{rsi_buy_thresh}, TP={tp_pct}%, SL={sl_pct}%)")

# Strategy B: Dynamic Grid / Volatility Harvester (Rebalance within dynamic bands)
def test_strategy_B_grid(bars, band_pct=1.0, grid_levels=3, tp_step=0.6, sl_max=3.5):
    sim = Engine(bars, initial_capital=100.0, max_order_ratio=0.30, max_exposure_ratio=0.85)
    last_buy_price = 0.0
    for i in range(50, len(bars)):
        b = bars[i]
        price = b['close']

        if sim.holdings > 0:
            cost = sim.avg_cost
            pnl_pct = (price - cost) / cost * 100.0
            
            # Take profit
            if pnl_pct >= tp_step:
                sim.sell(price, sim.holdings, i, reason=f"GridTP (+{pnl_pct:.2f}%)")
                last_buy_price = 0.0
            # Strict stop loss on total basket
            elif pnl_pct <= -sl_max:
                sim.sell(price, sim.holdings, i, reason=f"GridSL ({pnl_pct:.2f}%)")
                last_buy_price = 0.0
            # Averaging down if within limit
            elif price <= last_buy_price * (1.0 - band_pct/100.0) and sim.cash >= 20.0:
                if sim.buy(price, 20.0, i, reason=f"Grid Dip Buy"):
                    last_buy_price = price

        # First entry: When RSI < 35 or price touches BB lower
        if sim.holdings == 0:
            if b['rsi'] < 33 or b['low'] <= b['bb_low']:
                if sim.buy(price, 25.0, i, reason="Grid Init Entry"):
                    last_buy_price = price

        sim.update(price)
    return sim.report(f"Strategy B Grid (Step={band_pct}%, TP={tp_step}%, SL={sl_max}%)")

# Strategy C: Multi-Timeframe Trend & Pullback Sniper
# Uses 5m trend direction (EMA21 > EMA50) + 1m oversold pullback to enter in the direction of momentum
def test_strategy_C_trend_pullback(bars_1m, bars_5m):
    sim = Engine(bars_1m, initial_capital=100.0, max_order_ratio=0.30, max_exposure_ratio=0.80)
    
    # Map 5m timestamps to trend
    trend_5m = {}
    for b in bars_5m:
        is_up = b['ema9'] > b['ema21'] and b['close'] > b['ema50']
        is_strong = b['ema21'] > b['ema50']
        trend_5m[b['ts'] // 300000] = (is_up, is_strong, b['rsi'])

    peak_p = 0.0
    for i in range(50, len(bars_1m)):
        b = bars_1m[i]
        price = b['close']
        ts_5m_slot = b['ts'] // 300000
        up_5m, strong_5m, rsi_5m = trend_5m.get(ts_5m_slot, (False, False, 50.0))

        if sim.holdings > 0:
            cost = sim.avg_cost
            pnl_pct = (price - cost) / cost * 100.0
            if price > peak_p:
                peak_p = price
            dd_from_peak = (peak_p - price) / peak_p * 100.0

            # Profit lock
            if pnl_pct >= 0.8:
                sim.sell(price, sim.holdings, i, reason=f"TrendPullback TP (+{pnl_pct:.2f}%)")
            elif (peak_p - cost)/cost*100.0 >= 0.5 and dd_from_peak >= 0.2 and pnl_pct >= 0.25:
                sim.sell(price, sim.holdings, i, reason="TrendPullback TrailingExit")
            elif pnl_pct <= -1.2:
                sim.sell(price, sim.holdings, i, reason=f"TrendPullback SL ({pnl_pct:.2f}%)")
            elif not up_5m and pnl_pct >= 0.2:
                sim.sell(price, sim.holdings, i, reason="Trend5m Turned Bearish Exit")

        # Entry condition:
        # 5m is in uptrend (or at least RSI_5m > 45), 1m pulls back to oversold (RSI < 38)
        if sim.holdings == 0:
            if up_5m and b['rsi'] <= 36 and b['close'] > b['low']:
                sim.buy(price, 30.0, i, reason=f"5m Up + 1m Pullback (RSI={b['rsi']:.1f})")
                peak_p = price

        sim.update(price)
    return sim.report("Strategy C: Multi-Timeframe Trend Pullback Sniper")

print("Running simulations on real TRUMP datasets...")
bars_1m = add_indicators(load_data('data/market_history/TRUMP_1m_full.json'))
bars_5m = add_indicators(load_data('data/market_history/TRUMP_5m_full.json'))
bars_15m = add_indicators(load_data('data/market_history/TRUMP_15m_full.json'))
bars_1h = add_indicators(load_data('data/market_history/TRUMP_1h_full.json'))

print("\n--- 1. Testing Strategy A (Mean-Reversion with parameter sweep) ---")
test_strategy_A_mean_rev(bars_1m, rsi_buy_thresh=26, tp_pct=0.7, sl_pct=1.5)
test_strategy_A_mean_rev(bars_1m, rsi_buy_thresh=24, tp_pct=0.6, sl_pct=1.2, trailing_activate=0.4, trailing_step=0.15)
test_strategy_A_mean_rev(bars_1m, rsi_buy_thresh=22, tp_pct=0.8, sl_pct=1.2, trailing_activate=0.5, trailing_step=0.2)

print("\n--- 2. Testing Strategy B (Dynamic Volatility Grid) ---")
test_strategy_B_grid(bars_1m, band_pct=1.0, tp_step=0.6, sl_max=2.5)
test_strategy_B_grid(bars_1m, band_pct=1.2, tp_step=0.8, sl_max=2.0)

print("\n--- 3. Testing Strategy C (Multi-Timeframe Trend Pullback) ---")
test_strategy_C_trend_pullback(bars_1m, bars_5m)

print("\n--- 4. Testing Strategy on 5M and 15M Data (Longer Time Horizon) ---")
test_strategy_A_mean_rev(bars_5m, rsi_buy_thresh=26, tp_pct=1.5, sl_pct=2.5, trailing_activate=1.0, trailing_step=0.4)
test_strategy_A_mean_rev(bars_15m, rsi_buy_thresh=28, tp_pct=2.5, sl_pct=3.5, trailing_activate=1.8, trailing_step=0.6)
