import sqlite3
from datetime import datetime

conn = sqlite3.connect('src/Dvc.Web/data/trading.db')
c = conn.cursor()

# Check when orders 64, 65, 66 were updated to Filled
c.execute("SELECT Id, Status, FilledBaseAmount, FilledAvgPrice, CreatedAtUtc, UpdatedAtUtc FROM Trade_Order WHERE Id IN (64, 65, 66, 70, 73, 74)")
for r in c.fetchall():
    print("Order:", r)
