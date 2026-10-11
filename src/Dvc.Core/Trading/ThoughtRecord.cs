using Dvc.Core;

namespace Dvc.Core.Trading;

/// <summary>AI 思考日志（Agent_Thought_Log 表行）。复盘 AI 稳定性的关键数据。</summary>
public sealed class ThoughtRecord
{
    public long Id { get; set; }

    public string SessionId { get; set; } = "";

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>触发原因（MACRO_HOURLY / LOSS_EVENT_WAKEUP / STRATEGY_TICK / STARTUP）。</summary>
    public string TriggerReason { get; set; } = "";

    /// <summary>发给 AI 的完整输入 JSON 快照（盘面盘口快照）。</summary>
    public string RequestJson { get; set; } = "";

    /// <summary>DeepSeek 思考链全文（thinking / reasoning_content）。</summary>
    public string ThinkingText { get; set; } = "";

    /// <summary>AI 最终决策 JSON（闭合后输出）。</summary>
    public string DecisionJson { get; set; } = "";

    public string Diagnosis { get; set; } = "";

    public string ActionType { get; set; } = "";

    /// <summary>是否被硬编码风控层一票否决。</summary>
    public bool RiskVetoed { get; set; }

    public string RiskReason { get; set; } = "";

    /// <summary>决策时刻总权益。</summary>
    public decimal EquityAfter { get; set; }
}
