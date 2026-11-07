import sqlite3

conn = sqlite3.connect('src/Dvc.Web/data/trading.db')
c = conn.cursor()

c.execute("SELECT Id, Symbol, Side, Price, BaseAmount, FilledBaseAmount, FilledAvgPrice, Status, CreatedAtUtc, UpdatedAtUtc FROM Trade_Order WHERE Id >= 64 ORDER BY Id ASC")
for r in c.fetchall():
    print(r)
