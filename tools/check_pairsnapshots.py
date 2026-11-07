import sqlite3
import json

conn = sqlite3.connect('src/Dvc.Web/data/trading.db')
c = conn.cursor()

c.execute("SELECT RequestJson FROM Agent_Thought_Log WHERE Id=115")
row = c.fetchone()
req = json.loads(row[0])
print("PairSnapshots keys:", [p.get('Symbol') for p in req.get('Pair_Snapshots', [])])
print("MarketSnapshot:", req.get('Market_Snapshot'))
