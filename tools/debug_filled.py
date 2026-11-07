import sqlite3
import json

conn = sqlite3.connect('src/Dvc.Web/data/trading.db')
c = conn.cursor()
c.execute("SELECT Id, ClientOrderId, Symbol, Side, Price, AmountUsd, BaseAmount, FilledBaseAmount, FilledAvgPrice, Status, CreatedAtUtc, UpdatedAtUtc FROM Trade_Order WHERE Status='Filled' ORDER BY Id DESC LIMIT 20")
rows = c.fetchall()
print(f"Total filled orders: {len(rows)}")
for r in rows:
    print(r)
