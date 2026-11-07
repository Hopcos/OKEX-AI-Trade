import sqlite3
import os
import sys

# Ensure UTF-8 output
sys.stdout.reconfigure(encoding='utf-8')

p = 'src/Dvc.Web/data/trading.db'
if os.path.exists(p):
    conn = sqlite3.connect(p)
    c = conn.cursor()
    c.execute("SELECT name FROM sqlite_master WHERE type='table'")
    tables = [row[0] for row in c.fetchall()]
    print("Tables:", tables)
    for t in tables:
        if t == 'sqlite_sequence': continue
        c.execute(f"PRAGMA table_info({t})")
        cols = [col[1] for col in c.fetchall()]
        print(f"\n--- {t} (Columns: {cols}) ---")
        c.execute(f"SELECT * FROM {t} ORDER BY rowid DESC LIMIT 10")
        rows = c.fetchall()
        for r in rows:
            print("  ", r)
