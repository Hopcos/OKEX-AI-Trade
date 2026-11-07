import sqlite3

conn = sqlite3.connect('src/Dvc.Web/data/trading.db')
c = conn.cursor()

c.execute("""
SELECT Id, ClientOrderId, Symbol, Side, Price, AmountUsd, BaseAmount, FilledBaseAmount, FilledAvgPrice, Status, CreatedAtUtc
FROM Trade_Order
WHERE Symbol='TRUMP-USDT'
ORDER BY Id ASC
""")
orders = c.fetchall()
print(f"Total TRUMP orders: {len(orders)}")

total_bought_qty = 0.0
total_spent = 0.0
fee_rate = 0.001

for o in orders:
    oid, clOrdId, sym, side, px, amt, base_amt, filled_base, filled_px, status, created = o
    print(f"ID={oid} | {created} | {side} | Status={status} | Px={px} | FilledQty={filled_base} | FilledPx={filled_px}")
    if status == 'Filled' and side == 'Buy':
        total_bought_qty += filled_base
        total_spent += filled_base * filled_px * (1 + fee_rate)

avg_cost = total_spent / total_bought_qty if total_bought_qty > 0 else 0
print(f"\n--- TRUMP TOTALS ---")
print(f"Total Bought Qty: {total_bought_qty:.8f}")
print(f"Total Spent USD (with fee): ${total_spent:.4f}")
print(f"Avg Cost USD: ${avg_cost:.6f}")
