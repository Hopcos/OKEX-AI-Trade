import sqlite3
import json

conn = sqlite3.connect('src/Dvc.Web/data/trading.db')
c = conn.cursor()

# In Agent_Thought_Log, let's see what RequestJson contains for thought 110, 112, 115
for tid in [112, 113, 114, 115, 116, 120, 130, 140, 150]:
    c.execute("SELECT RequestJson FROM Agent_Thought_Log WHERE Id=?", (tid,))
    row = c.fetchone()
    if row:
        req = json.loads(row[0])
        print(f"Thought {tid} System_Time: {req.get('System_Time')}")
