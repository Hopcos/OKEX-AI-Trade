import sqlite3
import json

conn = sqlite3.connect('src/Dvc.Web/data/trading.db')
c = conn.cursor()

c.execute("SELECT Id, CreatedAtUtc, RequestJson FROM Agent_Thought_Log WHERE Id BETWEEN 110 AND 160 ORDER BY Id ASC")
for row in c.fetchall():
    req_str = row[2]
    if not req_str or not req_str.strip().startswith('{'):
        print(f"ID={row[0]} at {row[1]}: Invalid JSON ({len(req_str)} chars)")
        continue
    try:
        req = json.loads(req_str)
        fm = req.get('Financial_Metrics', {})
        print(f"ID={row[0]} at {row[1]}: Avail={fm.get('Available_USD')}, Equity={fm.get('Current_Total_Equity')}, Held={fm.get('Position_Base_Qty')}, Cost={fm.get('Position_Avg_Cost')}, UnPnL={fm.get('Unrealized_Pnl')}, Committed={fm.get('Committed_Usd')}")
    except Exception as e:
        print(f"ID={row[0]} at {row[1]}: JSON error {e}")
