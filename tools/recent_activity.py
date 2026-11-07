import sqlite3, sys, json
sys.stdout.reconfigure(encoding='utf-8')
c = sqlite3.connect('src/Dvc.Web/data/trading.db').cursor()
c.execute("SELECT Id,Side,Price,FilledBaseAmount,Status,CreatedAtUtc,substr(Reason,1,90) FROM Trade_Order WHERE Id>=99 ORDER BY Id")
for r in c.fetchall(): print(r)
print('---thoughts')
c.execute("SELECT ActionType,count(*),sum(RiskVetoed) FROM Agent_Thought_Log WHERE Id>=112 GROUP BY ActionType")
print(c.fetchall())
c.execute("SELECT Id,CreatedAtUtc,ActionType,RiskVetoed,substr(RiskReason,1,80),substr(Diagnosis,1,80),RequestJson FROM Agent_Thought_Log ORDER BY Id DESC LIMIT 8")
for r in c.fetchall():
    fm={}
    try: fm=json.loads(r[6]).get('Financial_Metrics',{})
    except: pass
    print(r[:6], 'qty',fm.get('Position_Base_Qty'),'unp',fm.get('Unrealized_Pnl'),'sellable',fm.get('Position_Sellable_Now'),'avail',fm.get('Available_USD'))
