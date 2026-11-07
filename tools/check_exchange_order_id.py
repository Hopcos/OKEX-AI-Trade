import sqlite3

conn = sqlite3.connect('src/Dvc.Web/data/trading.db')
c = conn.cursor()
c.execute("SELECT Id, Symbol, Side, ExchangeOrderId, CreatedAtUtc FROM Trade_Order WHERE Id <= 62")
for r in c.fetchall():
    print(r)
