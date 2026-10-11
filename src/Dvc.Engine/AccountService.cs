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

    public decimal AvailableUsd { get; init; }

    public decimal EquityUsd { get; init; }

    public decimal RealizedPnl { get; init; }

    public IReadOnlyDictionary<string, decimal> Holdings { get; init; } = new Dictionary<string, decimal>();

    public IReadOnlyDictionary<string, decimal> LastPrices { get; init; } = new Dictionary<string, decimal>();

    public decimal AccumulatedProfit => EquityUsd - InitialCapital;

    public decimal MaxOrderSize => RiskEngine.MaxOrderSize(InitialCapital, EquityUsd, MaxOrderSizeRatio);

    public decimal LastSettlementEquity { get; init; }
}

/// <summary>
/// 账户服务：负责从网关拉取余额/持仓、折算权益、结算快照落库（Asset_Snapshot）、
/// 维护 LastSettlementEquity（亏损唤醒判定基准）、推送 UiAccountTick。
/// </summary>
public sealed class AccountService
{
    private readonly IOrderGateway _gateway;
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
        SettlementRepository settlements,
        TradingConfigStore configStore,
        RuntimeState runtime,
        MarketContextStore market,
        EngineEventBus bus,
        ILogger log,
        Dvc.Core.Configuration.TradingOptions options)
    {
        _gateway = gateway;
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

            var state = new AccountState
            {
                InitialCapital = cfg.InitialCapital,
                AvailableUsd = Math.Round(bal.AvailableUsd, 2),
                EquityUsd = bal.EquityUsd > 0 ? Math.Round(bal.EquityUsd, 2) : Math.Round(bal.AvailableUsd, 2),
                RealizedPnl = bal.RealizedPnlUsd,
                Holdings = bal.Holdings,
                LastPrices = lastPrices,
                LastSettlementEquity = cfg.LastSettlementEquity,
                MaxOrderSizeRatio = _options.MaxOrderSizeRatio,
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
            MaxOrderSize = state.MaxOrderSize,
            MaxOrderSizeRatio = state.MaxOrderSizeRatio,
            ShieldCount = cfg.ShieldCount,
            Holdings = state.Holdings.ToDictionary(kv => kv.Key, kv => Math.Round(kv.Value, 8)),
            RunState = cfg.RunState.ToString(),
        };
        _bus.Publish(EngineEventKind.AccountSnapshot,
            JsonSerializer.Serialize(tick, DvcJsonContext.Default.UiAccountTick));
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
