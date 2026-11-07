import json
import sqlite3
from datetime import datetime

# Test what BudgetLedger would produce if LedgerStartUtc is 13:00:00Z (before the TRUMP buys)
conn = sqlite3.connect('src/Dvc.Web/data/trading.db')
c = conn.cursor()

since_correct = "2026-11-07T13:00:00.0000000Z"
c.execute("""
SELECT Id, ClientOrderId, Symbol, Side, Price, AmountUsd, BaseAmount, FilledBaseAmount, FilledAvgPrice, Status, CreatedAtUtc
FROM Trade_Order
WHERE FilledBaseAmount > 0 AND CreatedAtUtc >= ?
ORDER BY Id ASC
""", (since_correct,))
fills = c.fetchall()
print(f"Fills since 13:00 UTC: {len(fills)}")

initial_capital = 28.24
fee_rate = 0.001

cash = initial_capital
realized = 0.0
pos = {}

for o in fills:
    oid, clid, sym, side, px, amt, base_amt, filled_base, filled_px, status, created = o
    qty = filled_base
    p = filled_px if filled_px > 0 else px
    
    if side == 'Buy':
        cost = qty * p * (1 + fee_rate)
        cash -= cost
        old_qty, old_avg = pos.get(sym, (0.0, 0.0))
        new_qty = old_qty + qty
        new_avg = (old_qty * old_avg + cost) / new_qty
        pos[sym] = (new_qty, new_avg)
    else:
        # sell
        old_qty, old_avg = pos.get(sym, (0.0, 0.0))
        sell_qty = min(qty, old_qty)
        proceeds = sell_qty * p * (1 - fee_rate)
        cash += proceeds
        pnl = proceeds - sell_qty * old_avg
        realized += pnl
        left = old_qty - sell_qty
        if left <= 1e-8:
            pos.pop(sym, None)
        else:
            pos[sym] = (left, old_avg)

print("\n=== REPLAY RESULT ===")
print(f"Cash: ${cash:.4f}")
print(f"Realized PnL: ${realized:.4f}")
print("Positions:")
for sym, (qty, avg) in pos.items():
    print(f"  {sym}: Qty={qty:.8f}, AvgCost=${avg:.6f}, CostTotal=${qty*avg:.4f}")
    current_px = 1.2035
    market_val = qty * current_px
    unrealized = (current_px - avg) * qty
    pnl_pct = (current_px - avg) / avg * 100
    print(f"  Current Px: ${current_px} | Market Val: ${market_val:.4f} | Unrealized: ${unrealized:+.4f} ({pnl_pct:+.2f}%)")
    print(f"  Total Equity: ${cash + market_val:.4f}")
