import json
import math
from datetime import datetime

# Load real 1m candles for TRUMP-USDT
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

n = len(bars)
start_price = bars[0]['open']
end_price = bars[-1]['close']
asset_return = (end_price - start_price) / start_price * 100
print(f"TRUMP-USDT 1m Bars: {n} bars from {bars[0]['dt']} to {bars[-1]['dt']}")
print(f"Asset Buy & Hold: ${start_price:.4f} -> ${end_price:.4f} ({asset_return:+.2f}%)")

FEE_RATE = 0.0010  # 10 bps per side, 20 bps roundtrip

# Calculate Technical Indicators
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
avg_g = sum(gains[1:period+1]) / period
avg_l = sum(losses[1:period+1]) / period
for i in range(period, n):
    avg_g = (avg_g * (period - 1) + gains[i]) / period
    avg_l = (avg_l * (period - 1) + losses[i]) / period
    rs = avg_g / avg_l if avg_l > 0 else 1000
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
        mean = sum(window) / 20.0
        var = sum((x - mean)**2 for x in window) / 20.0
        std = math.sqrt(var)
        bb_mid[i] = mean
        bb_up[i] = mean + 2.0 * std
        bb_low[i] = mean - 2.0 * std
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

class BacktestSimulator:
    def __init__(self, initial_capital=100.0, max_order_ratio=0.25, max_exposure_ratio=0.80):
        self.initial_capital = initial_capital
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

    def total_equity(self, current_price):
        return self.cash + self.holdings * current_price

    def buy(self, price, amount_usd, bar_idx, reason=""):
        equity = self.total_equity(price)
        max_order = equity * self.max_order_ratio
        order_usd = min(amount_usd, max_order, self.cash)
        if (self.holdings * price + order_usd) > equity * self.max_exposure_ratio:
            order_usd = max(0.0, equity * self.max_exposure_ratio - self.holdings * price)
        
        if order_usd < 5.0:
            return False

        fee = order_usd * FEE_RATE
        net_usd = order_usd - fee
        qty = net_usd / price

        total_qty = self.holdings + qty
        total_cost = self.holdings * self.avg_cost + order_usd
        self.avg_cost = total_cost / total_qty
        self.holdings = total_qty
        self.cash -= order_usd
        self.entry_bar = bar_idx

        self.trades.append({
            'bar': bar_idx, 'time': bars[bar_idx]['dt'], 'side': 'BUY',
            'price': price, 'qty': qty, 'usd': order_usd, 'fee': fee, 'reason': reason
        })
        return True

    def sell(self, price, qty, bar_idx, reason=""):
        if qty <= 0 or self.holdings <= 0:
            return False
        qty = min(qty, self.holdings)
        gross_usd = qty * price
        fee = gross_usd * FEE_RATE
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
            'bar': bar_idx, 'time': bars[bar_idx]['dt'], 'side': 'SELL',
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
        final_eq = self.equity_curve[-1]
        ret = (final_eq - self.initial_capital) / self.initial_capital * 100
        sells = [t for t in self.trades if t['side'] == 'SELL']
        win_sells = [t for t in sells if t.get('pnl', 0) > 0]
        loss_sells = [t for t in sells if t.get('pnl', 0) <= 0]
        win_rate = (len(win_sells) / len(sells) * 100) if sells else 0
        total_pnl = sum(t.get('pnl', 0) for t in sells)
        total_fees = sum(t.get('fee', 0) for t in self.trades)
        
        print(f"=== {name} ===")
        print(f"Return: {ret:+.2f}% | Final Equity: ${final_eq:.2f} (Start: ${self.initial_capital:.2f})")
        print(f"Benchmark Return (TRUMP): {asset_return:+.2f}% | Alpha: {ret - asset_return:+.2f}%")
        print(f"Max Drawdown: {self.max_dd:.2f}% | Total Trades: {len(self.trades)} (Sells: {len(sells)})")
        print(f"Win Rate: {win_rate:.1f}% ({len(win_sells)} W / {len(loss_sells)} L)")
        print(f"Realized PnL: ${total_pnl:+.2f} | Total Fees: ${total_fees:.2f}")
        print("-" * 50)
        return ret, self.max_dd, win_rate

# Model 1: Current System Baseline (Naive Scalping + PositionManager)
def run_model1_baseline():
    sim = BacktestSimulator(initial_capital=100.0)
    for i in range(50, n):
        b = bars[i]
        price = b['close']
        
        # PositionManager check for existing holdings
        if sim.holdings > 0:
            cost = sim.avg_cost
            # min profit sell price: covering fees + 15 bps
            min_sell_px = cost * (1 + 0.0015) / (1 - FEE_RATE)
            loss_pct = (cost - price) / cost * 100.0
            
            # 1. Take Profit
            if b['high'] >= min_sell_px:
                sim.sell(min_sell_px, sim.holdings, i, reason="PositionManager: TakeProfit")
            # 2. Stop Loss (3%)
            elif loss_pct >= 3.0:
                sim.sell(price, sim.holdings, i, reason="PositionManager: StopLoss")
        
        # Buying logic: frequent small scalping if price dips slightly
        if sim.holdings == 0 and i % 5 == 0:
            sim.buy(price, 25.0, i, reason="Scalp: Periodic Buy")
            
        sim.update(price)
    sim.report("Model 1: Naive Periodic Scalp + 15bps TP / 3% SL")

# Model 2: Strict Trend Following (Only Buy in Bullish Regimes)
def run_model2_trend():
    sim = BacktestSimulator(initial_capital=100.0)
    for i in range(50, n):
        b = bars[i]
        price = b['close']
        
        # Check exit
        if sim.holdings > 0:
            cost = sim.avg_cost
            min_sell_px = cost * (1 + 0.0030) / (1 - FEE_RATE) # 30 bps net profit
            loss_pct = (cost - price) / cost * 100.0
            
            # Trend breakdown exit
            trend_broken = b['ema9'] < b['ema21'] and bars[i-1]['ema9'] >= bars[i-1]['ema21']
            
            if b['high'] >= min_sell_px and (trend_broken or price >= cost * 1.01):
                sim.sell(price, sim.holdings, i, reason="Trend: TP / Reversal")
            elif loss_pct >= 2.0 or trend_broken:
                sim.sell(price, sim.holdings, i, reason="Trend: Cut Loss")
                
        # Buy condition: Strong Bullish Alignment
        if sim.holdings == 0:
            bullish = b['ema9'] > b['ema21'] > b['ema50'] and price > b['ema9'] and b['rsi'] > 52 and b['rsi'] < 70
            golden_cross = bars[i-1]['ema9'] <= bars[i-1]['ema21'] and b['ema9'] > b['ema21']
            if bullish and golden_cross:
                sim.buy(price, 30.0, i, reason="Trend: Golden Cross")
                
        sim.update(price)
    sim.report("Model 2: EMA Trend Following (Cross & Alignment)")

# Model 3: Bollinger Oversold Mean-Reversion + Tight Trailing Profit
def run_model3_mean_reversion():
    sim = BacktestSimulator(initial_capital=100.0)
    peak_price_since_buy = 0.0
    for i in range(50, n):
        b = bars[i]
        price = b['close']
        
        if sim.holdings > 0:
            cost = sim.avg_cost
            if price > peak_price_since_buy:
                peak_price_since_buy = price
            
            pnl_pct = (price - cost) / cost * 100.0
            drawdown_from_peak = (peak_price_since_buy - price) / peak_price_since_buy * 100.0
            
            # Target 1: Quick profit at BB mid or +0.6%
            if price >= b['bb_mid'] and pnl_pct >= 0.35: # net positive
                sim.sell(price, sim.holdings, i, reason="MeanRev: BB Mid Reached")
            # Target 2: Trailing profit lock (if peaked > 0.8% and pulled back 0.25%)
            elif (peak_price_since_buy - cost) / cost * 100.0 >= 0.8 and drawdown_from_peak >= 0.25:
                sim.sell(price, sim.holdings, i, reason="MeanRev: Trailing Stop Profit")
            # Target 3: Hard Stop Loss at 1.8%
            elif pnl_pct <= -1.8:
                sim.sell(price, sim.holdings, i, reason="MeanRev: Stop Loss")
            # Timeout: If held > 60 minutes and breakeven available
            elif (i - sim.entry_bar) >= 60 and pnl_pct >= 0.15:
                sim.sell(price, sim.holdings, i, reason="MeanRev: Time Decay Exit")
                
        # Buy condition: Extreme dip / lower band pierce + RSI oversold
        if sim.holdings == 0:
            oversold = (b['low'] <= b['bb_low'] or b['rsi'] < 28) and b['close'] > b['low'] # hammer/rejection
            rsi_turning = b['rsi'] > bars[i-1]['rsi'] and bars[i-1]['rsi'] < 30
            if (oversold or rsi_turning) and price > b['ema200'] * 0.96:
                sim.buy(price, 30.0, i, reason="MeanRev: BB Lower Bounce")
                peak_price_since_buy = price
                
        sim.update(price)
    sim.report("Model 3: Bollinger Bands + RSI Oversold Bounce")

# Model 4: Multi-Timeframe Momentum Breakout + Volume Confirmation
def run_model4_breakout():
    sim = BacktestSimulator(initial_capital=100.0)
    for i in range(50, n):
        b = bars[i]
        price = b['close']
        
        if sim.holdings > 0:
            cost = sim.avg_cost
            pnl_pct = (price - cost) / cost * 100.0
            # Profit target: +1.2% or RSI overbought > 75
            if pnl_pct >= 1.2 or (pnl_pct >= 0.5 and b['rsi'] > 75):
                sim.sell(price, sim.holdings, i, reason="Breakout: Target / Overbought")
            elif pnl_pct <= -1.5:
                sim.sell(price, sim.holdings, i, reason="Breakout: Stop Loss")
            elif b['close'] < b['ema21'] and pnl_pct > 0.25:
                sim.sell(price, sim.holdings, i, reason="Breakout: Under EMA21 Exit")

        # Buy condition: 20-bar Donchian High Breakout with high volume
        if sim.holdings == 0:
            high_20 = max(bars[j]['high'] for j in range(i-20, i))
            avg_vol_20 = sum(bars[j]['vol_usd'] for j in range(i-20, i)) / 20.0
            
            is_breakout = b['close'] > high_20 and b['vol_usd'] > avg_vol_20 * 1.8 and b['ema9'] > b['ema21']
            if is_breakout:
                sim.buy(price, 30.0, i, reason="Breakout: 20-bar High + Vol Spike")
                
        sim.update(price)
    sim.report("Model 4: Volume-Backed Donchian Breakout")

run_model1_baseline()
run_model2_trend()
run_model3_mean_reversion()
run_model4_breakout()
