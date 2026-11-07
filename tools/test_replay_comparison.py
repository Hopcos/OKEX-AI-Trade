import sqlite3

conn = sqlite3.connect('src/Dvc.Web/data/trading.db')
c = conn.cursor()

c.execute("SELECT Id, Symbol, Side, Price, FilledBaseAmount, FilledAvgPrice, Status, CreatedAtUtc FROM Trade_Order WHERE FilledBaseAmount > 0 ORDER BY Id ASC")
fills = c.fetchall()
print(f"Total filled orders in DB: {len(fills)}")

def replay(since=None):
    cash = 28.24
    fee_rate = 0.001
    realized = 0.0
    pos = {}
    
    for r in fills:
        oid, sym, side, px, qty, fill_px, status, created = r
        if since and created < since:
            continue
        p = fill_px if fill_px > 0 else px
        if side == 'Buy':
            cost = qty * p * (1 + fee_rate)
            cash -= cost
            old_qty, old_avg = pos.get(sym, (0.0, 0.0))
            new_qty = old_qty + qty
            new_avg = (old_qty * old_avg + cost) / new_qty
            pos[sym] = (new_qty, new_avg)
        elif side == 'Sell':
            old_qty, old_avg = pos.get(sym, (0.0, 0.0))
            sell_qty = min(qty, old_qty)
            if sell_qty > 0:
                proceeds = sell_qty * p * (1 - fee_rate)
                cash += proceeds
                realized += proceeds - sell_qty * old_avg
                left = old_qty - sell_qty
                if left <= 1e-8:
                    pos.pop(sym, None)
                else:
                    pos[sym] = (left, old_avg)
    return cash, pos, realized

print("\n--- REPLAY ALL ---")
cash, pos, realized = replay(None)
print("Cash:", cash, "Realized:", realized, "Positions:", pos)

print("\n--- REPLAY TODAY (since 2026-11-07T13:00:00Z) ---")
cash, pos, realized = replay("2026-11-07T13:00:00Z")
print("Cash:", cash, "Realized:", realized, "Positions:", pos)
