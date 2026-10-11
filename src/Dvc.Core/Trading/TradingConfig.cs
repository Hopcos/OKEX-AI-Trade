using Dvc.Core;

namespace Dvc.Core.Trading;

/// <summary>
/// 运行时开关状态（即 System_Config 的镜像）。由 SQLite 持久化，启动时加载，
/// 运行中通过 TradingControlService 原子更新（更新即触发决策重评估）。
/// </summary>
public sealed class TradingConfig
{
    public SystemRunState RunState { get; set; } = SystemRunState.Running;

    public TradingMode TradingMode { get; set; } = TradingMode.Scalping;

    public AssetScope AssetScope { get; set; } = AssetScope.SinglePair;

    /// <summary>当前聚焦交易对（如 BTC-USDT）。</summary>
    public string ActiveSymbol { get; set; } = "BTC-USDT";

    /// <summary>
    /// 聚焦标的锁定（仅运行时内存态，不落库）：true = 用户在界面手动指定，
    /// AI 决策输出不得覆盖聚焦标的；false = AI 可按期望值自动切换聚焦。
    /// </summary>
    public bool FocusLocked { get; set; }

    public DataSourceMode DataSourceMode { get; set; } = DataSourceMode.Paper;

    public AiProvider AiProvider { get; set; } = AiProvider.Mock;

    /// <summary>初始本金（风控公式基准）。</summary>
    public decimal InitialCapital { get; set; } = 20m;

    /// <summary>上一周期结算总权益（亏损唤醒判定基准）。</summary>
    public decimal LastSettlementEquity { get; set; }

    /// <summary>风控盾牌累计拦截次数。</summary>
    public long ShieldCount { get; set; }

    /// <summary>黑名单（AI 幻觉聚焦/买入一票否决）。</summary>
    public List<string> Blacklist { get; set; } = [];

    /// <summary>Multi-Pair 轮动观察池。</summary>
    public List<string> WatchSymbols { get; set; } = [];

    /// <summary>看板皮肤（cyberpunk | classic | mint）。</summary>
    public string Theme { get; set; } = "cyberpunk";
}
