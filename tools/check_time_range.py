import sqlite3

conn = sqlite3.connect('src/Dvc.Web/data/trading.db')
c = conn.cursor()

c.execute("SELECT min(CreatedAtUtc), max(CreatedAtUtc) FROM Trade_Order WHERE Symbol='TRUMP-USDT'")
print("TRUMP orders range:", c.fetchone())

c.execute("SELECT min(CreatedAtUtc), max(CreatedAtUtc) FROM Agent_Thought_Log")
print("Thought log range:", c.fetchone())
