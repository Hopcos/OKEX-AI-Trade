using Dvc.Core;

namespace Dvc.Core.Configuration;

/// <summary>
/// 系统全局配置（appsettings.json 绑定；启动时种子化 System_Config 表，
/// 运行中改由 SQLite + TradingControlService 驱动）。
/// </summary>
public sealed class TradingOptions
{
    public const string SectionName = "Trading";

    public DataSourceMode DataSourceMode { get; set; } = DataSourceMode.Paper;

    public AiProvider AiProvider { get; set; } = AiProvider.Mock;

    /// <summary>
    /// 强制开关：为 true 时所有决策一律使用 InternDecision 提供方，
    /// 无视 AiProvider 配置与看板热切换（启动即对齐 DB/横幅，每轮决策也强制拦截）。
    /// </summary>
    public bool ForceInternDecision { get; set; }

    public TradingMode DefaultTradingMode { get; set; } = TradingMode.Scalping;

    public AssetScope DefaultAssetScope { get; set; } = AssetScope.SinglePair;

    public string ActiveSymbol { get; set; } = "BTC-USDT";

    /// <summary>
    /// 本金预算（USD）：系统只动用这一笔钱——下单、盈亏、权益、风控上限、熔断线全部按它核算
    /// （Live 模式下即使交易所账户余额更大，也只在此预算内交易，其余资金视为不存在）。
    /// </summary>
    public decimal InitialCapital { get; set; } = 20m;

    /// <summary>
    /// 预算账本起算时间（UTC，仅 Live 生效；空 = 重放库内全部成交）。
    /// 库里混有旧的 Paper 成交或本金调整前的成交时，设为"从现在起"，账本只统计该时间之后创建的订单。
    /// </summary>
    public DateTime? LedgerStartUtc { get; set; }

    /// <summary>SQLite 数据库文件路径（可绝对路径；相对路径基于进程工作目录解析）。</summary>
    public string DbPath { get; set; } = "data/trading.db";

    public List<string> WatchSymbols { get; set; } =
        ["BTC-USDT", "ETH-USDT", "SOL-USDT", "XRP-USDT", "BNB-USDT", "DOGE-USDT", "ADA-USDT", "AVAX-USDT", "LINK-USDT", "TON-USDT"];

    public List<string> Blacklist { get; set; } = ["LUNA", "FTT", "MEME1"];

    /// <summary>单笔买单上限比例：单笔最大 = 当前总权益（本金 ± 已实现/浮动盈亏）× 该比例；盈利放大、亏损自动缩小。</summary>
    public decimal MaxOrderSizeRatio { get; set; } = 0.20m;

    /// <summary>
    /// 总仓位上限比例：持仓市值 + 挂买单冻结 ≤ 当前总权益 × 该比例（默认 0.6）。
    /// 永远保留一部分现金，保证"买入 → 卖出回笼现金 → 再买入"的循环能转起来，而不是一把买满后无事可做。
    /// </summary>
    public decimal MaxExposureRatio { get; set; } = 0.60m;

    /// <summary>单笔买单绝对上限（USD，0 = 不启用）：与"权益 × 比例"取较小者，用于给最大下单金额再加一道硬顶。</summary>
    public decimal MaxOrderUsd { get; set; }

    /// <summary>挂单（maker）单边手续费（bps，OKX 现货 Lv1 实测 8 = 0.08%）：Live 账本里买入按此计费（买单都是挂单成交）。</summary>
    public decimal MakerFeeBps { get; set; } = 8m;

    /// <summary>吃单（taker）单边手续费（bps，OKX 现货 Lv1 实测 10 = 0.10%）。</summary>
    public decimal TakerFeeBps { get; set; } = 10m;

    /// <summary>保守费率 = max(maker, taker)：用于 Paper 撮合与保本价，宁可多要 2bp，也不会因费率估低而亏钱卖出。</summary>
    public decimal FeeBps => Math.Max(MakerFeeBps, TakerFeeBps);

    /// <summary>硬熔断阈值：总权益 &lt; 初始本金 * 70% 触发清仓停机。</summary>
    public decimal CircuitBreakerRatio { get; set; } = 0.70m;

    /// <summary>AI 调用最小间隔（秒）：低优先级策略节拍不足此间隔将被丢弃，防 LLM 网关 429 限流；高优先级（亏损/宏观/控制）不节流；Mock 提供方不生效。</summary>
    public double AiMinIntervalSeconds { get; set; } = 4.0;

    public double DefaultTimezoneOffsetHours { get; set; } = 0;

    /// <summary>单笔限价单生命周期（秒），超时进入撤单/继续等待 AI 决策。</summary>
    public int OrderTtlSeconds { get; set; } = 120;

    /// <summary>挂单价相对盘口内沿的最大偏离 bps（AI 定价偏离校验，风控否决）。</summary>
    public int MaxPriceDeviationBps { get; set; } = 250;

    public SimulationOptions Simulation { get; set; } = new();

    public OkxOptions Okx { get; set; } = new();

    public DeepSeekOptions DeepSeek { get; set; } = new();

    public InternDecisionOptions InternDecision { get; set; } = new();

    public ProfitOptions Profit { get; set; } = new();

    public BrainOptions Brain { get; set; } = new();
}

/// <summary>盈利护栏：保证"卖出必须净盈利"，除非触发止损（系统永远朝着盈利的方向）。</summary>
public sealed class ProfitOptions
{
    /// <summary>是否启用卖出盈利护栏（默认启用）。关闭后 AI 可以在任意价位认赔卖出。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>卖出价必须覆盖 持仓成本 + 卖出手续费 + 该净利润（bps，15 = 0.15%）；未达到则继续持有等待。</summary>
    public decimal MinNetProfitBps { get; set; } = 15m;

    /// <summary>止损线（%）：持仓浮亏达到该比例时允许认赔卖出（放行护栏），避免无限死扛。</summary>
    public decimal StopLossPercent { get; set; } = 3m;

    /// <summary>
    /// 自动离场（持仓管家，默认启用）：独立于 AI 的 2 秒巡检——每个持仓必有退出计划：
    /// 买一 ≥ 保本价立即止盈落袋；否则在保本价挂止盈卖单等待；浮亏触及止损线则止损离场。
    /// </summary>
    public bool AutoExit { get; set; } = true;

    /// <summary>止盈/卖单的有效期（秒，默认 600）。卖单是回笼现金的关键，比买单（OrderTtlSeconds）活得更久。</summary>
    public int SellOrderTtlSeconds { get; set; } = 600;

    /// <summary>
    /// 空转兜底（分钟，默认 3，0 = 关闭）：空仓且连续这么久没有下过任何单（AI 一直 hold）时，
    /// 即使 InternDecision 说观望，也按小仓位挂一笔试单买入，把"买→持仓管家挂保本卖单→成交回笼"的循环转起来。
    /// 不交易就没有收益；试单仍受单笔上限/总仓位/风控/价差限制，且大脑硬止血时不触发。
    /// </summary>
    public int IdleProbeMinutes { get; set; } = 3;

    /// <summary>
    /// 空转试单允许的最大价差（bps，默认 8）：试单直接吃卖一价（保证入场），价差就是成本，
    /// 价差过宽的币种不试单。
    /// </summary>
    public decimal IdleProbeMaxSpreadBps { get; set; } = 8m;

    /// <summary>
    /// 止盈目标衰减时长（分钟，默认 30，0 = 不衰减）：持仓越久，要求的净利润越低，
    /// 这么久之后降到 <see cref="MinNetProfitFloorBps"/>。避免一笔仓位占着额度干等够不到的目标价而没有周转。
    /// </summary>
    public int TargetDecayMinutes { get; set; } = 30;

    /// <summary>下跌趋势不开新仓（默认开）：15M 趋势为 BEARISH/BEARISH_STRONG 时拒绝买入——手续费 20bp 往返，逆势接刀期望为负。</summary>
    public bool BlockBuysInDowntrend { get; set; } = true;

    /// <summary>禁止摊低成本（%，默认 0.3，0 = 关闭）：已持仓且现价低于均价超过该比例时不再加仓，避免越跌越买、把整笔本金套死。</summary>
    public decimal NoAverageDownPercent { get; set; } = 0.3m;

    /// <summary>同一交易对两次买入的最小间隔（秒，默认 180，0 = 关闭）：防止同一信号连续 14 笔满额扫货。</summary>
    public int BuyCooldownSeconds { get; set; } = 180;

    /// <summary>
    /// 高位不追（%，默认 0 = 关闭）：现价处于近 1 小时价格区间的位置（0 = 区间最低，100 = 最高）高于该值时拒绝买入。
    /// 例：80 表示只在区间下方 80% 内买入，不在近 1 小时最高的 20% 区域追高。
    /// 对空转试单同样生效。区间样本不足或波幅小于 <see cref="MinRangeForPositionGuardPercent"/> 时不判断。
    /// </summary>
    public decimal MaxBuyRangePositionPercent { get; set; }

    /// <summary>
    /// 高位不追的最小有效波幅（%，默认 0.2）：近 1 小时 (max-min)/min 低于该值时区间过窄，
    /// "位置"没有意义（几个 tick 就会从 0% 跳到 100%），此时放行，不启用高位护栏。
    /// </summary>
    public decimal MinRangeForPositionGuardPercent { get; set; } = 0.2m;

    /// <summary>止盈目标衰减到底的净利润（bps，默认 2）：仍保证扣完双边手续费后不亏钱。</summary>
    public decimal MinNetProfitFloorBps { get; set; } = 2m;

    /// <summary>
    /// 空转试单允许同时持有的最大仓位数（币种数，默认 3）：已持有一个币、又空转时，
    /// 会在候选池里再挑一个价差窄、无持仓的币小仓位试单，多路并行周转；达到上限后不再试。
    /// </summary>
    public int MaxOpenPositions { get; set; } = 3;
}

/// <summary>
/// 大脑（LLM）复盘配置：InternDecision 模式下，由 LLM 定期/在关键阶段复盘战绩，
/// 产出"策略指引"（风险上限/是否允许新买/保本利润/止损线/文字指引）约束并调教 InternDecision。
/// 连接信息留空则复用 <see cref="DeepSeekOptions"/>；LLM 不可用时自动退回规则复盘。
/// </summary>
public sealed class BrainOptions
{
    /// <summary>是否启用大脑复盘（默认启用；仅在实际决策提供方为 InternDecision 时生效）。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>定期复盘间隔（分钟）。</summary>
    public int ReviewIntervalMinutes { get; set; } = 20;

    /// <summary>关键阶段（启动 / 每小时宏观 / 亏损事件）是否同步触发复盘。</summary>
    public bool ReviewOnKeyEvents { get; set; } = true;

    /// <summary>两次复盘的最小间隔（秒），防止亏损事件密集时刷爆 LLM。</summary>
    public int MinReviewIntervalSeconds { get; set; } = 60;

    /// <summary>复盘时回看的最近订单条数。</summary>
    public int LookbackOrders { get; set; } = 40;

    /// <summary>指引有效期（分钟）：超期未刷新的指引视为过期，InternDecision 退回默认行为。</summary>
    public int DirectiveTtlMinutes { get; set; } = 60;

    /// <summary>单次复盘 LLM 超时（秒）。关键阶段复盘会阻塞决策至多这么久。</summary>
    public int TimeoutSeconds { get; set; } = 45;

    /// <summary>
    /// 风险档位下限（默认 0.5）：LLM 再保守，Max_Risk_Ratio 也不会被压到该值以下（规则复盘本身判定防守/保本时除外）。
    /// 防止大脑把下单额压到几乎为 0，导致"永远不交易 = 永远零收益"。
    /// </summary>
    public decimal MinRiskRatio { get; set; } = 0.5m;

    /// <summary>LLM 可在规则保本利润之上额外加码的上限（bps，默认 10）：保本利润要求过高会让卖单永远挂不出去。</summary>
    public decimal MaxExtraProfitBps { get; set; } = 5m;

    public double Temperature { get; set; } = 0.3;

    /// <summary>大脑 LLM 的 BaseUrl（OpenAI 兼容）；留空复用 DeepSeek:BaseUrl。</summary>
    public string BaseUrl { get; set; } = "";

    /// <summary>大脑 LLM 的 ApiKey；留空复用 DeepSeek:ApiKey。</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>大脑 LLM 的模型名；留空复用 DeepSeek:Model。</summary>
    public string Model { get; set; } = "";
}

/// <summary>Paper 模拟行情参数。</summary>
public sealed class SimulationOptions
{
    /// <summary>每轮行情推进间隔（毫秒）。</summary>
    public int TickIntervalMs { get; set; } = 400;

    /// <summary>基准价（模拟行情初始中间价）。</summary>
    public decimal BasePrice { get; set; } = 96000m;

    /// <summary>年化波动率（GBM sigma）。</summary>
    public double AnnualVolatility { get; set; } = 0.55;

    /// <summary>盘口价差（基准价的 bps）。</summary>
    public decimal SpreadBps { get; set; } = 6m;

    /// <summary>模拟 AI 思考链打字机每字符延迟（毫秒）。</summary>
    public int MockAiCharDelayMs { get; set; } = 6;

    /// <summary>趋势状态切换平均周期（秒）——模拟"活跃震荡/单边趋势"宏观切换。</summary>
    public double RegimeSwitchSeconds { get; set; } = 240;
}

/// <summary>OKX 实盘配置（Live 模式）。</summary>
public sealed class OkxOptions
{
    public string ApiKey { get; set; } = "";

    public string SecretKey { get; set; } = "";

    public string Passphrase { get; set; } = "";

    /// <summary>公共 WebSocket 地址。</summary>
    public string PublicWsUrl { get; set; } = "wss://ws.okx.com:8443/ws/v5/public";

    /// <summary>REST 基地址。</summary>
    public string RestBaseUrl { get; set; } = "https://www.okx.com";

    /// <summary>盘口深度（books5）。</summary>
    public string OrderBookChannel { get; set; } = "books5";

    public int ReconnectDelayMs { get; set; } = 3000;
}

/// <summary>DeepSeek 配置（OpenAI 兼容 Chat Completions）。</summary>
public sealed class DeepSeekOptions
{
    public string BaseUrl { get; set; } = "https://api.deepseek.com";

    public string ApiKey { get; set; } = "";

    public string Model { get; set; } = "deepseek-chat";

    /// <summary>使用推理模式（reasoning_content 思考链）。</summary>
    public bool UseReasoning { get; set; } = true;

    public double Temperature { get; set; } = 0.2;

    public int TimeoutSeconds { get; set; } = 60;
}

/// <summary>Intern-Decision 结构化决策服务配置（POST {BaseUrl}/v1/decisions）。</summary>
public sealed class InternDecisionOptions
{
    public string BaseUrl { get; set; } = "http://127.0.0.1:8000";

    public string ApiKey { get; set; } = "";

    public double Temperature { get; set; } = 1.0;

    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>评分型问题档位 → 下单额度系数映射（保守/中性/激进）。</summary>
    public double[] RiskAppetiteRatios { get; set; } = [0.25, 0.5, 1.0];
}

