import sqlite3
from datetime import datetime

conn = sqlite3.connect('src/Dvc.Web/data/trading.db')
c = conn.cursor()

# Let's check orders with CreatedAtUtc between 13:00 and 14:00 UTC
c.execute("SELECT count(*) FROM Trade_Order WHERE FilledBaseAmount > 0 AND CreatedAtUtc >= '2026-11-07T21:04:00Z'")
print("Orders with CreatedAtUtc >= 21:04:00Z:", c.fetchone()[0])

c.execute("SELECT count(*) FROM Trade_Order WHERE FilledBaseAmount > 0 AND CreatedAtUtc >= '2026-11-07T13:04:00Z'")
print("Orders with CreatedAtUtc >= 13:04:00Z:", c.fetchone()[0])
