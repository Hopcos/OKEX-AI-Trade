import urllib.request
import json

url = "https://www.okx.com/api/v5/market/ticker?instId=TRUMP-USDT"
req = urllib.request.Request(url, headers={"User-Agent": "Mozilla/5.0"})
with urllib.request.urlopen(req, timeout=10) as r:
    data = json.loads(r.read().decode('utf-8'))
    print("OKX TRUMP-USDT Ticker:", json.dumps(data['data'][0], indent=2))
