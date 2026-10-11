using Dapper;
using Dvc.Core;
using Dvc.Core.Trading;

namespace Dvc.Persistence;

/// <summary>Agent_Thought_Log 表仓储。</summary>
public sealed class ThoughtRepository(AppDb db)
{
    public async Task<long> InsertAsync(ThoughtRecord t, CancellationToken ct = default)
    {
        using var conn = db.Open();
        const string sql = """
            INSERT INTO Agent_Thought_Log(
                SessionId, CreatedAtUtc, TriggerReason, RequestJson, ThinkingText,
                DecisionJson, Diagnosis, ActionType, RiskVetoed, RiskReason, EquityAfter)
            VALUES (
                @SessionId, @CreatedAtUtc, @TriggerReason, @RequestJson, @ThinkingText,
                @DecisionJson, @Diagnosis, @ActionType, @RiskVetoed, @RiskReason, @EquityAfter);
            SELECT last_insert_rowid();
            """;
        return await conn.ExecuteScalarAsync<long>(new CommandDefinition(sql, new
        {
            t.SessionId,
            CreatedAtUtc = SqliteFmt.Iso(t.CreatedAtUtc),
            t.TriggerReason,
            t.RequestJson,
            t.ThinkingText,
            t.DecisionJson,
            t.Diagnosis,
            t.ActionType,
            RiskVetoed = SqliteFmt.Bool(t.RiskVetoed),
            t.RiskReason,
            t.EquityAfter,
        }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<ThoughtRecord>> ListRecentAsync(int limit = 30, CancellationToken ct = default)
    {
        using var conn = db.Open();
        var rows = await conn.QueryAsync<ThoughtRow>(new CommandDefinition(
            "SELECT * FROM Agent_Thought_Log ORDER BY Id DESC LIMIT @limit", new { limit }, cancellationToken: ct));
        return rows.Select(Unmap).ToList();
    }

    private static ThoughtRecord Unmap(ThoughtRow r) => new()
    {
        Id = r.Id,
        SessionId = r.SessionId,
        CreatedAtUtc = SqliteFmt.FromIso(r.CreatedAtUtc),
        TriggerReason = r.TriggerReason,
        RequestJson = r.RequestJson,
        ThinkingText = r.ThinkingText,
        DecisionJson = r.DecisionJson,
        Diagnosis = r.Diagnosis,
        ActionType = r.ActionType,
        RiskVetoed = r.RiskVetoed != 0,
        RiskReason = r.RiskReason,
        EquityAfter = r.EquityAfter,
    };

    private sealed class ThoughtRow
    {
        public long Id { get; set; }
        public string SessionId { get; set; } = "";
        public string CreatedAtUtc { get; set; } = "";
        public string TriggerReason { get; set; } = "";
        public string RequestJson { get; set; } = "";
        public string ThinkingText { get; set; } = "";
        public string DecisionJson { get; set; } = "";
        public string Diagnosis { get; set; } = "";
        public string ActionType { get; set; } = "";
        public int RiskVetoed { get; set; }
        public string RiskReason { get; set; } = "";
        public decimal EquityAfter { get; set; }
    }
}
