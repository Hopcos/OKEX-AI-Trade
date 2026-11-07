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

    for i in range(n):
        bars[i]['ema9'] = ema9[i]
        bars[i]['ema21'] = ema21[i]
        bars[i]['ema50'] = ema50[i]
        bars[i]['ema100'] = ema100[i]
        bars[i]['ema200'] = ema200[i]
        bars[i]['rsi'] = rsi[i]
        bars[i]['atr'] = atr[i]

    return bars

class Engine:
    def __init__(self, bars, initial_capital=100.0, fee_bps=10.0, max_order_ratio=0.30, max_exposure_ratio=0.85):
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
        print(f"  Final Equity: ${final_eq:.2f} | Max DD: {self.max_dd:.2f}%")
        print(f"  Trades: {len(self.trades)} (Sells: {len(sells)}) | Win Rate: {win_rate:.1f}% ({len(win_sells)}W/{len(loss_sells)}L)")
        print(f"  Realized PnL: ${total_pnl:+.2f} | Fees: ${total_fees:.2f}")
        return {
            'ret': ret, 'alpha': alpha, 'dd': self.max_dd, 'win_rate': win_rate,
            'pnl': total_pnl, 'fees': total_fees, 'trades': len(self.trades)
        }

bars_1m = add_indicators(load_data('data/market_history/TRUMP_1m_full.json'))
bars_5m = add_indicators(load_data('data/market_history/TRUMP_5m_full.json'))
bars_15m = add_indicators(load_data('data/market_history/TRUMP_15m_full.json'))

# Map higher timeframe trends
trend_15m = {}
for b in bars_15m:
    # 15m slot = ts // 900000
    is_bull = b['ema9'] > b['ema21'] and b['close'] > b['ema50']
    is_bear = b['ema9'] < b['ema21'] and b['close'] < b['ema50']
    trend_15m[b['ts'] // 900000] = {
        'bull': is_bull, 'bear': is_bear, 'ema9': b['ema9'], 'ema21': b['ema21'],
        'rsi': b['rsi'], 'close': b['close']
    }

trend_5m = {}
for b in bars_5m:
    is_bull = b['ema9'] > b['ema21'] and b['close'] > b['ema21']
    is_bear = b['ema9'] < b['ema21'] and b['close'] < b['ema21']
    trend_5m[b['ts'] // 300000] = {
        'bull': is_bull, 'bear': is_bear, 'rsi': b['rsi']
    }

# STRATEGY 1: Triple-Screen Trend & Pullback Sniper (15m Filter + 5m Trend + 1m Entry)
def run_triple_screen(bars_1m, min_profit_bps=50, stop_loss_pct=1.2, trail_activate=0.5, trail_dist=0.2):
    sim = Engine(bars_1m, initial_capital=100.0, max_order_ratio=0.30, max_exposure_ratio=0.85)
    peak_p = 0.0

    for i in range(60, len(bars_1m)):
        b = bars_1m[i]
        price = b['close']
        ts_15m = b['ts'] // 900000
        ts_5m = b['ts'] // 300000
        t15 = trend_15m.get(ts_15m, {'bull': False, 'bear': True, 'rsi': 50.0})
        t5 = trend_5m.get(ts_5m, {'bull': False, 'bear': True, 'rsi': 50.0})

        if sim.holdings > 0:
            cost = sim.avg_cost
            pnl_pct = (price - cost) / cost * 100.0
            if price > peak_p:
                peak_p = price
            dd_from_peak = (peak_p - price) / peak_p * 100.0
            peak_pnl = (peak_p - cost) / cost * 100.0

            # 1. Trailing profit exit
            if peak_pnl >= trail_activate and dd_from_peak >= trail_dist and pnl_pct >= (min_profit_bps / 100.0):
                sim.sell(price, sim.holdings, i, reason=f"TrailingTP (+{pnl_pct:.2f}%)")
            # 2. Hard profit target (e.g. +1.2%)
            elif pnl_pct >= 1.2:
                sim.sell(price, sim.holdings, i, reason=f"HardTP (+{pnl_pct:.2f}%)")
            # 3. Stop loss
            elif pnl_pct <= -stop_loss_pct:
                sim.sell(price, sim.holdings, i, reason=f"StopLoss ({pnl_pct:.2f}%)")
            # 4. Regime change exit (if 5m turns strictly bearish and we have covered fee + small profit)
            elif t5['bear'] and pnl_pct >= 0.25:
                sim.sell(price, sim.holdings, i, reason="5m Bearish Protect")
            # 5. Time decay (held > 45 mins with small profit)
            elif (i - sim.entry_bar) >= 45 and pnl_pct >= 0.22:
                sim.sell(price, sim.holdings, i, reason="Time Decay Breakeven")

        # ENTRY:
        # Screen 1: 15m is NOT bear (neutral or bull)
        # Screen 2: 5m is bull OR RSI_5m > 45
        # Screen 3: 1m pullback: RSI <= 36 and current candle rejected low
        if sim.holdings == 0:
            screen1 = not t15['bear'] or t15['rsi'] > 48
            screen2 = t5['bull'] or t5['rsi'] > 46
            c_range = b['high'] - b['low']
            bounce = (b['close'] - b['low']) >= 0.20 * c_range if c_range > 0 else True
            screen3 = b['rsi'] <= 38 and bounce

            if screen1 and screen2 and screen3:
                sim.buy(price, 30.0, i, reason=f"Sniper Entry (RSI={b['rsi']:.1f})")
                peak_p = price

        sim.update(price)
    return sim.report(f"Triple-Screen Sniper (MinProfit={min_profit_bps}bp, SL={stop_loss_pct}%)")

# STRATEGY 2: Dynamic Donchian Breakout + Volume Wave (Momentum Scalping)
def run_momentum_breakout(bars_1m, lookback=15, min_profit_pct=0.8, stop_loss_pct=1.0):
    sim = Engine(bars_1m, initial_capital=100.0, max_order_ratio=0.30, max_exposure_ratio=0.85)
    peak_p = 0.0

    for i in range(60, len(bars_1m)):
        b = bars_1m[i]
        price = b['close']
        ts_15m = b['ts'] // 900000
        t15 = trend_15m.get(ts_15m, {'bull': False, 'bear': True, 'rsi': 50.0})

        if sim.holdings > 0:
            cost = sim.avg_cost
            pnl_pct = (price - cost) / cost * 100.0
            if price > peak_p:
                peak_p = price
            dd_from_peak = (peak_p - price) / peak_p * 100.0

            if pnl_pct >= min_profit_pct:
                sim.sell(price, sim.holdings, i, reason=f"Breakout TP (+{pnl_pct:.2f}%)")
            elif (peak_p - cost)/cost*100.0 >= 0.5 and dd_from_peak >= 0.25 and pnl_pct >= 0.25:
                sim.sell(price, sim.holdings, i, reason=f"Breakout Trailing (+{pnl_pct:.2f}%)")
            elif pnl_pct <= -stop_loss_pct:
                sim.sell(price, sim.holdings, i, reason=f"Breakout SL ({pnl_pct:.2f}%)")
            elif b['close'] < b['ema9'] and pnl_pct >= 0.3:
                sim.sell(price, sim.holdings, i, reason="EMA9 Cross Sell")

        if sim.holdings == 0:
            # 15-bar high
            hh = max(bars_1m[j]['high'] for j in range(i-lookback, i))
            avg_vol = sum(bars_1m[j]['vol_usd'] for j in range(i-lookback, i)) / lookback
            # Vol spike > 2x and price breakout
            if price > hh and b['vol_usd'] > avg_vol * 2.0 and not t15['bear'] and b['rsi'] < 72:
                sim.buy(price, 30.0, i, reason="Donchian Vol Breakout")
                peak_p = price

        sim.update(price)
    return sim.report(f"Momentum Vol Breakout (Lookback={lookback}, TP={min_profit_pct}%, SL={stop_loss_pct}%)")

print("=== RUNNING DETAILED TRUMP SIMULATIONS ===")
run_triple_screen(bars_1m, min_profit_bps=40, stop_loss_pct=1.0, trail_activate=0.5, trail_dist=0.2)
run_triple_screen(bars_1m, min_profit_bps=50, stop_loss_pct=1.2, trail_activate=0.6, trail_dist=0.25)
run_triple_screen(bars_1m, min_profit_bps=60, stop_loss_pct=1.5, trail_activate=0.8, trail_dist=0.3)

run_momentum_breakout(bars_1m, lookback=15, min_profit_pct=0.8, stop_loss_pct=1.0)
run_momentum_breakout(bars_1m, lookback=20, min_profit_pct=1.0, stop_loss_pct=1.2)
