import hmac
import base64
import json
import urllib.request
from datetime import datetime, timezone

with open('src/Dvc.Web/appsettings.Live.json', 'r', encoding='utf-8') as f:
    cfg = json.load(f)

okx = cfg['Trading']['Okx']
api_key = okx['ApiKey']
secret_key = okx['SecretKey']
passphrase = okx['Passphrase']

def okx_get(path):
    now_iso = datetime.now(timezone.utc).strftime('%Y-%m-%dT%H:%M:%S.%f')[:-3] + 'Z'
    method = 'GET'
    msg = f"{now_iso}{method}{path}"
    sig = base64.b64encode(hmac.new(secret_key.encode('utf-8'), msg.encode('utf-8'), 'sha256').digest()).decode('utf-8')
    headers = {
        'OK-ACCESS-KEY': api_key,
        'OK-ACCESS-SIGN': sig,
        'OK-ACCESS-TIMESTAMP': now_iso,
        'OK-ACCESS-PASSPHRASE': passphrase,
        'User-Agent': 'Mozilla/5.0'
    }
    req = urllib.request.Request('https://www.okx.com' + path, headers=headers)
    with urllib.request.urlopen(req, timeout=10) as r:
        return json.loads(r.read().decode('utf-8'))

# Check fills for TRUMP-USDT
fills = okx_get('/api/v5/trade/fills-history?instType=SPOT&instId=TRUMP-USDT')
print("Fills History (last 3 months):")
for f in fills.get('data', []):
    ts = datetime.fromtimestamp(int(f['fillTime'])/1000).strftime('%Y-%m-%d %H:%M:%S')
    print(f"  [{ts}] Side: {f['side']} | Px: {f['fillPx']} | Sz: {f['fillSz']} | Fee: {f['fee']} {f['feeCcy']}")

# Also check fills archive or older if empty
if not fills.get('data'):
    fills_recent = okx_get('/api/v5/trade/fills?instType=SPOT&instId=TRUMP-USDT')
    print("Fills Recent:")
    for f in fills_recent.get('data', []):
        ts = datetime.fromtimestamp(int(f['fillTime'])/1000).strftime('%Y-%m-%d %H:%M:%S')
        print(f"  [{ts}] Side: {f['side']} | Px: {f['fillPx']} | Sz: {f['fillSz']} | Fee: {f['fee']} {f['feeCcy']}")
