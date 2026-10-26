using System.Text.Json.Serialization;

namespace Dvc.Core.Ai;

/// <summary>
/// 大脑（LLM）复盘产出的"策略指引"：约束并调教 InternDecision 的下一阶段行为。
/// 同一结构既是 LLM 的输出 JSON，也会随输入快照（Brain_Directive）交给 InternDecision。
/// 所有数值字段都会在落地前被引擎钳制到安全范围——大脑只能让系统更谨慎或更聚焦盈利，
/// 不能突破硬编码风控（单笔上限/熔断线/黑名单）。
/// </summary>
public sealed class BrainDirective
{
    /// <summary>姿态：AGGRESSIVE / BALANCED / DEFENSIVE / CAPITAL_PRESERVATION。</summary>
    [JsonPropertyName("Posture")]
    public string Posture { get; set; } = "BALANCED";

    /// <summary>风险档位上限（0~1）：InternDecision 选出的下单系数不得超过它。</summary>
    [JsonPropertyName("Max_Risk_Ratio")]
    public decimal MaxRiskRatio { get; set; } = 1m;

    /// <summary>是否允许开新买单（false = 只许卖出/观望，用于连续亏损或趋势暴跌时止血）。</summary>
    [JsonPropertyName("Allow_New_Buys")]
    public bool AllowNewBuys { get; set; } = true;

    /// <summary>
    /// 硬止血（引擎判定，非 LLM 输出）：true = 即使空仓也禁止新买（权益回撤已逼近熔断线）。
    /// false 时 <see cref="AllowNewBuys"/>=false 只在有持仓时生效（逼出卖单、回笼现金），空仓永远不会被冻死。
    /// </summary>
    [JsonPropertyName("Hard_Stop")]
    public bool HardStop { get; set; }

    /// <summary>当前是否应拦截新买单：硬止血一律拦；普通"禁止新买"仅在有持仓时拦。</summary>
    public bool BlocksBuys(bool holding) => !AllowNewBuys && (HardStop || holding);

    /// <summary>卖出必须达到的净利润（bps，覆盖 Profit:MinNetProfitBps）；null = 用配置值。</summary>
    [JsonPropertyName("Min_Net_Profit_Bps")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? MinNetProfitBps { get; set; }

    /// <summary>止损线（%，覆盖 Profit:StopLossPercent）；null = 用配置值。</summary>
    [JsonPropertyName("Stop_Loss_Percent")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? StopLossPercent { get; set; }

    /// <summary>给 InternDecision 的文字指引（会拼进它的 state 文本，让决策中心"读懂"当前战略重心）。</summary>
    [JsonPropertyName("Guidance")]
    public string Guidance { get; set; } = "";

    /// <summary>复盘归因（正常概率摩擦 / 策略失效 / 趋势扭转…），仅用于日志与复盘页。</summary>
    [JsonPropertyName("Reasoning")]
    public string Reasoning { get; set; } = "";

    /// <summary>来源：LLM（大脑 LLM 复盘）/ RULE（LLM 不可用时的规则复盘）。</summary>
    [JsonPropertyName("Source")]
    public string Source { get; set; } = "";

    /// <summary>触发本次复盘的阶段（STARTUP / MACRO_HOURLY / LOSS_EVENT_WAKEUP / PERIODIC）。</summary>
    [JsonPropertyName("Trigger")]
    public string Trigger { get; set; } = "";

    [JsonPropertyName("Created_At")]
    public DateTime CreatedAtUtc { get; set; }
}
