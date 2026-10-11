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

    public decimal InitialCapital { get; set; } = 20m;

    /// <summary>SQLite 数据库文件路径（可绝对路径；相对路径基于进程工作目录解析）。</summary>
    public string DbPath { get; set; } = "data/trading.db";

    public List<string> WatchSymbols { get; set; } =
        ["BTC-USDT", "ETH-USDT", "SOL-USDT", "XRP-USDT", "BNB-USDT", "DOGE-USDT", "ADA-USDT", "AVAX-USDT", "LINK-USDT", "TON-USDT"];

    public List<string> Blacklist { get; set; } = ["LUNA", "FTT", "MEME1"];

    /// <summary>盈利阶梯下单比例：单笔最大 = (初始本金 + max(0, 权益-初始本金)) * 20%。</summary>
    public decimal MaxOrderSizeRatio { get; set; } = 0.20m;

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
    public string BaseUrl { get; set; } = "http://localhost:8000";

    public string ApiKey { get; set; } = "";

    public double Temperature { get; set; } = 1.0;

    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>评分型问题档位 → 下单额度系数映射（保守/中性/激进）。</summary>
    public double[] RiskAppetiteRatios { get; set; } = [0.25, 0.5, 1.0];
}
