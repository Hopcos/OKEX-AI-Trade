import sqlite3
import json

conn = sqlite3.connect('src/Dvc.Web/data/trading.db')
c = conn.cursor()

# Check what was in Trade_Order when LedgerStartUtc was '2026-10-25T17:20:00Z'
since_oct = '2026-10-25T17:20:00Z'
c.execute("""
SELECT Id, Symbol, Side, Price, BaseAmount, FilledBaseAmount, FilledAvgPrice, Status, CreatedAtUtc
FROM Trade_Order
WHERE FilledBaseAmount > 0 AND CreatedAtUtc >= ?
ORDER BY Id ASC
""", (since_oct,))
all_fills = c.fetchall()
print(f"Fills since {since_oct}: {len(all_fills)}")
for f in all_fills:
    print(f)
