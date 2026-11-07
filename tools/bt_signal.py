import sys, statistics, itertools
sys.stdout.reconfigure(encoding='utf-8')
sys.path.insert(0, 'tools')
import importlib.util
src = open('tools/bt_policy.py', encoding='utf-8').read().split("for inst, sp in")[0]
exec(src)

FEE_S = 0.001
def run(cs, spread, sig, tp, sl, hor, maker=False):
    out = []; i = 40
    while i < len(cs) - hor - 2:
        c = cs[i][4]
        r10 = (c - cs[i-10][4]) / cs[i-10][4] * 1e4
        rets = [(cs[j][4]-cs[j-1][4])/cs[j-1][4]*1e4 for j in range(i-30, i)]
        sd = statistics.pstdev(rets) or 1
        z = r10 / (sd * 10 ** 0.5)
        mom15 = (c - cs[i-15][4]) / cs[i-15][4] * 1e4
        ok = {'none': True, 'dip': z < -1.5, 'deepdip': z < -2.2, 'dip_bounce': z < -1.5 and cs[i][4] > cs[i][1],
              'mom': z > 1.5}[sig]
        if not ok: i += 1; continue
        if maker:
            bid = c * (1 - spread / 2)
            if cs[i+1][3] > bid: i += 1; continue  # not filled
            entry = bid; j0 = i + 1
        else:
            entry = c * (1 + spread / 2); j0 = i
        cost = entry * (1 + FEE_S)
        target = cost * (1 + tp / 1e4) / (1 - FEE_S)
        res = None
        for k in range(1, hor + 1):
            _, o, h, l, cl = cs[j0 + k]
            if h >= target: res = target * (1 - FEE_S) / cost - 1; break
            b = cl * (1 - spread / 2) * (1 - FEE_S)
            if (cost - b) / cost * 100 >= sl: res = b / cost - 1; break
        if res is None: res = cs[j0 + hor][4] * (1 - spread / 2) * (1 - FEE_S) / cost - 1
        out.append(res * 1e4); i += hor // 2 + 1
    return out

def S(n, r):
    if len(r) < 8: print(f"  {n:40s} n={len(r)}"); return
    print(f"  {n:40s} n={len(r):4d} win={sum(x>0 for x in r)/len(r):5.1%} avg={statistics.mean(r):7.1f}bp")

for inst, sp in [("TRUMP-USDT", 0.00084), ("DOGE-USDT", 0.0004), ("SOL-USDT", 0.0001)]:
    cs = candles(inst, 6000)
    print(inst, len(cs), 'bars drift', round((cs[-1][4]/cs[0][4]-1)*100, 1), '%')
    for sig, tp, sl, hor, mk in [('none', 25, 1.0, 20, False), ('dip', 25, 1.0, 20, False), ('deepdip', 25, 1.0, 20, False),
                                 ('dip_bounce', 25, 1.0, 20, False), ('deepdip', 40, 1.5, 40, False), ('deepdip', 40, 1.5, 40, True),
                                 ('dip_bounce', 40, 1.5, 40, True), ('mom', 25, 1.0, 20, False)]:
        S(f"{sig} tp{tp} sl{sl} h{hor} {'maker' if mk else 'taker'}", run(cs, sp, sig, tp, sl, hor, mk))
