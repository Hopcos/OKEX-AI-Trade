import sqlite3
import json

conn = sqlite3.connect('src/Dvc.Web/data/trading.db')
c = conn.cursor()

# Check when the orders were filled vs when thoughts occurred
# Let's check Trade_Order rows 64, 65, 66
c.execute("SELECT Id, Symbol, Side, Price, BaseAmount, FilledBaseAmount, FilledAvgPrice, Status, CreatedAtUtc, UpdatedAtUtc FROM Trade_Order WHERE Id IN (64, 65, 66)")
for r in c.fetchall():
    print("Order:", r)

# Now check thoughts at that exact time:
c.execute("SELECT Id, CreatedAtUtc FROM Agent_Thought_Log WHERE CreatedAtUtc BETWEEN '2026-11-07T13:12:00Z' AND '2026-11-07T13:14:00Z'")
for r in c.fetchall():
    print("Thought:", r)
