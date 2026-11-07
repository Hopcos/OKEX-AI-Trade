import sqlite3

conn = sqlite3.connect('src/Dvc.Web/data/trading.db')
c = conn.cursor()

c.execute("""
SELECT Id, ClientOrderId, Symbol, Side, Price, BaseAmount, FilledBaseAmount, FilledAvgPrice, Status, ExchangeOrderId, CreatedAtUtc, UpdatedAtUtc
FROM Trade_Order
WHERE Id IN (64, 65, 66, 70, 73, 74, 77, 80, 88, 89, 90, 91, 94, 98)
""")
for r in c.fetchall():
    print(r)
