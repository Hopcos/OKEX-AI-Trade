using Dvc.Ai;
using Dvc.Ai.DeepSeek;
using Dvc.Ai.Mock;
using Dvc.Core;
using Dvc.Core.Configuration;
using Dvc.Core.Exchange;
using Dvc.Engine;
using Dvc.Exchange.Okx;
using Dvc.Exchange.Simulation;
using Dvc.Persistence;
using Dvc.Web.Hubs;
using Dvc.Web.Services;

var builder = WebApplication.CreateBuilder(args);

// 控制台 UTF-8，避免中文日志在默认代码页下显示为 ???
try
{
    Console.OutputEncoding = System.Text.Encoding.UTF8;
}
catch
{
    // 某些宿主不允许设置编码，忽略
}

// ============ 宿主：局域网安全绑定（硬编码 localhost:5000） ============
builder.WebHost.UseUrls("http://localhost:5000");

var options = builder.Configuration.GetSection(TradingOptions.SectionName).Get<TradingOptions>() ?? new TradingOptions();

// .NET 10 ConfigurationBinder 对"默认值非空的集合属性"是追加而非替换
// （RiskAppetiteRatios 默认 [0.25,0.5,1.0] + 配置 [0.1..1.0] → 绑出 9 个！）
// 因此集合字段一律做覆盖式重绑：只取配置文件里的项，缺省时才回退内置默认。
static string[] BindStrArray(IConfiguration cfg, string path, string[] fallback) =>
    cfg.GetSection(path).Get<string[]>() is { Length: > 0 } a ? a : fallback;
static double[] BindDblArray(IConfiguration cfg, string path, double[] fallback) =>
    cfg.GetSection(path).Get<double[]>() is { Length: > 0 } a ? a : fallback;

options.WatchSymbols = [.. BindStrArray(builder.Configuration, "Trading:WatchSymbols",
    ["BTC-USDT", "ETH-USDT", "SOL-USDT", "XRP-USDT", "BNB-USDT", "DOGE-USDT", "ADA-USDT", "AVAX-USDT", "LINK-USDT", "TON-USDT"])];
options.Blacklist = [.. BindStrArray(builder.Configuration, "Trading:Blacklist", ["LUNA", "FTT", "MEME1"])];
options.InternDecision.RiskAppetiteRatios = BindDblArray(builder.Configuration, "Trading:InternDecision:RiskAppetiteRatios", [0.25, 0.5, 1.0]);

// ============ 全局异常兜底：任何未捕获异常 → 全堆栈落盘 data/last-error.log + 控制台 ============
// 用途：若电路/后台任务抛异常，仅看 message 难以定位（如 "Destination array was not long enough"），
// 落盘完整堆栈后即可一步定位；同时避免 UnobservedTaskException 被静默吞掉。
var errorLogPath = Path.Combine(Path.GetDirectoryName(ResolveDbPath(options)) ?? "data", "last-error.log");

void DumpError(string title, Exception? ex)
{
    try
    {
        var text = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {title}\n{ex}\n";
        File.AppendAllText(errorLogPath, text);
        Console.Error.WriteLine("!!" + text);
    }
    catch (Exception dumpEx)
    {
        // 兜底失败也绝不能再抛出
        Console.Error.WriteLine("!![错误记录失败] " + dumpEx.Message);
    }
}

AppDomain.CurrentDomain.UnhandledException += (_, e) =>
    DumpError("UNHANDLED_EXCEPTION",
        e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString() ?? "unknown"));
TaskScheduler.UnobservedTaskException += (_, e) =>
{
    DumpError("UNOBSERVED_TASK_EXCEPTION", e.Exception);
    e.SetObserved();
};

// ============ 基础设施（单例） ============
builder.Services.AddSingleton(options);
builder.Services.AddSingleton(options.Simulation);
builder.Services.AddSingleton<Dvc.Engine.ILogger, ConsoleLogger>();
builder.Services.AddSingleton<EngineEventBus>();
builder.Services.AddSingleton(sp => new AppDb(ResolveDbPath(options)));
builder.Services.AddSingleton<SystemConfigRepository>();
builder.Services.AddSingleton<TradingConfigStore>();
builder.Services.AddSingleton<OrderRepository>();
builder.Services.AddSingleton<ThoughtRepository>();
builder.Services.AddSingleton<RiskInterceptRepository>();
builder.Services.AddSingleton<SettlementRepository>();

// ============ 配置加载：唯一来源 = appsettings|env（DB 不存配置） ============
var db = new AppDb(ResolveDbPath(options));
db.Initialize();
var configRepo = new SystemConfigRepository(db);
var configStore = new TradingConfigStore(configRepo);
// 强制开关：Trading:ForceInternDecision=true → 所有决策一律 InternDecision（横幅/UI 同步一致）
if (options.ForceInternDecision)
{
    options.AiProvider = AiProvider.InternDecision;
}

var config = configStore.LoadOrSeedAsync(options).GetAwaiter().GetResult();
builder.Services.AddSingleton(configStore);
builder.Services.AddSingleton(new RuntimeState(config));

builder.Services.AddSingleton<MarketContextStore>();
builder.Services.AddSingleton<UiEventStore>();
builder.Services.AddSingleton<TradingControlService>();

// ============ 数据源双模式（Paper / Live 可切换） ============
if (options.DataSourceMode == DataSourceMode.Paper)
{
    var symbols = options.DefaultAssetScope == AssetScope.MultiPair ? options.WatchSymbols : [options.ActiveSymbol];
    var sim = new SimulationMarket([.. symbols], options.Simulation, config.InitialCapital);
    builder.Services.AddSingleton<IMarketDataFeed>(sim);
    builder.Services.AddSingleton<IOrderGateway>(sim);
}
else
{
    var symbols = options.DefaultAssetScope == AssetScope.MultiPair ? options.WatchSymbols : [options.ActiveSymbol];
    builder.Services.AddSingleton<IMarketDataFeed>(sp => new OkxMarketFeed(options.Okx, [.. symbols]));
    builder.Services.AddSingleton<HttpClient>(sp => new HttpClient { BaseAddress = new Uri(options.Okx.RestBaseUrl) });
    builder.Services.AddSingleton<IOrderGateway>(sp => new OkxOrderGateway(options.Okx, sp.GetRequiredService<HttpClient>()));
}

// ============ AI 决策引擎（Mock / 本地 LLM 网关 / Intern-Decision 可切换） ============
builder.Services.AddSingleton<MockStrategyAi>();
builder.Services.AddSingleton(sp => new DeepSeekStrategyAi(options.DeepSeek, new HttpClient()));
builder.Services.AddSingleton(sp => new Dvc.Ai.InternDecision.InternDecisionStrategyAi(options.InternDecision, new HttpClient()));
builder.Services.AddSingleton<Func<AiProvider, IStrategyAi>>(sp =>
{
    var mock = sp.GetRequiredService<MockStrategyAi>();
    var ds = sp.GetRequiredService<DeepSeekStrategyAi>();
    var intern = sp.GetRequiredService<Dvc.Ai.InternDecision.InternDecisionStrategyAi>();
    return provider => provider switch
    {
        AiProvider.DeepSeek => ds,
        AiProvider.InternDecision => intern,
        _ => mock,
    };
});

// ============ 引擎管道（三管道物理隔离 + 风控巡检 + Hub 转发） ============
builder.Services.AddSingleton<AccountService>();
builder.Services.AddSingleton<OrderExecutionService>();
builder.Services.AddSingleton<DecisionOrchestratorService>();
builder.Services.AddSingleton<MarketIngestionService>();
builder.Services.AddSingleton<RiskMonitorService>();
builder.Services.AddSingleton<HubForwarderService>();

builder.Services.AddHostedService(sp => sp.GetRequiredService<OrderExecutionService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<DecisionOrchestratorService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<MarketIngestionService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<RiskMonitorService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<HubForwarderService>());

// ============ Web 层 ============
builder.Services.AddSignalR();
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddScoped<ScopedUiBridge>();

var app = builder.Build();

// HTTP 渲染管线兜底：Blazor 预渲染/请求处理抛异常同样落盘完整堆栈（排在 UseExceptionHandler 之前，先记录再交还异常）
app.Use(async (ctx, next) =>
{
    try
    {
        await next();
    }
    catch (Exception ex)
    {
        DumpError($"HTTP_REQUEST {ctx.Request.Method} {ctx.Request.Path}", ex);
        throw;
    }
});

// ============ Live 模式启动自检：风控基准与实际入金一致性 ============
if (options.DataSourceMode == DataSourceMode.Live)
{
    try
    {
        var gateway = app.Services.GetRequiredService<IOrderGateway>();
        var bal = await gateway.GetBalancesAsync(app.Lifetime.ApplicationStopping);
        var diffRatio = options.InitialCapital > 0m
            ? Math.Abs(bal.EquityUsd - options.InitialCapital) / options.InitialCapital
            : 1m;
        if (diffRatio > 0.2m)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine(
                $"[启动自检] ⚠ 风控基准 InitialCapital=${options.InitialCapital:0.00} 与 OKX 实际权益 ${bal.EquityUsd:0.00} 偏差 {diffRatio:P0}——" +
                $"熔断线(×{options.CircuitBreakerRatio:P0})与单笔上限(×{options.MaxOrderSizeRatio:P0})均按前者计算，请对齐！");
            Console.ResetColor();
        }

        if (options.InitialCapital < 100m)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine(
                $"[启动自检] ⚠ InitialCapital=${options.InitialCapital:0.00} < $100：单笔上限=" +
                $"${RiskEngine.MaxOrderSize(options.InitialCapital, bal.EquityUsd, options.MaxOrderSizeRatio):0.00} 可能低于 OKX 现货最小下单名义金额（约 $10），订单会被拒");
            Console.ResetColor();
        }

        Console.WriteLine(
            $"[启动自检] OKX 实盘余额核对完成：权益 ${bal.EquityUsd:0.00}，可用 ${bal.AvailableUsd:0.00}，持仓 {bal.Holdings.Count} 个币种");
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine(
            $"[启动自检] ⚠ 实盘余额预检失败（不影响启动，1s 风控巡检会持续监测）: {ex.Message}");
        Console.WriteLine($"[启动自检]   ↳ 若提示 code=50112 Invalid OK-ACCESS-TIMESTAMP（对错密钥同码），说明本机到 OKX 的签名 REST 被网络拦截（公司代理/非标准网关），公开行情不受影响但无法读余额/下单；请用直连网络或 OKX App 端 API 调试页验证 Key。");
        Console.ResetColor();
    }
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

app.UseStaticFiles();
app.UseAntiforgery();

app.MapRazorComponents<Dvc.Web.Components.App>()
    .AddInteractiveServerRenderMode();
app.MapHub<TradingHub>("/hubs/trading");

app.Lifetime.ApplicationStarted.Register(() =>
    Console.WriteLine($"""
        ╔══════════════════════════════════════════════════════════╗
        ║  OKEX-AI 自动交易 · 决策虚拟币 · 本机看板已启动              ║
        ║  地址   : http://localhost:5000                            ║
        ║  数据源 : {options.DataSourceMode,-12}  AI : {options.AiProvider,-10}  初始本金 ${config.InitialCapital:0.00} ║
        ║  数据库 : {ResolveDbPath(options)}                         ║
        ╚══════════════════════════════════════════════════════════╝
        """));

app.Run();

static string ResolveDbPath(TradingOptions options)
{
    return Path.IsPathRooted(options.DbPath)
        ? options.DbPath
        : Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), options.DbPath));
}

/// <summary>Blazor 组件向 Singletons 的安全访问桥（scoped 包装，避免组件直接依赖单例可变态）。</summary>
public sealed class ScopedUiBridge(
    UiEventStore store,
    TradingControlService control)
{
    public UiEventStore Store { get; } = store;

    public TradingControlService Control { get; } = control;
}
