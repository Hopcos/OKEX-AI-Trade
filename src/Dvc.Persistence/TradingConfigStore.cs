using System.Globalization;
using Dvc.Core;
using Dvc.Core.Trading;

namespace Dvc.Persistence;

/// <summary>
/// 配置唯一来源 = appsettings（TradingOptions）。System_Config **不再存放任何配置项**，
/// 仅持久化运行状态/历史基准（run_state 停机保持、last_settlement_equity 亏损唤醒基线、
/// shield_count 护盾计数、theme 主题记忆）供重启恢复；交易模式/标的/本金等配置一律以
/// appsettings|env 为准，热切换只改运行时内存，重启即回到配置文件。
/// </summary>
public sealed class TradingConfigStore(SystemConfigRepository repo)
{
    // ===== 允许持久化的"状态/历史"键 =====
    private const string KeyRunState = "run_state";
    private const string KeyLastSettlementEquity = "last_settlement_equity";
    private const string KeyShieldCount = "shield_count";
    private const string KeyTheme = "theme";

    /// <summary>历史遗留的纯配置键：启动时从库中清除，确保 DB 不再是配置来源。</summary>
    private static readonly string[] ConfigOnlyKeys =
    [
        "trading_mode", "asset_scope", "active_symbol", "data_source_mode",
        "ai_provider", "initial_capital", "blacklist", "watch_symbols",
    ];

    /// <summary>加载配置：一律取 appsettings；System_Config 仅用于恢复运行状态/历史基准。</summary>
    public async Task<TradingConfig> LoadOrSeedAsync(Dvc.Core.Configuration.TradingOptions options, CancellationToken ct = default)
    {
        var all = await repo.GetAllAsync(ct);
        var cfg = new TradingConfig
        {
            // 配置字段：全部来自 appsettings|env
            RunState = ParseEnum(all, KeyRunState, SystemRunState.Running),
            TradingMode = options.DefaultTradingMode,
            AssetScope = options.DefaultAssetScope,
            ActiveSymbol = options.ActiveSymbol,
            DataSourceMode = options.DataSourceMode,
            AiProvider = options.AiProvider,
            InitialCapital = options.InitialCapital,
            Blacklist = [.. options.Blacklist],
            WatchSymbols = [.. options.WatchSymbols],
            // 状态/历史基准：从库恢复
            LastSettlementEquity = ParseDecimal(all, KeyLastSettlementEquity, options.InitialCapital),
            ShieldCount = ParseLong(all, KeyShieldCount, 0),
            Theme = Get(all, KeyTheme, "cyberpunk"),
        };

        // 幂等清理历史遗留配置键（DB 只管历史，不存配置）
        var stale = ConfigOnlyKeys.Where(all.ContainsKey).ToList();
        if (stale.Count > 0)
        {
            await repo.DeleteManyAsync(stale, ct);
        }

        return cfg;
    }

    public async Task SaveAsync(TradingConfig c, CancellationToken ct = default)
    {
        // 只落状态/历史键；配置项一律不落库
        await repo.SetManyAsync(
            [
                new(KeyRunState, c.RunState.ToString()),
                new(KeyLastSettlementEquity, c.LastSettlementEquity.ToString(CultureInfo.InvariantCulture)),
                new(KeyShieldCount, c.ShieldCount.ToString(CultureInfo.InvariantCulture)),
                new(KeyTheme, c.Theme),
            ], ct);
    }

    private static string Get(IReadOnlyDictionary<string, string> all, string key, string fallback) =>
        all.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : fallback;

    private static TEnum ParseEnum<TEnum>(IReadOnlyDictionary<string, string> all, string key, TEnum fallback) where TEnum : struct =>
        all.TryGetValue(key, out var v) && Enum.TryParse<TEnum>(v, out var parsed) ? parsed : fallback;

    private static decimal ParseDecimal(IReadOnlyDictionary<string, string> all, string key, decimal fallback) =>
        all.TryGetValue(key, out var v) && decimal.TryParse(v, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : fallback;

    private static long ParseLong(IReadOnlyDictionary<string, string> all, string key, long fallback) =>
        all.TryGetValue(key, out var v) && long.TryParse(v, NumberStyles.Number, CultureInfo.InvariantCulture, out var l) ? l : fallback;
}
