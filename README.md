# OKEX-AI 自动交易 · 决策虚拟币（端到端可运行骨架）

.NET 10 · 纯现货高频量化助手 · 模拟人类交易员的限价挂撤单行为 · 彻底摒弃杠杆与衍生品。
基于《OKEX-AI自动交易的技术方案（最终版）》实现：**双数据源（模拟/实盘可切换）+ 三 AI 提供方（Mock / 本地 LLM 网关 / Intern-Decision）+ 硬编码风控 + SQLite(WAL) 复盘 + Blazor Server 三皮肤看板**。

---

## 一、30 秒上手（默认离线模式，零依赖）

```bash
dotnet run --project src\Dvc.Web\Dvc.Web.csproj --no-launch-profile
```

打开 **http://localhost:5000** 即可看到实时看板：
- 左侧：账户权益 / 策略开关矩阵（Scalping↔Swing、Single↔Multi、聚焦币种）/ 风控盾牌计数
- 中间：AI 思考链打字机（流式 reasoning）+ 复利阶梯曲线图（红=初始本金红线、绿=总权益、蓝=20% 复利上限）
- 右侧：订单流水 / 流水广播；顶部 🎮🌱📈 三皮肤无刷新切换（localStorage 记忆）
- `/history`：Agent_Thought_Log、Trade_Order、Risk_Intercept、Asset_Snapshot 四表复盘

默认配置（`src/Dvc.Web/appsettings.json`）：`DataSourceMode=Paper`（GBM 行情仿真 400ms/tick）、`AiProvider=Mock`（规则型 MockAI，思考链打字机），初始本金 $20，开箱即跑，无需任何 API Key。

---

## 二、双模式切换

### 1) 数据源：Paper ⇄ Live（OKX 实盘）

`appsettings.json` → `Trading:DataSourceMode`：

| 模式 | 行情 | 下单 |
|---|---|---|
| `Paper`（默认） | `SimulationMarket`：GBM 年化波动 55% + 三态区间切换（Range/TrendUp/TrendDown）+ 5 档盘口 + 逐笔撮合 | 仿真账户（FIFO 平均成本、已实现盈亏、OrderFilled 事件） |
| `Live` | `OkxMarketFeed`：OKX v5 **公共 WebSocket（books5 五档/trades 逐笔）** + **REST 轮询 K 线兜底**（15m/1H 经 `/api/v5/market/candles` 每 15s 拉取去重入管道；实测当前网关通道列表拒绝 `candle*` WS 订阅 60018，REST 稳定），断线退避重连 | `OkxOrderGateway`：REST 签名（HMAC-SHA256）+ 限价 `tdMode=cash` + 轮询成交/TTL 自动撤单 |

Live 模式需在配置文件填入 OKX API Key/Secret/Passphrase（见 `appsettings.Live.json`），且建议先在模拟盘验证。

### 2) AI 决策引擎：Mock ⇄ 本地 LLM 网关 ⇄ Intern-Decision

`appsettings.json` → `Trading:AiProvider`：

| 提供方 | 说明 |
|---|---|
| `Mock`（默认） | 内置规则引擎：趋势/盘口不平衡/资金流打分，多币种挑选期望值最高者，思考链打字机 |
| `DeepSeek` | OpenAI 兼容 Chat Completions **流式**：`reasoning`/`reasoning_content` 分片→思考链；最终 JSON 容忍 markdown 围栏提取。 |
| `InternDecision` | POST `{BaseUrl}/v1/decisions` 结构化决策：`state`（盘面/账户文本+完整 JSON 快照）+ 七类问题（choice/score/noul），答案→标准决策 JSON 映射。已预填 `http://localhost:8000` + Bearer Key |

快速切换 LLM 提供方（免改主配置）：
```bash
$env:ASPNETCORE_ENVIRONMENT = "DeepSeek"; dotnet run --project src\Dvc.Web\Dvc.Web.csproj
# 或复制 Trading:AiProvider 绝对作用域（.NET 配置优先级：环境变量 > 环境 JSON > appsettings.json）
$env:Trading__AiProvider = "InternDecision"
```

> 注意：LLM 网关的 new-api 会自动解析"裸 JSON"的 user 消息并炸掉，因此客户端对快照 JSON 加了中文导语前缀（已内置处理）。

---

## 三、硬编码风控（不可被 AI 绕过）

| 规则 | 公式/动作 | 位置 |
|---|---|---|
| 单笔上限 | `MaxOrderSize = (初始本金 + max(0, 总权益 − 初始本金)) × 20%` | `RiskEngine` |
| 黑名单 | 基础币名归一化拒绝（默认 `LUNA/FETT/MEME1` 示例） | `RiskEngine` |
| 价格偏差 | 限价偏离最新价 ±250bp 直接否决 | `RiskEngine` |
| 资金不足/持仓不足 | 下单前校验可用 USD / 卖出额度收敛到持仓市值 | `RiskEngine` + `OrderExecutionService` |
| TTL 挂单自毁 | 120s 未成交自动撤销并知会 AI（ORDER_TIMEOUT 唤醒） | `OrderExecutionService` |
| 亏损事件唤醒 | 卖出亏损单成交 或 权益跌破结算基准 → 高优先级通道即时唤醒 AI 复盘归因 | `OrderExecutionService/AccountService` |
| 30% 硬熔断 | 权益 < 初始本金×70%：市价清仓全部持仓 + 撤全部挂单 + `RunState=Halted` 永久停 AI | `RiskMonitorService`（1s 巡检，独立管道） |
| 熔断二道闸 | 每笔 LIMIT 下单前复核熔断底线（fail-closed：余额查询异常一律拒单），杜绝熔断瞬间竞态漏单 | `OrderExecutionService` |
| 永续停止 | `RunState=Halted` 持久化落库，**重启也不恢复交易**；熔断后每 2s 清扫残余挂单防漏网成交 | `RiskMonitorService` + `OrderExecutionService` |
| 余额异常自愈 | 余额接口失败沿用上次快照并节流重试，巡检进程永不因异常退出；失败日志 30s 节流 | `AccountService` + `RiskMonitorService` |
| LLM 429/5xx 容错 | DeepSeek 客户端对 429/5xx/网络异常指数退避重试（2s/4s×2）；LLM 提供方下低优先级策略节拍按 `Trading:AiMinIntervalSeconds`（默认 4s）节流防限流，高优先级（亏损/宏观/控制）不节流 | `DeepSeekStrategyAi` + `DecisionOrchestratorService` |

风控拦截每次落库 `Risk_Intercept`，看板盾牌计数实时递增。
熔断链已端到端实测：权益跌破熔断线 → 市价清仓（Filled）→ `RunState=Halted` → 决策进程永久停止，重启同样保持停止；实测最大亏损 < 初始本金×1%（测试把熔断线拉到 99.9% 触发）。

---

## 四、架构（三管道物理隔离 + 决策中心）

```
┌─ 管道一 MarketIngestionService ─┐   ┌─ 管道二 DecisionOrchestratorService ─┐   ┌─ 管道三 OrderExecutionService ─┐
│ IMarketDataFeed（Paper/Live）    │   │ 高优先通道(亏损/宏观/控制) > 低优先   │   │ 挂单(限价) / 撤单 / 清仓        │
│ → MarketContextStore(盘口/趋势/  │──▶│ (策略节拍 Scalping 2s / Swing K线)   │──▶│ 撮合成交 → OrderFilled 事件     │
│   资金流/1H 波动/15M 分钟)        │   │ → AiInputSnapshot(规范 JSON 快照)    │   │ → 已实现盈亏 → 亏损唤醒回调     │
│ 1s UiMarketTick → 看板           │   │ → IStrategyAi(Mock/LLM/InternDecision)│   │ 2s 对账轮询 + TTL 自动撤单      │
└──────────────────────────────────┘   │ 风控校验 → ExecCommand / 拦截落库      │   └────────────────────────────────┘
                                        └──────┬───────────────────────────────┘
                                     RiskMonitorService(1s 熔断巡检) ── 独立退出管道
                                     EngineEventBus(Channel) ──▶ HubForwarder → SignalR / 看板
                                     SQLite(WAL)：System_Config / Asset_Snapshot / Trade_Order /
                                     Agent_Thought_Log / Risk_Intercept
```

- **决策唤醒**：两类无界 Channel（高优先先读）；决策串行化（SemaphoreSlim）。
- **AOT 就绪**：`Dvc.Core/Persistence/Exchange/Ai/Engine` 均 `IsAotCompatible=true` + System.Text.Json 源生成（`DvcJsonContext`），无反射序列化。Blazor Server 看板受框架 trim 限制以 JIT 发布（引擎可单独 `dotnet publish -r win-x64` AOT 宿主化）。
- **数据库**：`data/trading.db`（相对运行目录解析），WAL 模式，四核心表 + System_Config 配置镜像。

---

## 五、验证结果（本次实跑）

- 全解决方案 `dotnet build DecisionVirtualCoin.slnx`：**0 警告 0 错误**。
- 默认 Paper+Mock：决策节拍 ~3.5s/轮，限价买单成交（Filled）、卖单挂单、TTL 120s 自动撤单、亏损唤醒（权益回撤→高优通道）、风控"持仓不足/可用资金不足"拒绝均实测通过。
- DeepSeek 真模型流式：thinking 打字机 + 决策 JSON 解析 + 挂单执行实测通过（修复了两个网关适配问题：new-api 拒收裸 JSON user 消息；max_tokens 截断致 JSON 不闭合 → 8192 + 精炼思考）。
- 看板 http://localhost:5000 与 /history 均 200，四表数据实时可见。

---

## 六、目录结构

```
DecisionVirtualCoin.slnx
src/
  Dvc.Core/          枚举/合约/配置/事件 DTO/Json 源生成上下文
  Dvc.Persistence/   AppDb(WAL) + 四表仓储 + 配置存储（Dapper + SQLite）
  Dvc.Exchange/      SimulationMarket(仿真盘) + Okx(WebSocket 行情/REST 交易网关)
  Dvc.Ai/            IStrategyAi + Mock + DeepSeek(流式) + InternDecision(结构化)
  Dvc.Engine/        四管道 + 风控 + 账户/结算 + 市场上下文 + AI 输入组装
  Dvc.Web/           Program.cs(组合根/双模式 DI) + Blazor Server 看板(三皮肤) + SignalR
data/                运行期生成的 trading.db
```
