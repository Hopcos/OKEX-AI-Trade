import json
import numpy as np
import pandas as pd

with open('data/market_history/TRUMP_1m_full.json', 'r', encoding='utf-8') as f:
    c1m = json.load(f)

df1m = pd.DataFrame(c1m, columns=['ts', 'open', 'high', 'low', 'close', 'vol', 'volCcy', 'volCcyQuote', 'confirm'])
for col in ['open', 'high', 'low', 'close', 'vol', 'volCcy']:
    df1m[col] = df1m[col].astype(float)
df1m['datetime'] = pd.to_datetime(df1m['ts'].astype(int), unit='ms')
df1m.sort_values('datetime', inplace=True)
df1m.reset_index(drop=True, inplace=True)

print("=== TRUMP 1-Minute Candle Analysis ===")
print(f"Candles count: {len(df1m)}")
print(f"Time range: {df1m['datetime'].min()} to {df1m['datetime'].max()} (UTC)")
print(f"Price range: Low={df1m['low'].min():.4f}, High={df1m['high'].max():.4f}, Start={df1m['open'].iloc[0]:.4f}, End={df1m['close'].iloc[-1]:.4f}")

df1m['high_low_spread_pct'] = (df1m['high'] - df1m['low']) / df1m['low'] * 100
df1m['close_pct_change'] = df1m['close'].pct_change() * 100

print(f"Average 1m High-Low Range: {df1m['high_low_spread_pct'].mean():.3f}% ({df1m['high_low_spread_pct'].mean()*100:.1f} bps)")
print(f"Median 1m High-Low Range: {df1m['high_low_spread_pct'].median():.3f}% ({df1m['high_low_spread_pct'].median()*100:.1f} bps)")
print(f"75th percentile 1m Range: {df1m['high_low_spread_pct'].quantile(0.75):.3f}%")
print(f"90th percentile 1m Range: {df1m['high_low_spread_pct'].quantile(0.90):.3f}%")
print(f"1m Volume in USDT: Mean=${df1m['volCcy'].mean():.1f}, Median=${df1m['volCcy'].median():.1f}")

with open('data/market_history/TRUMP_5m_full.json', 'r', encoding='utf-8') as f:
    c5m = json.load(f)
df5m = pd.DataFrame(c5m, columns=['ts', 'open', 'high', 'low', 'close', 'vol', 'volCcy', 'volCcyQuote', 'confirm'])
for col in ['open', 'high', 'low', 'close', 'vol', 'volCcy']:
    df5m[col] = df5m[col].astype(float)
df5m['datetime'] = pd.to_datetime(df5m['ts'].astype(int), unit='ms')
df5m.sort_values('datetime', inplace=True)
df5m.reset_index(drop=True, inplace=True)
df5m['range_pct'] = (df5m['high'] - df5m['low']) / df5m['low'] * 100

print("\n=== TRUMP 5-Minute Candle Analysis ===")
print(f"Candles count: {len(df5m)}")
print(f"Time range: {df5m['datetime'].min()} to {df5m['datetime'].max()} (UTC)")
print(f"Average 5m High-Low Range: {df5m['range_pct'].mean():.3f}% ({df5m['range_pct'].mean()*100:.1f} bps)")
print(f"Median 5m High-Low Range: {df5m['range_pct'].median():.3f}% ({df5m['range_pct'].median()*100:.1f} bps)")
print(f"Price range: Low={df5m['low'].min():.4f}, High={df5m['high'].max():.4f}")

with open('data/market_history/TRUMP_15m_full.json', 'r', encoding='utf-8') as f:
    c15m = json.load(f)
df15m = pd.DataFrame(c15m, columns=['ts', 'open', 'high', 'low', 'close', 'vol', 'volCcy', 'volCcyQuote', 'confirm'])
for col in ['open', 'high', 'low', 'close']:
    df15m[col] = df15m[col].astype(float)
df15m['datetime'] = pd.to_datetime(df15m['ts'].astype(int), unit='ms')
df15m.sort_values('datetime', inplace=True)
df15m.reset_index(drop=True, inplace=True)
df15m['range_pct'] = (df15m['high'] - df15m['low']) / df15m['low'] * 100

print("\n=== TRUMP 15-Minute Candle Analysis ===")
print(f"Candles count: {len(df15m)}")
print(f"Time range: {df15m['datetime'].min()} to {df15m['datetime'].max()} (UTC)")
print(f"Average 15m High-Low Range: {df15m['range_pct'].mean():.3f}% ({df15m['range_pct'].mean()*100:.1f} bps)")
print(f"Price range: Low={df15m['low'].min():.4f}, High={df15m['high'].max():.4f}")
