import sqlite3
import json

conn = sqlite3.connect('src/Dvc.Web/data/trading.db')
c = conn.cursor()

# Let's inspect thought logs: when did Available_USD decrease from 28.24 to 26.25, 24.22, ... down to 0.0?
c.execute("""
SELECT Id, CreatedAtUtc, RequestJson, DecisionJson
FROM Agent_Thought_Log
WHERE Id BETWEEN 112 AND 158
ORDER BY Id ASC
""")
rows = c.fetchall()

print(f"Total thoughts between 112 and 158: {len(rows)}")
for r in rows:
    tid, created, req_str, dec_str = r
    if not req_str or not req_str.strip().startswith('{'): continue
    req = json.loads(req_str)
    fm = req.get('Financial_Metrics', {})
    avail = fm.get('Available_USD')
    held = fm.get('Position_Base_Qty')
    cost = fm.get('Position_Avg_Cost')
    unpnl = fm.get('Unrealized_Pnl')
    eq = fm.get('Current_Total_Equity')
    comm = fm.get('Committed_Usd')
    buy_room = fm.get('Buy_Room_USD')
    sellable = fm.get('Position_Sellable_Now')
    print(f"Thought {tid:3d} ({created}) | Avail={avail:5.2f} | Held={held} | Cost={cost} | UnPnL={unpnl} | Equity={eq:5.2f} | Comm={comm:5.2f} | BuyRoom={buy_room:5.2f} | Sellable={sellable}")
