import sqlite3
import os

p = 'data/trading.db'
if os.path.exists(p):
    conn = sqlite3.connect(p)
    c = conn.cursor()
    c.execute("SELECT name FROM sqlite_master WHERE type='table'")
    tables = [row[0] for row in c.fetchall()]
    print("Tables in trading.db:", tables)
    for t in tables:
        c.execute(f"SELECT count(*) FROM {t}")
        cnt = c.fetchone()[0]
        print(f"  {t}: {cnt} rows")
        if t in ['Trade_Order', 'trade_order']:
            c.execute(f"SELECT id, client_order_id, symbol, side, price, filled_base_amount, filled_avg_price, status, created_at_utc FROM {t} ORDER BY id DESC LIMIT 10")
            for r in c.fetchall():
                print("    Order:", r)
        elif t in ['Asset_Snapshot', 'asset_snapshot']:
            c.execute(f"SELECT id, snapshot_at_utc, equity_usd, available_usd, realized_pnl, holdings_json, last_prices_json FROM {t} ORDER BY id DESC LIMIT 5")
            for r in c.fetchall():
                print("    Snapshot:", r)
        elif t in ['Agent_Thought_Log', 'agent_thought_log']:
            c.execute(f"SELECT id, created_at_utc, diagnosis, action_type, risk_vetoed, risk_reason FROM {t} ORDER BY id DESC LIMIT 5")
            for r in c.fetchall():
                print("    Thought:", r)
else:
    print("data/trading.db does not exist")
