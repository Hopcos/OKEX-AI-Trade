import sqlite3
import json

conn = sqlite3.connect('src/Dvc.Web/data/trading.db')
c = conn.cursor()

# Let's inspect Asset_Snapshot row 13 and 14
c.execute("SELECT * FROM Asset_Snapshot WHERE Id IN (13, 14)")
for r in c.fetchall():
    print("Snapshot:", r)
