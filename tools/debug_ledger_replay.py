import sqlite3

conn = sqlite3.connect('src/Dvc.Web/data/trading.db')
c = conn.cursor()

# Let's inspect the exact orders in DB at 13:13:31
since = "2026-10-25T17:20:00Z"
c.execute("""
SELECT Id, ClientOrderId, Symbol, Side, Price, BaseAmount, FilledBaseAmount, FilledAvgPrice, Status, CreatedAtUtc, UpdatedAtUtc
FROM Trade_Order
WHERE FilledBaseAmount > 0 AND CreatedAtUtc >= ?
ORDER BY Id ASC
""", (since,))
rows = c.fetchall()
print(f"Total filled orders since Oct 25: {len(rows)}")
for r in rows:
    print(r)
