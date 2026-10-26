using System.Text.Json;
using Dvc.Core;
using Dvc.Core.Events;
using Dvc.Core.Exchange;
using Dvc.Core.Trading;
using Dvc.Persistence;

namespace Dvc.Engine;

/// <summary>账户状态快照（风控/决策/看板共用）。</summary>
public sealed class AccountState
{
    public decimal InitialCapital { get; init; }

    /// <summary>单笔上限比例（跟随 Trading:MaxOrderSizeRatio 配置，默认 20%）。</summary>
    public decimal MaxOrderSizeRatio { get; init; } = 0.20m;

    /// <summary>单笔绝对上限（Trading:MaxOrderUsd，0 = 不启用）。</summary>
    public decimal MaxOrderUsd { get; init; }

    public decimal AvailableUsd { get; init; }

    public decimal EquityUsd { get; init; }

    public decimal RealizedPnl { get; init; }

    public IReadOnlyDictionary<string, decimal> Holdings { get; init; } = new Dictionary<string, decimal>();

    public IReadOnlyDictionary<string, decimal> LastPrices { get; init; } = new Dictionary<string, decimal>();

    /// <summary>各基础币持仓均价（含买入手续费）。</summary>
    public IReadOnlyDictionary<string, decimal> AvgCosts { get; init; } = new Dictionary<string, decimal>();

    /// <summary>各基础币已持有多少分钟（距最近一次买入成交；未知则缺省）——止盈目标随持仓变久而下调。</summary>
    public IReadOnlyDictionary<string, decimal> PositionAgeMinutes { get; init; } = new Dictionary<string, decimal>();

    /// <summary>累计盈亏 = 当前总权益 − 本金预算（已实现 + 浮动，均已净手续费）。</summary>
    public decimal AccumulatedProfit => EquityUsd - InitialCapital;

    /// <summary>持仓浮动盈亏（按最新价 − 含费均价）。</summary>
    public decimal UnrealizedPnl
    {
        get
        {
            var total = 0m;
            foreach (var (sym, px) in LastPrices)
            {
                var b = RiskEngine.BaseOf(sym);
                if (Holdings.TryGetValue(b, out var qty) && AvgCosts.TryGetValue(b, out var avg))
                {
                    total += (px - avg) * qty;
                }
            }

            return total;
        }
    }

    /// <summary>
    /// Live 预算账本给出的"真实已占用"（持仓市值 + 挂买单冻结）；null = 用 权益 − 可用现金 推算（Paper 下二者等价）。
    /// 不能在 Live 一律用"权益 − 可用"：交易所真实 USDT 少于预算时，那段差额不是持仓，误当成已占用会让仓位永远"已满"、一笔都不买。
    /// </summary>
    public decimal? CommittedOverride { get; init; }

    /// <summary>已占用资金（持仓市值 + 挂买单冻结，USD）。</summary>
    public decimal CommittedUsd => CommittedOverride ?? Math.Max(0m, EquityUsd - AvailableUsd);

    public decimal MaxOrderSize => RiskEngine.MaxOrderSize(EquityUsd, MaxOrderSizeRatio, MaxOrderUsd);

    public decimal LastSettlementEquity { get; init; }
}

/// <summary>
/// 账户服务：负责从网关拉取余额/持仓、折算权益、结算快照落库（Asset_Snapshot）、
/// 维护 LastSettlementEquity（亏损唤醒判定基准）、推送 UiAccountTick。
/// </summary>
public sealed class AccountService
{
    private readonly IOrderGateway _gateway;
    private readonly OrderRepository _orders;
    private readonly SettlementRepository _settlements;
    private readonly TradingConfigStore _configStore;
    private readonly RuntimeState _runtime;
    private readonly MarketContextStore _market;
    private readonly EngineEventBus _bus;
    private readonly ILogger _log;
    private readonly Dvc.Core.Configuration.TradingOptions _options;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private AccountState _current;
    private DateTime _lastRefresh = DateTime.MinValue;
    private volatile bool _warnedZeroLiveEquity;
    private DateTime _lastBalanceFailLog = DateTime.MinValue;

    public AccountService(
        IOrderGateway gateway,
        OrderRepository orders,
        SettlementRepository settlements,
        TradingConfigStore configStore,
        RuntimeState runtime,
        MarketContextStore market,
        EngineEventBus bus,
        ILogger log,
        Dvc.Core.Configuration.TradingOptions options)
    {
        _gateway = gateway;
        _orders = orders;
        _settlements = settlements;
        _configStore = configStore;
        _runtime = runtime;
        _market = market;
        _bus = bus;
        _log = log;
        _options = options;
        var cfg = runtime.Snapshot();
        _current = new AccountState
        {
            InitialCapital = cfg.InitialCapital,
            AvailableUsd = cfg.InitialCapital,
            EquityUsd = cfg.InitialCapital,
            LastSettlementEquity = cfg.LastSettlementEquity,
            MaxOrderSizeRatio = _options.MaxOrderSizeRatio,
            MaxOrderUsd = _options.MaxOrderUsd,
        };
    }

    public AccountState Current
    {
        get
        {
            lock (this)
            {
                return _current;
            }
        }
    }

    /// <summary>刷新余额（节流：距上次 &lt;1.5s 直接返回缓存，Live 模式遵守 Rate Limit）。</summary>
    public Task<AccountState> RefreshAsync(CancellationToken ct = default) => RefreshAsync(ct, forceRefresh: false);

    /// <summary>刷新余额；forceRefresh=true 时绕过节流（熔断清仓后取真实快照用）。</summary>
    public async Task<AccountState> RefreshAsync(CancellationToken ct, bool forceRefresh)
    {
        await _refreshLock.WaitAsync(ct);
        try
        {
            var now = DateTime.UtcNow;
            if (!forceRefresh && now - _lastRefresh < TimeSpan.FromMilliseconds(1500))
            {
                return _current;
            }

            var cfg = _runtime.Snapshot();

            // 余额拉取失败：沿用上次快照并节流重试，绝不抛出导致风控巡检中断
            AccountBalances bal;
            try
            {
                bal = await _gateway.GetBalancesAsync(ct);
            }
            catch (Exception ex)
            {
                // 失败日志 30s 节流，避免 1.5s 轮询刷屏；根因不变时只提示"仍失败"
                if (DateTime.UtcNow - _lastBalanceFailLog > TimeSpan.FromSeconds(30))
                {
                    _lastBalanceFailLog = DateTime.UtcNow;
                    _log.LogError($"获取余额失败（沿用上次快照，继续重试）: {ex.Message}");
                }

                _lastRefresh = now;
                return _current;
            }

            // Live：交易所账户可能远大于本金预算 → 用预算账本折算"这笔钱"的现金/持仓/盈亏；
            // Paper：纸面账户本身就只有本金预算，直接采用。
            decimal? committed = null;
            if (_options.DataSourceMode == Dvc.Core.DataSourceMode.Live && bal.EquityUsd > 0m)
            {
                (bal, committed) = await ApplyBudgetLedgerAsync(bal, cfg, ct);
            }

            var lastPrices = new Dictionary<string, decimal>();
            foreach (var sym in bal.Holdings.Keys)
            {
                var instId = FindInstId(cfg, sym);
                if (instId is not null)
                {
                    var px = _market.Build(instId).LastPrice;
                    if (px > 0)
                    {
                        lastPrices[instId] = px;
                    }
                }
            }

            var ages = new Dictionary<string, decimal>();
            if (bal.Holdings.Count > 0)
            {
                var buyTimes = await _orders.LastBuyFillTimesAsync(ct);
                foreach (var baseCcy in bal.Holdings.Keys)
                {
                    var instId = FindInstId(cfg, baseCcy);
                    if (instId is not null && buyTimes.TryGetValue(instId, out var at))
                    {
                        ages[baseCcy] = Math.Max(0m, (decimal)(now - at).TotalMinutes);
                    }
                }
            }

            var state = new AccountState
            {
                PositionAgeMinutes = ages,
                InitialCapital = cfg.InitialCapital,
                AvailableUsd = Math.Round(bal.AvailableUsd, 2),
                EquityUsd = bal.EquityUsd > 0 ? Math.Round(bal.EquityUsd, 2) : Math.Round(bal.AvailableUsd, 2),
                RealizedPnl = bal.RealizedPnlUsd,
                Holdings = bal.Holdings,
                AvgCosts = bal.AvgCosts ?? new Dictionary<string, decimal>(),
                LastPrices = lastPrices,
                LastSettlementEquity = cfg.LastSettlementEquity,
                MaxOrderSizeRatio = _options.MaxOrderSizeRatio,
                MaxOrderUsd = _options.MaxOrderUsd,
                CommittedOverride = committed,
            };

            lock (this)
            {
                _current = state;
            }

            // Live 模式下余额持续读为 0：一次性告警，避免"权益=0/盈亏=-初始本金"的迷惑性看板
            if (_options.DataSourceMode == Dvc.Core.DataSourceMode.Live &&
                !_warnedZeroLiveEquity && state.EquityUsd == 0m)
            {
                _warnedZeroLiveEquity = true;
                _log.LogError(
                    "⚠ Live 余额读取为 $0（可用 $0、持仓 0）：签名 REST 被网络拒绝（常见 code=50112 Invalid OK-ACCESS-TIMESTAMP，" +
                    "对错密钥同码 = 非标准 OKX 网关/代理拦截），或 Key 无权限/未入金/IP 白名单未放行。" +
                    "此状态安全：AI 订单全被风控拒绝、不会真实下单；但权益熔断也暂不触发（equity=0 属保守挂起）。");
            }

            _lastRefresh = now;
            PublishAccountTick(state, cfg);
            return state;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    /// <summary>
    /// Live 模式：交易所不回传逐笔已实现盈亏，由预算账本重放得出某笔卖单的净盈亏（含手续费估算）。
    /// 找不到（非本账本内的卖单）返回 null。
    /// </summary>
    public async Task<decimal?> ComputeSellRealizedAsync(long orderId, CancellationToken ct = default)
    {
        var cfg = _runtime.Snapshot();
        var view = BudgetLedger.Replay(await _orders.ListFilledAsync(ct, _options.LedgerStartUtc), cfg.InitialCapital, _options.FeeBps / 10000m);
        return view.SellRealizedByOrderId.TryGetValue(orderId, out var pnl) ? pnl : null;
    }

    /// <summary>立即向看板推送一次账户快照（盾牌拦截计数变更后调用）。</summary>
    public void PublishAccountTickNow()
    {
        var cfg = _runtime.Snapshot();
        var state = Current;
        PublishAccountTick(state, cfg);
    }

    /// <summary>结算：记录 Asset_Snapshot（每日/每小时/亏损唤醒/熔断），并更新 LastSettlementEquity。</summary>
    public async Task SettleAsync(string kind, CancellationToken ct = default)
    {
        var state = await RefreshAsync(ct);
        var cfg = _runtime.Snapshot();
        var holdingsJson = JsonSerializer.Serialize(state.Holdings, DvcJsonContext.Default.DictionaryStringDecimal);
        var lastPricesJson = JsonSerializer.Serialize(state.LastPrices, DvcJsonContext.Default.DictionaryStringDecimal);

        var settlement = new AccountSettlement
        {
            Kind = kind,
            InitialCapital = state.InitialCapital,
            AvailableUsd = state.AvailableUsd,
            EquityUsd = state.EquityUsd,
            RealizedPnl = state.RealizedPnl,
            MaxOrderSize = state.MaxOrderSize,
            HoldingsJson = holdingsJson,
            LastPricesJson = lastPricesJson,
        };
        await _settlements.InsertAsync(settlement, ct);

        _runtime.Update(c =>
        {
            c.LastSettlementEquity = state.EquityUsd;
        });
        await _configStore.SaveAsync(_runtime.Snapshot(), ct);

        _log.LogInfo($"账户结算[{kind}] 权益=${state.EquityUsd:0.00} 可用=${state.AvailableUsd:0.00} 已实现盈亏=${state.RealizedPnl:0.00}");
    }

    private void PublishAccountTick(AccountState state, TradingConfig cfg)
    {
        var tick = new UiAccountTick
        {
            InitialCapital = state.InitialCapital,
            AvailableUsd = state.AvailableUsd,
            EquityUsd = state.EquityUsd,
            AccumulatedProfit = Math.Round(state.AccumulatedProfit, 2),
            RealizedPnl = Math.Round(state.RealizedPnl, 2),
            UnrealizedPnl = Math.Round(state.UnrealizedPnl, 2),
            MaxOrderSize = state.MaxOrderSize,
            MaxOrderSizeRatio = state.MaxOrderSizeRatio,
            ShieldCount = cfg.ShieldCount,
            Holdings = state.Holdings.ToDictionary(kv => kv.Key, kv => Math.Round(kv.Value, 8)),
            RunState = cfg.RunState.ToString(),
        };
        _bus.Publish(EngineEventKind.AccountSnapshot,
            JsonSerializer.Serialize(tick, DvcJsonContext.Default.UiAccountTick));
    }

    /// <summary>
    /// 把交易所原始余额收敛为"本金预算账户"：
    /// 现金 = 账本现金 − 挂买单冻结（且不超过交易所真实可用）；持仓 = min(账本持仓, 交易所真实持仓)；
    /// 账本里有、交易所已没有的持仓（紧急市价清仓/人工卖出）按最新价记为卖出，保持盈亏自洽。
    /// </summary>
    private async Task<(AccountBalances Balances, decimal Committed)> ApplyBudgetLedgerAsync(AccountBalances raw, TradingConfig cfg, CancellationToken ct)
    {
        var feeRate = _options.FeeBps / 10000m;
        var view = BudgetLedger.Replay(await _orders.ListFilledAsync(ct, _options.LedgerStartUtc), cfg.InitialCapital, feeRate);

        var cash = view.Cash;
        var realized = view.RealizedPnl;
        var holdings = new Dictionary<string, decimal>();
        var avgCosts = new Dictionary<string, decimal>();
        var positionValue = 0m;

        foreach (var (symbol, (qty, avg)) in view.Positions)
        {
            var baseCcy = RiskEngine.BaseOf(symbol);
            var actual = raw.Holdings.TryGetValue(baseCcy, out var a) ? a : 0m;
            var managed = Math.Min(qty, actual);
            var px = _market.Build(symbol).LastPrice;
            if (px <= 0m)
            {
                px = avg;
            }

            var missing = qty - managed;
            if (missing > 0.00000001m)
            {
                var proceeds = missing * px * (1m - feeRate);
                cash += proceeds;
                realized += proceeds - missing * avg;
            }

            if (managed > 0m)
            {
                holdings[baseCcy] = managed;
                avgCosts[baseCcy] = avg;
                positionValue += managed * px;
            }
        }

        var reserved = 0m;
        foreach (var o in await _orders.GetOpenAsync(null, ct))
        {
            if (o.IsBuy)
            {
                reserved += o.BaseAmount * o.Price * (1m + feeRate);
            }
        }

        var free = Math.Max(0m, Math.Min(cash - reserved, raw.AvailableUsd));
        return (new AccountBalances(free, holdings, cash + positionValue, realized, avgCosts), positionValue + reserved);
    }

    private static string? FindInstId(TradingConfig cfg, string baseCcy)
    {
        foreach (var w in cfg.WatchSymbols)
        {
            if (RiskEngine.BaseOf(w).Equals(baseCcy, StringComparison.OrdinalIgnoreCase))
            {
                return w;
            }
        }

        return null;
    }
}
