import sqlite3
import json

conn = sqlite3.connect('src/Dvc.Web/data/trading.db')
c = conn.cursor()

c.execute("SELECT Id, CreatedAtUtc, RequestJson FROM Agent_Thought_Log WHERE Id BETWEEN 113 AND 125")
for r in c.fetchall():
    req = json.loads(r[2])
    fm = req.get('Financial_Metrics', {})
    print(f"ID={r[0]} | Time={r[1]} | Avail={fm.get('Available_USD')} | Committed={fm.get('Committed_Usd')} | Held={fm.get('Position_Base_Qty')} | Cost={fm.get('Position_Avg_Cost')}")
