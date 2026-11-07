import sqlite3
import os

dbs = [
    'src/Dvc.Web/data/trading.db',
    'src/Dvc.Web/data/trading-10260121.db',
    'data/trading.db'
]

for p in dbs:
    if os.path.exists(p):
        print(f"\n=== Inspecting: {p} (size={os.path.getsize(p)} bytes) ===")
        conn = sqlite3.connect(p)
        c = conn.cursor()
        c.execute("SELECT name FROM sqlite_master WHERE type='table'")
        tables = [row[0] for row in c.fetchall()]
        print("Tables:", tables)
        for t in tables:
            c.execute(f"SELECT count(*) FROM {t}")
            cnt = c.fetchone()[0]
            print(f"  {t}: {cnt} rows")
            if t.lower() == 'trade_order':
                c.execute(f"SELECT id, client_order_id, symbol, side, price, filled_base_amount, filled_avg_price, status, created_at_utc FROM {t} ORDER BY id DESC LIMIT 5")
                for r in c.fetchall():
                    print("    Order:", r)
            elif t.lower() == 'asset_snapshot':
                c.execute(f"SELECT id, snapshot_at_utc, equity_usd, available_usd, realized_pnl, holdings_json, last_prices_json FROM {t} ORDER BY id DESC LIMIT 3")
                for r in c.fetchall():
                    print("    Snapshot:", r)
            elif t.lower() == 'system_config':
                c.execute(f"SELECT key, value FROM {t}")
                for r in c.fetchall():
                    print("    Config:", r)
