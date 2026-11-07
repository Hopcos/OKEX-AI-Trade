import sqlite3
import json

conn = sqlite3.connect('src/Dvc.Web/data/trading.db')
c = conn.cursor()

# Check when Order 64 was marked Filled in DB
c.execute("SELECT Id, Status, FilledBaseAmount, FilledAvgPrice, CreatedAtUtc, UpdatedAtUtc FROM Trade_Order WHERE Id IN (64, 65, 66)")
for r in c.fetchall():
    print("Order:", r)

# Check all orders created between 13:10 and 13:35
c.execute("SELECT Id, Symbol, Side, Status, FilledBaseAmount, CreatedAtUtc, UpdatedAtUtc FROM Trade_Order WHERE Symbol='TRUMP-USDT' ORDER BY Id ASC")
for r in c.fetchall():
    print(r)
