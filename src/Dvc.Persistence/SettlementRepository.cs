using Dapper;
using Dvc.Core;
using Dvc.Core.Trading;

namespace Dvc.Persistence;

/// <summary>Asset_Snapshot 表仓储（账户镜像）。</summary>
public sealed class SettlementRepository(AppDb db)
{
    public async Task<long> InsertAsync(AccountSettlement s, CancellationToken ct = default)
    {
        using var conn = db.Open();
        const string sql = """
            INSERT INTO Asset_Snapshot(
                SnapshotAtUtc, Kind, InitialCapital, AvailableUsd, EquityUsd,
                RealizedPnl, MaxOrderSize, HoldingsJson, LastPricesJson)
            VALUES (
                @SnapshotAtUtc, @Kind, @InitialCapital, @AvailableUsd, @EquityUsd,
                @RealizedPnl, @MaxOrderSize, @HoldingsJson, @LastPricesJson);
            SELECT last_insert_rowid();
            """;
        return await conn.ExecuteScalarAsync<long>(new CommandDefinition(sql, new
        {
            SnapshotAtUtc = SqliteFmt.Iso(s.SnapshotAtUtc),
            s.Kind,
            s.InitialCapital,
            s.AvailableUsd,
            s.EquityUsd,
            s.RealizedPnl,
            s.MaxOrderSize,
            s.HoldingsJson,
            s.LastPricesJson,
        }, cancellationToken: ct));
    }

    public async Task<AccountSettlement?> LatestAsync(CancellationToken ct = default)
    {
        using var conn = db.Open();
        var rows = await conn.QueryAsync<SettlementRow>(new CommandDefinition(
            "SELECT * FROM Asset_Snapshot ORDER BY Id DESC LIMIT 1", cancellationToken: ct));
        var r = rows.FirstOrDefault();
        return r is null ? null : Unmap(r);
    }

    public async Task<IReadOnlyList<AccountSettlement>> ListRecentAsync(int limit = 200, CancellationToken ct = default)
    {
        using var conn = db.Open();
        var rows = await conn.QueryAsync<SettlementRow>(new CommandDefinition(
            "SELECT * FROM Asset_Snapshot ORDER BY Id DESC LIMIT @limit", new { limit }, cancellationToken: ct));
        return rows.Select(Unmap).ToList();
    }

    private static AccountSettlement Unmap(SettlementRow r) => new()
    {
        Id = r.Id,
        SnapshotAtUtc = SqliteFmt.FromIso(r.SnapshotAtUtc),
        Kind = r.Kind,
        InitialCapital = r.InitialCapital,
        AvailableUsd = r.AvailableUsd,
        EquityUsd = r.EquityUsd,
        RealizedPnl = r.RealizedPnl,
        MaxOrderSize = r.MaxOrderSize,
        HoldingsJson = r.HoldingsJson,
        LastPricesJson = r.LastPricesJson,
    };

    private sealed class SettlementRow
    {
        public long Id { get; set; }
        public string SnapshotAtUtc { get; set; } = "";
        public string Kind { get; set; } = "";
        public decimal InitialCapital { get; set; }
        public decimal AvailableUsd { get; set; }
        public decimal EquityUsd { get; set; }
        public decimal RealizedPnl { get; set; }
        public decimal MaxOrderSize { get; set; }
        public string HoldingsJson { get; set; } = "{}";
        public string LastPricesJson { get; set; } = "{}";
    }
}
