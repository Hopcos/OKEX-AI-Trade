import json, hmac, hashlib, base64, datetime, urllib.request, sys
sys.stdout.reconfigure(encoding='utf-8')
o = json.load(open('src/Dvc.Web/appsettings.Live.json', encoding='utf-8'))['Trading']['Okx']
def call(path):
    ts = datetime.datetime.now(datetime.timezone.utc).strftime('%Y-%m-%dT%H:%M:%S.') + f"{datetime.datetime.now(datetime.timezone.utc).microsecond//1000:03d}Z"
    sig = base64.b64encode(hmac.new(o['SecretKey'].encode(), (ts + 'GET' + path).encode(), hashlib.sha256).digest()).decode()
    h = {'OK-ACCESS-KEY': o['ApiKey'], 'OK-ACCESS-SIGN': sig, 'OK-ACCESS-TIMESTAMP': ts, 'OK-ACCESS-PASSPHRASE': o['Passphrase'], 'User-Agent': 'Mozilla/5.0'}
    return json.loads(urllib.request.urlopen(urllib.request.Request('https://www.okx.com' + path, headers=h), timeout=15).read())
print(json.dumps(call('/api/v5/account/trade-fee?instType=SPOT&instId=TRUMP-USDT'), indent=1))
d = call('/api/v5/trade/fills-history?instType=SPOT&instId=TRUMP-USDT&limit=5')
for f in d.get('data', []):
    print(f['side'], f['fillPx'], f['fillSz'], 'fee', f['fee'], f['feeCcy'], 'role', f['execType'])
