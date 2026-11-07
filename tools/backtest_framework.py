import json
import math
from datetime import datetime

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
        bars[i]['ema200'] = ema200[i]
        bars[i]['rsi'] = rsi[i]
        bars[i]['atr'] = atr[i]
        bars[i]['bb_mid'] = bb_mid[i]
        bars[i]['bb_up'] = bb_up[i]
        bars[i]['bb_low'] = bb_low[i]

    return bars

class Engine:
    def __init__(self, bars, initial_capital=100.0, fee_bps=10.0, max_order_ratio=0.35, max_exposure_ratio=0.90):
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
