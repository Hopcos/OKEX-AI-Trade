import sqlite3
from datetime import datetime

conn = sqlite3.connect('src/Dvc.Web/data/trading.db')
c = conn.cursor()

since = "2026-11-07T21:04:00.0000000Z"
c.execute("SELECT count(*) FROM Trade_Order WHERE FilledBaseAmount > 0 AND CreatedAtUtc >= ?", (since,))
cnt_with_since = c.fetchone()[0]

c.execute("SELECT count(*) FROM Trade_Order WHERE FilledBaseAmount > 0 AND Symbol='TRUMP-USDT'")
cnt_all_trump = c.fetchone()[0]

c.execute("SELECT min(CreatedAtUtc), max(CreatedAtUtc) FROM Trade_Order WHERE Symbol='TRUMP-USDT' AND FilledBaseAmount > 0")
min_max = c.fetchone()

print(f"Filled TRUMP orders with CreatedAtUtc >= '{since}': {cnt_with_since}")
print(f"Total filled TRUMP orders: {cnt_all_trump}")
print(f"TRUMP orders CreatedAtUtc range: {min_max[0]} to {min_max[1]}")
