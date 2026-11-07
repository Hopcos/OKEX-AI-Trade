import sqlite3
import sys

sys.stdout.reconfigure(encoding='utf-8')

conn = sqlite3.connect('src/Dvc.Web/data/trading.db')
c = conn.cursor()

c.execute("PRAGMA table_info(Trade_Order)")
cols = [col[1] for col in c.fetchall()]

c.execute("SELECT * FROM Trade_Order WHERE Id IN (64, 65, 66)")
for r in c.fetchall():
    print("--- Order ---")
    for k, v in zip(cols, r):
        print(f"  {k}: {v}")
