import json

with open('src/Dvc.Web/appsettings.json', 'r', encoding='utf-8') as f:
    app_cfg = json.load(f)

with open('src/Dvc.Web/appsettings.Live.json', 'r', encoding='utf-8') as f:
    live_cfg = json.load(f)

print("appsettings.json WatchSymbols:")
print(app_cfg['Trading'].get('WatchSymbols'))
print("appsettings.Live.json WatchSymbols:")
print(live_cfg['Trading'].get('WatchSymbols'))
print("appsettings.Live.json ActiveSymbol:")
print(live_cfg['Trading'].get('ActiveSymbol'))
print("appsettings.Live.json DefaultAssetScope:")
print(live_cfg['Trading'].get('DefaultAssetScope'))
