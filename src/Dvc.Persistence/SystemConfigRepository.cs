using System.Globalization;
using Dapper;
using Dvc.Core;

namespace Dvc.Persistence;

/// <summary>System_Config 表仓储（键值对）。</summary>
public sealed class SystemConfigRepository(AppDb db)
{
    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        using var conn = db.Open();
        return await conn.QueryFirstOrDefaultAsync<string>(
            new CommandDefinition("SELECT Value FROM System_Config WHERE Key = @key", new { key }, cancellationToken: ct));
    }

    public async Task<Dictionary<string, string>> GetAllAsync(CancellationToken ct = default)
    {
        using var conn = db.Open();
        var rows = await conn.QueryAsync<(string Key, string Value)>(
            new CommandDefinition("SELECT Key, Value FROM System_Config", cancellationToken: ct));
        return rows.ToDictionary(r => r.Key, r => r.Value);
    }

    public async Task SetAsync(string key, string value, CancellationToken ct = default)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO System_Config(Key, Value, UpdatedAtUtc) VALUES (@key, @value, @now)
            ON CONFLICT(Key) DO UPDATE SET Value = @value, UpdatedAtUtc = @now
            """,
            new { key, value, now = IsoNow() }, transaction: tx, cancellationToken: ct));
        tx.Commit();
    }

    public async Task SetManyAsync(IEnumerable<KeyValuePair<string, string>> kvs, CancellationToken ct = default)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        foreach (var kv in kvs)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO System_Config(Key, Value, UpdatedAtUtc) VALUES (@key, @value, @now)
                ON CONFLICT(Key) DO UPDATE SET Value = @value, UpdatedAtUtc = @now
                """,
                new { key = kv.Key, value = kv.Value, now = IsoNow() },
                transaction: tx, cancellationToken: ct));
        }

        tx.Commit();
    }

    public async Task DeleteManyAsync(IReadOnlyList<string> keys, CancellationToken ct = default)
    {
        if (keys.Count == 0)
        {
            return;
        }

        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM System_Config WHERE Key IN @keys",
            new { keys }, transaction: tx, cancellationToken: ct));
        tx.Commit();
    }

    internal static string IsoNow() => DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
}

/// <summary>通用时间/枚举格式化助手（SQLite 无原生 datetime/bool，统一 TEXT 存储）。</summary>
public static class SqliteFmt
{
    public static string Iso(DateTime utc) => utc.ToString("o", CultureInfo.InvariantCulture);

    public static DateTime FromIso(string iso) => DateTime.Parse(iso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    public static int Bool(bool b) => b ? 1 : 0;
}
