import sqlite3
import json

conn = sqlite3.connect('src/Dvc.Web/data/trading.db')
c = conn.cursor()

c.execute("SELECT Id, CreatedAtUtc, RequestJson, ThinkingText, DecisionJson FROM Agent_Thought_Log WHERE Id BETWEEN 100 AND 120 ORDER BY Id ASC")
for r in c.fetchall():
    oid, created, req_str, think, dec = r
    try:
        req = json.loads(req_str)
        fm = req.get('Financial_Metrics', {})
        print(f"ID={oid} | {created} | Held={fm.get('Position_Base_Qty')} | Cost={fm.get('Position_Avg_Cost')} | UnPnL={fm.get('Unrealized_Pnl')} | Avail={fm.get('Available_USD')} | Committed={fm.get('Committed_Usd')}")
    except Exception as e:
        print(f"ID={oid} json err: {e}")
