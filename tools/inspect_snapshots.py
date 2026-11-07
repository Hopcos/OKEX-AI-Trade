import sqlite3
import json

conn = sqlite3.connect('src/Dvc.Web/data/trading.db')
c = conn.cursor()

c.execute("SELECT Id, SnapshotAtUtc, InitialCapital, AvailableUsd, EquityUsd, RealizedPnl, HoldingsJson, LastPricesJson FROM Asset_Snapshot ORDER BY Id DESC LIMIT 10")
for r in c.fetchall():
    print(r)
