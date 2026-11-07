import sqlite3
import json

conn = sqlite3.connect('src/Dvc.Web/data/trading.db')
c = conn.cursor()

# Check what was in Trade_Order when those thoughts were logged
c.execute("SELECT Id, CreatedAtUtc, ThinkingText FROM Agent_Thought_Log WHERE Id BETWEEN 115 AND 125")
for row in c.fetchall():
    print(f"--- Thought ID {row[0]} ({row[1]}) ---")
    print(row[2][:300])
