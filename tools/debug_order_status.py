import sqlite3

conn = sqlite3.connect('src/Dvc.Web/data/trading.db')
c = conn.cursor()

# Let's inspect Order 64:
c.execute("SELECT Id, ClientOrderId, Symbol, Side, Price, BaseAmount, FilledBaseAmount, FilledAvgPrice, Status, ExchangeOrderId, CreatedAtUtc, UpdatedAtUtc FROM Trade_Order WHERE Id=64")
print("Order 64:", c.fetchone())

# And let's inspect what AccountBalances had!
