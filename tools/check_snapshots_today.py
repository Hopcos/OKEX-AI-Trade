import sqlite3

conn = sqlite3.connect('src/Dvc.Web/data/trading.db')
c = conn.cursor()

# Let's inspect Asset_Snapshot rows around 13:00 to 13:40
c.execute("SELECT Id, SnapshotAtUtc, Kind, InitialCapital, AvailableUsd, EquityUsd, RealizedPnl, HoldingsJson, LastPricesJson FROM Asset_Snapshot WHERE SnapshotAtUtc >= '2026-11-07T13:00:00Z' ORDER BY Id ASC")
for r in c.fetchall():
    print(r)
