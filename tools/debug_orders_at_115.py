import sqlite3
import json

conn = sqlite3.connect('src/Dvc.Web/data/trading.db')
c = conn.cursor()

# At 13:13:31 (Thought 115), what was in Trade_Order?
c.execute("SELECT Id, ClientOrderId, Symbol, Side, Price, BaseAmount, FilledBaseAmount, FilledAvgPrice, Status, CreatedAtUtc, UpdatedAtUtc FROM Trade_Order WHERE Id <= 66")
for r in c.fetchall():
    print(r)
