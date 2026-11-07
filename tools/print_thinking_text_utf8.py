import sqlite3
import sys

sys.stdout.reconfigure(encoding='utf-8')

conn = sqlite3.connect('src/Dvc.Web/data/trading.db')
c = conn.cursor()

c.execute("SELECT Id, SessionId, ThinkingText FROM Agent_Thought_Log WHERE Id IN (114, 115, 116, 120, 130, 140, 150, 155)")
for r in c.fetchall():
    print(f"\n================ ID={r[0]} ================")
    print(r[2][:600])
