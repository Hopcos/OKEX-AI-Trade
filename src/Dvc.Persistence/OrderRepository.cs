using Dapper;
using Dvc.Core;
using Dvc.Core.Trading;

namespace Dvc.Persistence;

/// <summary>Trade_Order 表仓储。</summary>
public sealed class OrderRepository(AppDb db)
{
    public async Task<long> InsertAsync(OrderRecord o, CancellationToken ct = default)
    {
        using var conn = db.Open();
        const string sql = """
            INSERT INTO Trade_Order(
                ClientOrderId, Symbol, Side, Price, AmountUsd, BaseAmount,
                FilledBaseAmount, FilledAvgPrice, Status, ExchangeOrderId,
                Reason, RealizedPnl, CreatedAtUtc, UpdatedAtUtc)
            VALUES (
                @ClientOrderId, @Symbol, @Side, @Price, @AmountUsd, @BaseAmount,
                @FilledBaseAmount, @FilledAvgPrice, @Status, @ExchangeOrderId,
                @Reason, @RealizedPnl, @CreatedAtUtc, @UpdatedAtUtc);
            SELECT last_insert_rowid();
            """;
        return await conn.ExecuteScalarAsync<long>(new CommandDefinition(sql, Map(o), cancellationToken: ct));
    }

    public async Task UpdateAsync(OrderRecord o, CancellationToken ct = default)
    {
        using var conn = db.Open();
        const string sql = """
            UPDATE Trade_Order SET
                Symbol=@Symbol, Side=@Side, Price=@Price, AmountUsd=@AmountUsd,
                BaseAmount=@BaseAmount, FilledBaseAmount=@FilledBaseAmount,
                FilledAvgPrice=@FilledAvgPrice, Status=@Status,
                ExchangeOrderId=@ExchangeOrderId, Reason=@Reason,
                RealizedPnl=@RealizedPnl, UpdatedAtUtc=@UpdatedAtUtc
            WHERE ClientOrderId=@ClientOrderId;
            """;
        await conn.ExecuteAsync(new CommandDefinition(sql, Map(o), cancellationToken: ct));
    }

    public async Task<OrderRecord?> GetByClientOrderIdAsync(string clientOrderId, CancellationToken ct = default)
    {
        using var conn = db.Open();
        return (await conn.QueryAsync<OrderRow>(new CommandDefinition(
            "SELECT * FROM Trade_Order WHERE ClientOrderId=@clientOrderId", new { clientOrderId }, cancellationToken: ct)))
            .Select(Unmap)
            .FirstOrDefault();
    }

    public async Task<IReadOnlyList<OrderRecord>> GetOpenAsync(string? symbol = null, CancellationToken ct = default)
    {
        using var conn = db.Open();
        var rows = symbol is null
            ? await conn.QueryAsync<OrderRow>(new CommandDefinition(
                "SELECT * FROM Trade_Order WHERE Status IN ('Queued','Pending') ORDER BY Id DESC", cancellationToken: ct))
            : await conn.QueryAsync<OrderRow>(new CommandDefinition(
                "SELECT * FROM Trade_Order WHERE Symbol=@symbol AND Status IN ('Queued','Pending') ORDER BY Id DESC",
                new { symbol }, cancellationToken: ct));
        return rows.Select(Unmap).ToList();
    }

    public async Task<IReadOnlyList<OrderRecord>> ListRecentAsync(int limit = 50, CancellationToken ct = default)
    {
        using var conn = db.Open();
        var rows = await conn.QueryAsync<OrderRow>(new CommandDefinition(
            "SELECT * FROM Trade_Order ORDER BY Id DESC LIMIT @limit", new { limit }, cancellationToken: ct));
        return rows.Select(Unmap).ToList();
    }

    private static object Map(OrderRecord o) => new
    {
        o.ClientOrderId,
        o.Symbol,
        Side = o.Side.ToString(),
        o.Price,
        o.AmountUsd,
        o.BaseAmount,
        o.FilledBaseAmount,
        o.FilledAvgPrice,
        Status = o.Status.ToString(),
        o.ExchangeOrderId,
        o.Reason,
        o.RealizedPnl,
        CreatedAtUtc = SqliteFmt.Iso(o.CreatedAtUtc),
        UpdatedAtUtc = o.UpdatedAtUtc is null ? null : SqliteFmt.Iso(o.UpdatedAtUtc.Value),
    };

    private static OrderRecord Unmap(OrderRow r) => new()
    {
        Id = r.Id,
        ClientOrderId = r.ClientOrderId,
        Symbol = r.Symbol,
        Side = Enum.Parse<OrderSide>(r.Side),
        Price = r.Price,
        AmountUsd = r.AmountUsd,
        BaseAmount = r.BaseAmount,
        FilledBaseAmount = r.FilledBaseAmount,
        FilledAvgPrice = r.FilledAvgPrice,
        Status = Enum.Parse<OrderStatus>(r.Status),
        ExchangeOrderId = r.ExchangeOrderId,
        Reason = r.Reason,
        RealizedPnl = r.RealizedPnl,
        CreatedAtUtc = SqliteFmt.FromIso(r.CreatedAtUtc),
        UpdatedAtUtc = r.UpdatedAtUtc is null ? null : SqliteFmt.FromIso(r.UpdatedAtUtc),
    };

    // Dapper 行映射中间类型（避免对 sealed record 字段注入）。
    private sealed class OrderRow
    {
        public long Id { get; set; }
        public string ClientOrderId { get; set; } = "";
        public string Symbol { get; set; } = "";
        public string Side { get; set; } = "";
        public decimal Price { get; set; }
        public decimal AmountUsd { get; set; }
        public decimal BaseAmount { get; set; }
        public decimal FilledBaseAmount { get; set; }
        public decimal FilledAvgPrice { get; set; }
        public string Status { get; set; } = "";
        public string? ExchangeOrderId { get; set; }
        public string Reason { get; set; } = "";
        public decimal RealizedPnl { get; set; }
        public string CreatedAtUtc { get; set; } = "";
        public string? UpdatedAtUtc { get; set; }
    }
}
