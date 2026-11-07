import sqlite3
import json

conn = sqlite3.connect('src/Dvc.Web/data/trading.db')
c = conn.cursor()

# If LedgerStartUtc is '2026-11-07T13:00:00Z'
since = "2026-11-07T13:00:00.0000000Z"
c.execute("""
SELECT Id, Symbol, Side, Price, BaseAmount, FilledBaseAmount, FilledAvgPrice, Status, CreatedAtUtc
FROM Trade_Order
WHERE FilledBaseAmount > 0 AND CreatedAtUtc >= ?
ORDER BY Id ASC
""", (since,))
fills = c.fetchall()

initial_capital = 28.24
fee_rate = 0.001
cash = initial_capital
realized = 0.0
pos = {}

for o in fills:
    oid, sym, side, px, base_amt, filled_base, filled_px, status, created = o
    qty = filled_base
    p = filled_px if filled_px > 0 else px
    if side == 'Buy':
        cost = qty * p * (1 + fee_rate)
        cash -= cost
        old_qty, old_avg = pos.get(sym, (0.0, 0.0))
        new_qty = old_qty + qty
        new_avg = (old_qty * old_avg + cost) / new_qty
        pos[sym] = (new_qty, new_avg)

# In Live mode:
# raw.Holdings has 'TRUMP': 47.25513718
raw_holdings = {'TRUMP': 47.25513718}
raw_avail_usd = 0.00187765

holdings = {}
avg_costs = {}
pos_val = 0.0
last_prices = {}

# Current market price of TRUMP is ~1.204
current_trump_price = 1.204

for sym, (qty, avg) in pos.items():
    base = sym.split('-')[0]
    actual = raw_holdings.get(base, 0.0)
    managed = min(qty, actual)
    px = current_trump_price
    missing = qty - managed
    if missing > 1e-8:
        proceeds = missing * px * (1 - fee_rate)
        cash += proceeds
        realized += proceeds - missing * avg
    if managed > 0:
        holdings[base] = managed
        avg_costs[base] = avg
        pos_val += managed * px
        last_prices[sym] = px

# Open orders
open_orders_reserved = 0.0

free = max(0.0, min(cash - open_orders_reserved, raw_avail_usd))
total_equity = cash + pos_val
unrealized_pnl = sum((last_prices[sym] - avg_costs[sym.split('-')[0]]) * holdings[sym.split('-')[0]] for sym in last_prices)
accumulated_profit = total_equity - initial_capital

print("=== PROJECTED ACCOUNT STATE WITH LedgerStartUtc = 13:00:00Z ===")
print(f"Available USD: ${free:.2f}")
print(f"Total Equity USD: ${total_equity:.2f}")
print(f"Realized PnL USD: ${realized:.4f}")
print(f"Unrealized PnL USD: ${unrealized_pnl:+.4f}")
print(f"Accumulated Profit USD: ${accumulated_profit:+.4f}")
print(f"Holdings: {holdings}")
print(f"AvgCosts: {avg_costs}")
print(f"LastPrices: {last_prices}")

# Check AiInputBuilder metrics
base = "TRUMP"
avgCost = avg_costs.get(base, 0.0)
held_qty = holdings.get(base, 0.0)
minNet = 40 / 10000.0  # 40 bps
min_profit_sell_price = avgCost * (1 + fee_rate) / (1 - fee_rate - minNet)
sellable_now = avgCost > 0 and current_trump_price >= min_profit_sell_price
unrealized_pct = (current_trump_price - avgCost) / avgCost * 100

print("\n=== AI INPUT BUILDER (FinancialMetrics) ===")
print(f"PositionBaseQty: {held_qty:.4f}")
print(f"PositionAvgCost: ${avgCost:.6f}")
print(f"PositionMinProfitSellPrice: ${min_profit_sell_price:.6f}")
print(f"PositionSellableNow: {sellable_now} (Current: ${current_trump_price:.4f} >= MinSell: ${min_profit_sell_price:.6f})")
print(f"PositionUnrealizedPercent: {unrealized_pct:+.2f}%")
print(f"BuyRoomUsd: $0.00 (Cash is full)")
