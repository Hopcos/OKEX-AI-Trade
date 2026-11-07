import sqlite3
import json

conn = sqlite3.connect('src/Dvc.Web/data/trading.db')
c = conn.cursor()

c.execute("SELECT Id, SessionId, CreatedAtUtc, RequestJson FROM Agent_Thought_Log WHERE Id BETWEEN 115 AND 125")
for row in c.fetchall():
    req = json.loads(row[3])
    fm = req.get('Financial_Metrics', {})
    cfg = req.get('Current_Config', {})
    print(f"Thought {row[0]} ({row[2]}):")
    print(f"  ActiveSymbol: {cfg.get('ActiveSymbol')}")
    print(f"  Held: {fm.get('Position_Base_Qty')}")
    print(f"  HeldSymbols: {fm.get('Held_Symbols')}")
    print(f"  Available: {fm.get('Available_USD')}")
    print(f"  Committed: {fm.get('Committed_Usd')}")
    print(f"  Equity: {fm.get('Current_Total_Equity')}")
