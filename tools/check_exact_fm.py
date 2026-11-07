import sqlite3
import json

conn = sqlite3.connect('src/Dvc.Web/data/trading.db')
c = conn.cursor()

# Check thoughts around 13:10 to 13:15
c.execute("""
SELECT Id, CreatedAtUtc, RequestJson
FROM Agent_Thought_Log
WHERE Id BETWEEN 110 AND 120
ORDER BY Id ASC
""")
for row in c.fetchall():
    req = json.loads(row[2])
    fm = req.get('Financial_Metrics', {})
    print(f"ID={row[0]} at {row[1]}:")
    print(f"  InitCapital: {fm.get('Initial_Capital')}, Avail: {fm.get('Available_USD')}, Equity: {fm.get('Current_Total_Equity')}")
    print(f"  HeldQty: {fm.get('Position_Base_Qty')}, Cost: {fm.get('Position_Avg_Cost')}, UnPnL: {fm.get('Unrealized_Pnl')}")
    print(f"  Committed: {fm.get('Committed_Usd')}, BuyRoom: {fm.get('Buy_Room_USD')}")
