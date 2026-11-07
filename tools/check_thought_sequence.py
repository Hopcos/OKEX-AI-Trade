import sqlite3
import json

conn = sqlite3.connect('src/Dvc.Web/data/trading.db')
c = conn.cursor()

c.execute("""
SELECT Id, CreatedAtUtc, RequestJson, ThinkingText, DecisionJson
FROM Agent_Thought_Log
WHERE Id BETWEEN 120 AND 145
ORDER BY Id ASC
""")
rows = c.fetchall()
for r in rows:
    oid, created, req_str, think, dec = r
    req = json.loads(req_str)
    fm = req.get('Financial_Metrics', {})
    print(f"ID={oid} | {created} | Held={fm.get('Position_Base_Qty')} | Cost={fm.get('Position_Avg_Cost')} | UnPnL={fm.get('Unrealized_Pnl')} | Avail={fm.get('Available_USD')} | Committed={fm.get('Committed_Usd')}")
