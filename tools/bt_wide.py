import sys, statistics
sys.stdout.reconfigure(encoding='utf-8')
src = open('tools/bt_signal.py', encoding='utf-8').read().split("\nfor inst, sp in [(")[0]
exec(src)
for inst, sp in [("TRUMP-USDT", 0.00084), ("SOL-USDT", 0.0001), ("DOGE-USDT", 0.0004), ("BTC-USDT", 0.00001)]:
    cs = candles(inst, 9000)
    print(inst, 'drift', round((cs[-1][4]/cs[0][4]-1)*100,1), '%')
    for sig, tp, sl, hor, mk in [('deepdip',60,2.0,120,True),('deepdip',100,3.0,240,True),('dip_bounce',100,3.0,240,True),
                                 ('none',100,3.0,240,True),('deepdip',150,4.0,480,True),('dip',60,3.0,240,True)]:
        S(f"{sig} tp{tp} sl{sl} h{hor} maker", run(cs, sp, sig, tp, sl, hor, mk))

