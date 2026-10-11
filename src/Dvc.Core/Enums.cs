namespace Dvc.Core;

/// <summary>交易频次策略开关。</summary>
public enum TradingMode
{
    /// <summary>高频盘口微套（默认）：订阅 Orderbook + Ticks，秒/毫秒级盘口微操。</summary>
    Scalping = 0,

    /// <summary>中短线波段：降频订阅 15M/1H K 线，追踪趋势。</summary>
    Swing = 1,
}

/// <summary>标的选择范围开关。</summary>
public enum AssetScope
{
    /// <summary>单高流代币（默认），API 频次消耗与计算负载最低。</summary>
    SinglePair = 0,

    /// <summary>多币种轮动：AI 资金调度算法决定独占聚焦币种。</summary>
    MultiPair = 1,
}

/// <summary>行情与交易数据源模式（双模式可切换）。</summary>
public enum DataSourceMode
{
    /// <summary>模拟行情 + 纸面撮合（离线可完整跑通闭环，默认）。</summary>
    Paper = 0,

    /// <summary>真实 OKX：公共 WebSocket 行情 + REST 现货限价交易。</summary>
    Live = 1,
}

/// <summary>AI 决策引擎提供方。</summary>
public enum AiProvider
{
    /// <summary>内置规则 MockAI（离线演示/测试，支持思考链打字机流式输出）。</summary>
    Mock = 0,

    /// <summary>OpenAI 兼容 Chat Completions 网关（流式 + reasoning_content 思考链）。</summary>
    DeepSeek = 1,

    /// <summary>Intern-Decision 结构化决策服务（POST /v1/decisions，state + questions）。</summary>
    InternDecision = 2,
}

public enum OrderSide
{
    Buy = 0,
    Sell = 1,
}

public enum OrderStatus
{
    /// <summary>挂单中（已下发交易所/纸面簿）。</summary>
    Pending = 0,
    /// <summary>已成交。</summary>
    Filled = 1,
    /// <summary>已撤销（主动认赔撤单）。</summary>
    Cancelled = 2,
    /// <summary>被风控/交易所拒绝。</summary>
    Rejected = 3,
    /// <summary>等待提交（AI 决策已批准，排队执行）。</summary>
    Queued = 4,
}

/// <summary>决策唤醒原因（四关键节点中的触发轨）。</summary>
public enum DecisionTrigger
{
    /// <summary>策略节拍（Scalping 秒级 / Swing 分钟级或 K 线收盘）。</summary>
    StrategyTick = 0,
    /// <summary>每小时宏观评估（时间轮询）。</summary>
    MacroHourly = 1,
    /// <summary>亏损事件紧急唤醒（最高优先级）。</summary>
    LossEventWakeup = 2,
    /// <summary>系统启动自检。 </summary>
    Startup = 3,
}

/// <summary>系统全局运行状态（System_Config -> run_state）。</summary>
public enum SystemRunState
{
    Running = 0,

    /// <summary>硬熔断：权益 &lt; 初始本金*70%，已清仓并永久终止 AI 进程。</summary>
    Halted = 1,

    Stopped = 2,
}

/// <summary>AI 决策动作类型（Immediate_Execution_Order.Action）。</summary>
public enum AiActionType
{
    /// <summary>观望，不动作。</summary>
    HOLD,
    /// <summary>挂限价买单。</summary>
    PLACE_LIMIT_BUY,
    /// <summary>挂限价卖单。</summary>
    PLACE_LIMIT_SELL,
    /// <summary>撤销指定挂单。</summary>
    CANCEL_ORDER,
    /// <summary>撤销盘口所有残余挂单，锁定 USD。</summary>
    CANCEL_ALL_AND_WAIT,
    /// <summary>切换模式 / 目标配置（配合 Target_Config_Status）。</summary>
    MODE_SWITCH,
}

/// <summary>引擎事件种类（Channel 管道 + SignalR 推送）。</summary>
public enum EngineEventKind
{
    /// <summary>市场快照更新（节流后推送用于看板）。</summary>
    MarketUpdate,
    /// <summary>AI 思考链增量（打字机效果数据源）。</summary>
    ThoughtChunk,
    /// <summary>AI 最终决策完成。</summary>
    Decision,
    /// <summary>订单事件（挂单/成交/撤单）。</summary>
    OrderEvent,
    /// <summary>风控拦截（盾牌计数器）。</summary>
    RiskIntercept,
    /// <summary>策略开关切换。</summary>
    ModeChanged,
    /// <summary>账户资产快照。</summary>
    AccountSnapshot,
    /// <summary>触发原因/诊断等结构化日志。</summary>
    FlowLog,
    /// <summary>系统级硬熔断。</summary>
    SystemHalted,
}
