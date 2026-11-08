using System.Globalization;
using System.Text;
using System.Text.Json;
using Dvc.Ai.Brain;
using Dvc.Core;
using Dvc.Core.Ai;
using Dvc.Core.Configuration;
using Dvc.Core.Events;
using Dvc.Core.Trading;
using Dvc.Persistence;
using Microsoft.Extensions.Hosting;

namespace Dvc.Engine;

/// <summary>
/// 大脑（战略层）：InternDecision 负责高频战术决策，大脑（LLM）负责定期/关键阶段的复盘与调教。
/// - 定期复盘（Brain:ReviewIntervalMinutes）+ 关键阶段复盘（启动 / 每小时宏观 / 亏损事件，由决策中心调用）。
/// - 复盘输入：账户盈亏、最近订单战绩、风控拦截、盘面；输出 <see cref="BrainDirective"/>，
///   经"规则底线"合并与钳制后注入 InternDecision（风险档位上限、是否允许新买、保本利润、止损线、文字指引）。
/// - LLM 未配置/超时/出错：自动退回规则复盘，保证永远有一份朝着盈利方向的指引，且 LLM 故障期冷却 2 分钟不拖慢决策。
/// </summary>
public sealed class BrainService : BackgroundService
{
    private const string SystemPrompt = """
        你是一个自动现货交易系统的"大脑"（战略层）。执行层是 Intern-Decision（高频现货限价微套的结构化决策服务），
        它每几秒决策一次；你不下单，你只做复盘并给出下一阶段的"策略指引"来调教它。

        {SOUL}

        【你的任务】
        1. 复盘最近战绩：亏损属于正常概率摩擦，还是策略/市场环境失效（例如单边下跌中仍在接刀）？
        2. 结合账户距离熔断线的安全垫、连续亏损/盈利、风控拦截情况，决定下一阶段的姿态。
        3. 输出能直接约束 Intern-Decision 的参数与一段它能"读懂"的文字指引（可含：偏好的挂单位置、何时观望、何时落袋）。

        【反空转铁律】
        - 不交易就没有收益。空仓、没有亏损、账户健康时，你必须让系统继续入场（Allow_New_Buys=true，Max_Risk_Ratio 不低于 0.5）；
          "市场清淡/不确定"不是停手的理由，应该用更小仓位、更近的保本利润去做微套，而不是冻结。
        - Allow_New_Buys=false 只用于"已有持仓、要先卖出回笼现金"或"回撤逼近熔断线"；空仓时它不会生效，别指望靠它观望。
        - Min_Net_Profit_Bps 只比规则底线最多高 5bps；定得过高卖单永远成交不了，现金就回不来。
          （系统还会让止盈目标随持仓时间自动下调，持仓越久越只要求覆盖手续费，以保证周转。）

        【硬规则】
        - 你只能让系统更谨慎或更聚焦盈利，不能突破硬编码风控；"规则底线"给出的上限你只能收紧、不能放宽。
        - 纯现货，禁止做空/杠杆；只用本金预算内的钱。
        - 只输出一个 JSON 对象，不要任何额外文字。

        【输出 JSON】
        {
          "Posture": "AGGRESSIVE|BALANCED|DEFENSIVE|CAPITAL_PRESERVATION",
          "Max_Risk_Ratio": 0.05~1.0 的小数（Intern-Decision 风险档位的上限；亏损/不确定时调低）,
          "Allow_New_Buys": true|false（false = 只许卖出/观望，用于止血）,
          "Min_Net_Profit_Bps": 数字（卖出必须达到的净利润 bps；可选，只能 ≥ 规则底线）,
          "Stop_Loss_Percent": 数字（止损线 %；可选，只能 ≤ 规则底线）,
          "Guidance": "≤200 字中文，给 Intern-Decision 的战术指引",
          "Reasoning": "≤200 字中文，复盘归因"
        }
        """;

    private readonly RuntimeState _runtime;
    private readonly AccountService _account;
    private readonly MarketContextStore _market;
    private readonly OrderRepository _orders;
    private readonly RiskInterceptRepository _intercepts;
    private readonly ThoughtRepository _thoughts;
    private readonly BrainLlmClient _llm;
    private readonly EngineEventBus _bus;
    private readonly ILogger _log;
    private readonly TradingOptions _options;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly DateTime _startedAtUtc = DateTime.UtcNow;
    private volatile BrainDirective? _current;
    private DateTime _lastReviewAtUtc = DateTime.MinValue;
    private DateTime _llmCooldownUntilUtc = DateTime.MinValue;
    private bool _warnedNoLlm;

    public BrainService(
        RuntimeState runtime,
        AccountService account,
        MarketContextStore market,
        OrderRepository orders,
        RiskInterceptRepository intercepts,
        ThoughtRepository thoughts,
        BrainLlmClient llm,
        EngineEventBus bus,
        ILogger log,
        TradingOptions options)
    {
        _runtime = runtime;
        _account = account;
        _market = market;
        _orders = orders;
        _intercepts = intercepts;
        _thoughts = thoughts;
        _llm = llm;
        _bus = bus;
        _log = log;
        _options = options;
    }

    /// <summary>大脑是否生效：配置启用，且实际决策提供方是 InternDecision。</summary>
    public bool Active =>
        _options.Brain.Enabled &&
        (_options.ForceInternDecision || _runtime.Snapshot().AiProvider == AiProvider.InternDecision);

    /// <summary>最近一份未过期的指引（无 / 过期 / 大脑未生效 → null）。</summary>
    public BrainDirective? Current
    {
        get
        {
            var d = _current;
            if (d is null || !Active)
            {
                return null;
            }

            var ttl = TimeSpan.FromMinutes(Math.Max(1, _options.Brain.DirectiveTtlMinutes));
            return DateTime.UtcNow - d.CreatedAtUtc > ttl ? null : d;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var minutes = Math.Max(1, _options.Brain.ReviewIntervalMinutes);
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(minutes));
        try
        {
            // 开局复盘：决策中心的 Startup 唤醒可能因行情未就绪被跳过，这里兜底，确保开局就有第一份指引
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            if (Active && !_runtime.IsHalted)
            {
                try
                {
                    await ReviewAsync("STARTUP", stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _log.LogError("大脑开局复盘异常（定期复盘继续）: " + ex.Message, ex);
                }
            }

            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                if (_runtime.IsHalted || !Active)
                {
                    continue;
                }

                try
                {
                    await ReviewAsync("PERIODIC", stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _log.LogError("大脑定期复盘异常（下个周期继续）: " + ex.Message, ex);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停机
        }
    }

    /// <summary>
    /// 关键阶段复盘入口（决策中心在 STARTUP / MACRO_HOURLY / LOSS_EVENT 前调用）：
    /// 受 Brain:ReviewOnKeyEvents 与最小间隔约束；任何失败都退回规则复盘，绝不抛给决策主循环。
    /// </summary>
    public async Task<BrainDirective?> ReviewOnKeyStageAsync(DecisionTrigger trigger, CancellationToken ct)
    {
        if (!Active || !_options.Brain.ReviewOnKeyEvents || trigger == DecisionTrigger.StrategyTick)
        {
            return Current;
        }

        var name = trigger switch
        {
            DecisionTrigger.Startup => "STARTUP",
            DecisionTrigger.MacroHourly => "MACRO_HOURLY",
            DecisionTrigger.LossEventWakeup => "LOSS_EVENT_WAKEUP",
            _ => "KEY_STAGE",
        };

        try
        {
            return await ReviewAsync(name, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogError($"大脑关键阶段复盘失败 [{name}]（沿用上一份指引）: {ex.Message}", ex);
            return Current;
        }
    }

    public async Task<BrainDirective?> ReviewAsync(string trigger, CancellationToken ct, bool force = false)
    {
        if (!Active || _runtime.IsHalted)
        {
            return Current;
        }

        await _gate.WaitAsync(ct);
        try
        {
            var minGap = TimeSpan.FromSeconds(Math.Max(5, _options.Brain.MinReviewIntervalSeconds));
            if (!force && DateTime.UtcNow - _lastReviewAtUtc < minGap && _current is not null)
            {
                return Current;
            }

            var cfg = _runtime.Snapshot();
            var state = await _account.RefreshAsync(ct, forceRefresh: true);
            var stats = await GatherStatsAsync(cfg, state, ct);
            var rule = RuleReview(stats, state, trigger);

            BrainDirective final;
            var sessionNote = "";
            if (_llm.IsConfigured && DateTime.UtcNow >= _llmCooldownUntilUtc)
            {
                try
                {
                    var userText = BuildUserContent(trigger, cfg, state, stats, rule);
                    var llm = await _llm.ReviewAsync(SystemPrompt.Replace("{SOUL}", ProfitSoul.Creed), userText, ct);
                    final = Merge(llm, rule, trigger);
                    sessionNote = userText;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _llmCooldownUntilUtc = DateTime.UtcNow.AddMinutes(2);
                    _log.LogWarn($"大脑 LLM 复盘失败，退回规则复盘并冷却 2 分钟: {ex.Message}");
                    final = rule;
                    sessionNote = $"LLM 调用失败（{ex.Message}），采用规则复盘";
                }
            }
            else
            {
                if (!_llm.IsConfigured && !_warnedNoLlm)
                {
                    _warnedNoLlm = true;
                    _log.LogWarn("大脑 LLM 未配置（Brain/DeepSeek 的 BaseUrl/ApiKey/Model 为空或占位），使用规则复盘；配置后自动升级为 LLM 复盘。");
                }

                final = rule;
                sessionNote = "LLM 未配置或冷却中，采用规则复盘";
            }

            _current = final;
            _lastReviewAtUtc = DateTime.UtcNow;
            await PublishAsync(final, state, sessionNote, ct);
            return final;
        }
        finally
        {
            _gate.Release();
        }
    }

    // ================= 统计 =================

    private sealed record ReviewStats(
        int Sells,
        int Wins,
        int Losses,
        decimal SumRealized,
        decimal AvgWin,
        decimal AvgLoss,
        int Buys,
        int Rejected,
        int Cancelled,
        int Intercepts,
        int ConsecutiveLosses,
        string RecentLines);

    private async Task<ReviewStats> GatherStatsAsync(TradingConfig cfg, AccountState state, CancellationToken ct)
    {
        _ = state;
        var recent = await _orders.ListRecentAsync(Math.Max(5, _options.Brain.LookbackOrders), ct);
        // Paper 的纸面账户每次启动都会重置，只统计本次运行产生的订单；Live 账户连续，统计全部回看窗口
        var since = _options.DataSourceMode == DataSourceMode.Paper ? _startedAtUtc : DateTime.MinValue;
        var scoped = recent.Where(o => o.CreatedAtUtc >= since).ToList();

        var sellFills = scoped.Where(o => o.Side == OrderSide.Sell && o.FilledBaseAmount > 0m).ToList();
        var wins = sellFills.Where(o => o.RealizedPnl > 0m).ToList();
        var losses = sellFills.Where(o => o.RealizedPnl < 0m).ToList();

        var consecutive = 0;
        foreach (var o in sellFills) // ListRecentAsync 为新→旧
        {
            if (o.RealizedPnl < 0m)
            {
                consecutive++;
            }
            else
            {
                break;
            }
        }

        var interceptSince = DateTime.UtcNow.AddHours(-1);
        var intercepts = (await _intercepts.ListRecentAsync(50, ct)).Count(r => r.CreatedAtUtc >= interceptSince && r.CreatedAtUtc >= since);

        var lines = new StringBuilder();
        foreach (var o in scoped.Take(10))
        {
            lines.Append(CultureInfo.InvariantCulture,
                $"- {o.CreatedAtUtc:HH:mm:ss}Z {o.Side} {o.Symbol} 价{o.Price:0.######} 额${o.AmountUsd:0.00} {o.Status}");
            if (o.Side == OrderSide.Sell && o.FilledBaseAmount > 0m)
            {
                lines.Append(CultureInfo.InvariantCulture, $" 盈亏${o.RealizedPnl:0.0000}");
            }

            lines.AppendLine();
        }

        return new ReviewStats(
            Sells: sellFills.Count,
            Wins: wins.Count,
            Losses: losses.Count,
            SumRealized: sellFills.Sum(o => o.RealizedPnl),
            AvgWin: wins.Count > 0 ? wins.Average(o => o.RealizedPnl) : 0m,
            AvgLoss: losses.Count > 0 ? losses.Average(o => o.RealizedPnl) : 0m,
            Buys: scoped.Count(o => o.Side == OrderSide.Buy && o.FilledBaseAmount > 0m),
            Rejected: scoped.Count(o => o.Status == OrderStatus.Rejected),
            Cancelled: scoped.Count(o => o.Status == OrderStatus.Cancelled && o.FilledBaseAmount <= 0m),
            Intercepts: intercepts,
            ConsecutiveLosses: consecutive,
            RecentLines: lines.Length == 0 ? "（暂无订单）" : lines.ToString().TrimEnd());
    }

    // ================= 规则复盘（LLM 的底线与降级） =================

    private BrainDirective RuleReview(ReviewStats st, AccountState state, string trigger)
    {
        var initial = Math.Max(0.01m, state.InitialCapital);
        var drawdown = (initial - state.EquityUsd) / initial; // >0 表示亏损
        var profit = -drawdown;
        var decided = st.Wins + st.Losses;
        var winRate = decided > 0 ? (decimal)st.Wins / decided : 0.5m;
        var baseNet = Math.Max(0m, _options.Profit.MinNetProfitBps);

        var d = new BrainDirective
        {
            Source = "RULE",
            Trigger = trigger,
            CreatedAtUtc = DateTime.UtcNow,
        };

        if (drawdown >= 0.15m || st.ConsecutiveLosses >= 4)
        {
            d.Posture = "CAPITAL_PRESERVATION";
            d.MaxRiskRatio = 0.2m;
            d.AllowNewBuys = drawdown < 0.20m;
            d.MinNetProfitBps = baseNet + 15m;
            d.StopLossPercent = _options.Profit.StopLossPercent;
            d.Guidance = "保本第一：只在盘口买一支撑明显、价差足够覆盖手续费时极小仓试单；已有持仓优先在保本价之上落袋，不再加仓。";
            d.Reasoning = $"权益回撤 {drawdown:P1}，连续亏损 {st.ConsecutiveLosses} 笔，进入资本保全。";
        }
        else if (drawdown >= 0.05m || st.ConsecutiveLosses >= 2 || (decided >= 4 && winRate < 0.4m))
        {
            d.Posture = "DEFENSIVE";
            d.MaxRiskRatio = 0.4m;
            d.AllowNewBuys = true;
            d.MinNetProfitBps = baseNet + 10m;
            d.Guidance = "防守：缩小单笔额度，只做价差明显大于双边手续费的微套；无把握就观望，卖出必须在保本价之上。";
            d.Reasoning = $"回撤 {drawdown:P1}，近期胜率 {winRate:P0}，连亏 {st.ConsecutiveLosses}，降低风险档位。";
        }
        else if (profit >= 0.03m && decided >= 3 && winRate >= 0.6m)
        {
            d.Posture = "AGGRESSIVE";
            d.MaxRiskRatio = 1m;
            d.AllowNewBuys = true;
            d.Guidance = "状态良好：在活跃震荡市保持节奏，利润达到保本价之上就分批落袋，把盈利锁进权益。";
            d.Reasoning = $"累计盈利 {profit:P1}，胜率 {winRate:P0}，可维持正常风险档位。";
        }
        else
        {
            d.Posture = "BALANCED";
            d.MaxRiskRatio = 0.8m;
            d.AllowNewBuys = true;
            d.Guidance = "均衡：只在净利润空间明确时出手，买入挂在买一/买二支撑，卖出必须覆盖成本与手续费。";
            d.Reasoning = $"权益变动 {profit:P1}，已平仓 {decided} 笔胜率 {winRate:P0}，维持均衡。";
        }

        d.HardStop = !d.AllowNewBuys;
        return d;
    }

    /// <summary>LLM 指引与规则底线合并：LLM 只能比规则更谨慎，且所有数值钳制到安全范围。</summary>
    private BrainDirective Merge(BrainDirective llm, BrainDirective rule, string trigger)
    {
        var posture = (llm.Posture ?? "").Trim().ToUpperInvariant();
        if (posture is not ("AGGRESSIVE" or "BALANCED" or "DEFENSIVE" or "CAPITAL_PRESERVATION"))
        {
            posture = rule.Posture;
        }

        // 风险档位：LLM 只能比规则更保守，但不得低于下限（规则自身已判定防守/保本的除外）——
        // 否则一句"风险 0.2"就会把下单额压到几乎为 0，永远不交易 = 永远零收益
        var riskFloor = Math.Min(rule.MaxRiskRatio, Math.Clamp(_options.Brain.MinRiskRatio, 0.05m, 1m));
        var risk = Math.Clamp(Math.Max(Math.Min(llm.MaxRiskRatio, rule.MaxRiskRatio), riskFloor), 0.05m, 1m);

        // 保本利润：LLM 只能在规则之上加码，且最多加 MaxExtraProfitBps——要求过高会让卖单永远成交不了
        var baseNet = Math.Max(0m, _options.Profit.MinNetProfitBps);
        decimal? minNet = llm.MinNetProfitBps is { } a && rule.MinNetProfitBps is { } b ? Math.Max(a, b) : llm.MinNetProfitBps ?? rule.MinNetProfitBps;
        if (minNet is { } mn)
        {
            var cap = Math.Max(rule.MinNetProfitBps ?? baseNet, baseNet + Math.Max(0m, _options.Brain.MaxExtraProfitBps));
            minNet = Math.Min(mn, cap);
        }

        var merged = new BrainDirective
        {
            Posture = posture,
            MaxRiskRatio = risk,
            AllowNewBuys = llm.AllowNewBuys && rule.AllowNewBuys,
            HardStop = !rule.AllowNewBuys, // 只有规则判定的回撤硬止血才会冻结空仓；LLM 的"禁止新买"只在持仓时（逼出卖单）生效
            MinNetProfitBps = minNet,
            StopLossPercent = llm.StopLossPercent is { } sa && rule.StopLossPercent is { } sb ? Math.Min(sa, sb) : llm.StopLossPercent ?? rule.StopLossPercent,
            Guidance = Trunc(llm.Guidance, 400),
            Reasoning = Trunc(llm.Reasoning, 400),
            Source = "LLM",
            Trigger = trigger,
            CreatedAtUtc = DateTime.UtcNow,
        };

        if (string.IsNullOrWhiteSpace(merged.Guidance))
        {
            merged.Guidance = rule.Guidance;
        }

        return merged;
    }

    // ================= LLM 输入 =================

    private string BuildUserContent(string trigger, TradingConfig cfg, AccountState state, ReviewStats st, BrainDirective rule)
    {
        var ctx = _market.Build(cfg.ActiveSymbol);
        var held = state.Holdings.Count == 0
            ? "空仓"
            : string.Join("，", state.Holdings.Select(kv =>
                $"{kv.Key} 数量{kv.Value:0.########} 均价{(state.AvgCosts.TryGetValue(kv.Key, out var c) ? c : 0m):0.######}"));
        var prev = _current is { } p
            ? $"上一份指引（{p.Source}/{p.Trigger}）：姿态 {p.Posture}，风险上限 {p.MaxRiskRatio:0.##}，允许新买 {p.AllowNewBuys}；指引：{p.Guidance}"
            : "（尚无上一份指引）";

        return string.Create(CultureInfo.InvariantCulture, $"""
            复盘触发阶段：{trigger}；系统时间 {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z；数据源 {_options.DataSourceMode}。

            【账户（只算本金预算内的钱）】
            本金预算 ${state.InitialCapital:0.00}，当前总权益 ${state.EquityUsd:0.00}，累计盈亏 ${state.AccumulatedProfit:0.00}（{(state.InitialCapital > 0 ? state.AccumulatedProfit / state.InitialCapital : 0m):P2}），
            已实现盈亏 ${state.RealizedPnl:0.00}，浮动盈亏 ${state.UnrealizedPnl:0.00}，可用 USD ${state.AvailableUsd:0.00}，单笔买单上限 ${state.MaxOrderSize:0.00}。
            持仓：{held}。
            仓位：已占用 ${state.CommittedUsd:0.00}（总仓位上限 权益×{Math.Max(_options.MaxExposureRatio, _options.MaxOrderSizeRatio):P0}），当前还能新买 ${RiskEngine.BuyRoom(state.EquityUsd, state.AvailableUsd, _options, state.CommittedUsd):0.00}。系统有持仓管家：持仓一到保本价之上就自动止盈落袋，浮亏触及止损线自动止损——你的指引要关注"现金能否回笼、循环能否转起来"。
            硬熔断线：权益 < ${state.InitialCapital * _options.CircuitBreakerRatio:0.00}（本金 × {_options.CircuitBreakerRatio:P0}），当前安全垫 ${state.EquityUsd - state.InitialCapital * _options.CircuitBreakerRatio:0.00}。
            手续费：挂单 {_options.MakerFeeBps:0.##} / 吃单 {_options.TakerFeeBps:0.##} bps（单边）；当前保本利润要求 {_options.Profit.MinNetProfitBps:0.##} bps；止损线 {_options.Profit.StopLossPercent:0.##}%。

            【最近战绩（回看 {_options.Brain.LookbackOrders} 单）】
            已平仓卖出 {st.Sells} 笔：盈利 {st.Wins} / 亏损 {st.Losses}，合计已实现 ${st.SumRealized:0.0000}，平均盈利 ${st.AvgWin:0.0000}，平均亏损 ${st.AvgLoss:0.0000}，当前连续亏损 {st.ConsecutiveLosses} 笔。
            成交买单 {st.Buys} 笔，被拒 {st.Rejected} 笔，超时/主动撤单 {st.Cancelled} 笔，近 1 小时风控拦截 {st.Intercepts} 次。
            最近订单：
            {st.RecentLines}

            【聚焦标的盘面 {cfg.ActiveSymbol}】
            最新价 {ctx.LastPrice:0.######}，价差 {ctx.SpreadBps:0.0} bps，1H 波幅 {ctx.Volatility1HPercent:0.00}%，15M 趋势 {ctx.TrendSignal15M}（{ctx.Change15MPercent:0.00}%），盘口不平衡 {ctx.Imbalance:0.00}，5M 成交额 ${ctx.Turnover5M:0}。
            交易模式 {cfg.TradingMode}，范围 {cfg.AssetScope}。

            【规则底线（你只能收紧，不能放宽）】
            姿态 {rule.Posture}，Max_Risk_Ratio ≤ {rule.MaxRiskRatio:0.##}，Allow_New_Buys = {rule.AllowNewBuys}，Min_Net_Profit_Bps ≥ {rule.MinNetProfitBps?.ToString("0.##") ?? "配置值"}，Stop_Loss_Percent ≤ {rule.StopLossPercent?.ToString("0.##") ?? "配置值"}。规则判断：{rule.Reasoning}

            {prev}

            请复盘并只输出 JSON 指引。
            """);
    }

    // ================= 发布 =================

    private async Task PublishAsync(BrainDirective d, AccountState state, string requestNote, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture,
            $"🧠 大脑复盘[{d.Trigger}/{d.Source}] 姿态={d.Posture} 风险上限={d.MaxRiskRatio:0.##} 允许新买={(d.AllowNewBuys ? "是" : "否")}");
        if (d.MinNetProfitBps is { } mp)
        {
            sb.Append(CultureInfo.InvariantCulture, $" 保本利润≥{mp:0.##}bps");
        }

        if (d.StopLossPercent is { } sl)
        {
            sb.Append(CultureInfo.InvariantCulture, $" 止损≤{sl:0.##}%");
        }

        sb.Append($"\n归因：{d.Reasoning}\n指引：{d.Guidance}");
        var summary = sb.ToString();

        _log.LogInfo(summary.Replace('\n', ' '));
        var sessionId = Guid.NewGuid().ToString("N");

        _bus.Publish(EngineEventKind.ThoughtChunk,
            JsonSerializer.Serialize(new UiThoughtChunk
            {
                SessionId = sessionId,
                Trigger = "BRAIN_REVIEW",
                Text = summary,
            }, DvcJsonContext.Default.UiThoughtChunk));
        _bus.Publish(EngineEventKind.FlowLog,
            JsonSerializer.Serialize(new UiFlowLog { Message = summary.Split('\n')[0] }, DvcJsonContext.Default.UiFlowLog));

        try
        {
            await _thoughts.InsertAsync(new ThoughtRecord
            {
                SessionId = sessionId,
                TriggerReason = "BRAIN_" + d.Trigger,
                RequestJson = requestNote,
                ThinkingText = summary,
                DecisionJson = JsonSerializer.Serialize(d, DvcJsonContext.Default.BrainDirective),
                Diagnosis = d.Reasoning,
                ActionType = "BRAIN_" + d.Posture,
                EquityAfter = state.EquityUsd,
            }, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarn("大脑复盘日志落库失败（不影响运行）: " + ex.Message);
        }
    }

    private static string Trunc(string? s, int max) =>
        string.IsNullOrWhiteSpace(s) ? "" : (s.Length > max ? s[..max] : s.Trim());
}
