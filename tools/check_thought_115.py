import sqlite3
import json

conn = sqlite3.connect('src/Dvc.Web/data/trading.db')
c = conn.cursor()

c.execute("SELECT RequestJson FROM Agent_Thought_Log WHERE Id=115")
row = c.fetchone()
req = json.loads(row[0])
print("Thought 115 config:", req.get('Current_Config'))
print("Thought 115 full:", json.dumps(req, indent=2))
