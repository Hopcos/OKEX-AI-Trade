import sqlite3
import json

conn = sqlite3.connect('src/Dvc.Web/data/trading.db')
c = conn.cursor()

# Let's see what was logged in Agent_Thought_Log around Thought 115
# Specifically let's look at the ThinkingText of Thought 115, 116, 120
c.execute("SELECT Id, SessionId, ThinkingText FROM Agent_Thought_Log WHERE Id IN (114, 115, 116, 120, 130, 140)")
for r in c.fetchall():
    print(f"\n================ ID={r[0]} ================")
    print(r[2])
