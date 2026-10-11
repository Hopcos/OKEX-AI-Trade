using Dapper;
using Dvc.Core;
using Dvc.Core.Trading;

namespace Dvc.Persistence;

/// <summary>Risk_Intercept 表仓储（风控盾牌计数器）。</summary>
public sealed class RiskInterceptRepository(AppDb db)
{
    public async Task<long> InsertAsync(RiskInterceptRecord r, CancellationToken ct = default)
    {
        using var conn = db.Open();
        const string sql = """
            INSERT INTO Risk_Intercept(CreatedAtUtc, Kind, Symbol, RequestedAmount, AllowedAmount, Detail)
            VALUES (@CreatedAtUtc, @Kind, @Symbol, @RequestedAmount, @AllowedAmount, @Detail);
            SELECT last_insert_rowid();
            """;
        return await conn.ExecuteScalarAsync<long>(new CommandDefinition(sql, new
        {
            CreatedAtUtc = SqliteFmt.Iso(r.CreatedAtUtc),
            r.Kind,
            r.Symbol,
            r.RequestedAmount,
            r.AllowedAmount,
            r.Detail,
        }, cancellationToken: ct));
    }

    public async Task<long> CountAsync(CancellationToken ct = default)
    {
        using var conn = db.Open();
        return await conn.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(*) FROM Risk_Intercept", cancellationToken: ct));
    }

    public async Task<IReadOnlyList<RiskInterceptRecord>> ListRecentAsync(int limit = 20, CancellationToken ct = default)
    {
        using var conn = db.Open();
        var rows = await conn.QueryAsync<InterceptRow>(new CommandDefinition(
            "SELECT * FROM Risk_Intercept ORDER BY Id DESC LIMIT @limit", new { limit }, cancellationToken: ct));
        return rows.Select(r => new RiskInterceptRecord
        {
            Id = r.Id,
            CreatedAtUtc = SqliteFmt.FromIso(r.CreatedAtUtc),
            Kind = r.Kind,
            Symbol = r.Symbol,
            RequestedAmount = r.RequestedAmount,
            AllowedAmount = r.AllowedAmount,
            Detail = r.Detail,
        }).ToList();
    }

    private sealed class InterceptRow
    {
        public long Id { get; set; }
        public string CreatedAtUtc { get; set; } = "";
        public string Kind { get; set; } = "";
        public string Symbol { get; set; } = "";
        public decimal RequestedAmount { get; set; }
        public decimal AllowedAmount { get; set; }
        public string Detail { get; set; } = "";
    }
}
