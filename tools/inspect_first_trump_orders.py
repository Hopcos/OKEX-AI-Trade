import sqlite3
import sys
sys.stdout.reconfigure(encoding='utf-8')

conn = sqlite3.connect('src/Dvc.Web/data/trading.db')
c = conn.cursor()

c.execute("SELECT Id, ClientOrderId, Symbol, Side, Price, BaseAmount, FilledBaseAmount, FilledAvgPrice, Status, ExchangeOrderId, CreatedAtUtc, UpdatedAtUtc FROM Trade_Order WHERE Id BETWEEN 64 AND 75")
for r in c.fetchall():
    print(r)
