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

now_iso = datetime.now(timezone.utc).strftime('%Y-%m-%dT%H:%M:%S.%f')[:-3] + 'Z'
method = 'GET'
path = '/api/v5/account/balance'
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
    data = json.loads(r.read().decode('utf-8'))

bal = data['data'][0]
details = bal.get('details', [])
print("Details count:", len(details))
for d in details:
    ccy = d.get('ccy')
    avail = d.get('availBal')
    eq = d.get('eq')
    print(f"ccy='{ccy}', eq='{eq}', availBal='{avail}'")
