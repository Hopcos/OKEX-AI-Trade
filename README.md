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
- 中间：AI 思考链打字机（流式 reasoning）+ 复利阶梯曲线图（红=初始本金红线、绿=总权益、蓝=单笔买单上限（权益×比例））
- 右侧：订单流水 / 流水广播；顶部 🎮🌱📈 三皮肤无刷新切换（localStorage 记忆）
- `/history`：Agent_Thought_Log、Trade_Order、Risk_Intercept、Asset_Snapshot 四表复盘

代码内默认值：`DataSourceMode=Paper`（GBM 行情仿真 400ms/tick）、`AiProvider=Mock`（规则型 MockAI，思考链打字机），初始本金 $20，开箱即跑，无需任何 API Key。
仓库自带的 `src/Dvc.Web/appsettings.json` 则预设为 `AiProvider=InternDecision` + `ForceInternDecision=true`（需要可访问的 Intern-Decision 服务；想离线体验请把这两项改回 `Mock` / `false`，见第六节「appsettings 参数详解」）。

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
| `InternDecision` | POST `{BaseUrl}/v1/decisions` 结构化决策：`state`（盘面/账户文本+完整 JSON 快照）+ 七类问题（choice/score/noul），答案→标准决策 JSON 映射。**默认由「大脑（LLM）」定期复盘并调教**（见第五节） |

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
| 单笔买单上限 | `MaxOrderSize = min(当前总权益 × MaxOrderSizeRatio, MaxOrderUsd)`（总权益 = 本金 ± 盈亏；盈利放大、亏损自动缩小；只限制买单，卖单是减仓不受限） | `RiskEngine` |
| 黑名单 | 基础币名归一化拒绝（默认 `LUNA/FETT/MEME1` 示例） | `RiskEngine` |
| 价格偏差 | 限价偏离盘口内沿超过 `MaxPriceDeviationBps`（默认 250bp）直接否决 | `RiskEngine` |
| 逆势接刀拦截 | 15M 趋势为 BEARISH/BEARISH_STRONG 时禁止新开买单（`BlockBuysInDowntrend=true`），避免单边下跌逆势接飞刀 | `DecisionOrchestratorService` |
| 摊平止损限制 | 已持仓且浮亏超过 `NoAverageDownPercent`（默认 0.3%）时禁止加仓摊低成本，杜绝单边连环扫货套死全部本金 | `DecisionOrchestratorService` |
| 买入冷却间隔 | 同一交易对买入最小间隔 `BuyCooldownSeconds`（默认 180s），防止单一冲动信号连续满额扫货 | `DecisionOrchestratorService` |
| 盈利护栏 | 卖价必须 ≥ 持仓成本 + 手续费 + `Profit:MinNetProfitBps`；未达保本价就改挂保本价等待，保本价远在盘口之外则继续持有；仅浮亏触及 `Profit:StopLossPercent` 才允许认赔 | `DecisionOrchestratorService` + `ProfitPolicy` |
| 总仓位上限 | 持仓 + 挂买单占用 ≤ 权益 × `MaxExposureRatio`；买单额度收敛到剩余空间，满了就只卖不买 | `RiskEngine` + `DecisionOrchestratorService` |
| 持仓管家 | 独立 2s 巡检：买一 ≥ 保本价立即止盈；否则在保本价挂止盈卖单；浮亏达止损线止损离场；覆盖全部持仓（含已不是聚焦标的的币） | `PositionManagerService` |
| 大脑止血 | 大脑指引 `Allow_New_Buys=false` 时，买单一律降级为观望（InternDecision 模式） | `DecisionOrchestratorService` |
| 资金不足/持仓不足 | 下单前校验可用 USD（已扣除挂单冻结与手续费）/ 卖出额度收敛到持仓市值（Paper 精确清仓不留尘埃） | `RiskEngine` + `OrderExecutionService` |
| TTL 挂单自毁 | 120s 未成交自动撤销并知会 AI（ORDER_TIMEOUT 唤醒） | `OrderExecutionService` |
| 亏损事件唤醒 | 卖出亏损单成交 或 权益跌破结算基准 → 高优先级通道即时唤醒 AI 复盘归因 | `OrderExecutionService/AccountService` |
| 30% 硬熔断 | 权益 < 初始本金×`CircuitBreakerRatio`（默认 70%）：市价清仓全部持仓 + 撤全部挂单 + `RunState=Halted` 永久停 AI | `RiskMonitorService`（1s 巡检，独立管道） |
| 熔断二道闸 | 每笔 LIMIT 下单前复核熔断底线（fail-closed：余额查询异常一律拒单），杜绝熔断瞬间竞态漏单 | `OrderExecutionService` |
| 永续停止 | `RunState=Halted` 持久化落库，**重启也不恢复交易**；熔断后每 2s 清扫残余挂单防漏网成交 | `RiskMonitorService` + `OrderExecutionService` |
| 余额异常自愈 | 余额接口失败沿用上次快照并节流重试，巡检进程永不因异常退出；失败日志 30s 节流 | `AccountService` + `RiskMonitorService` |
| LLM 429/5xx 容错 | DeepSeek 客户端对 429/5xx/网络异常指数退避重试（2s/4s×2）；LLM 提供方下低优先级策略节拍按 `Trading:AiMinIntervalSeconds`（默认 4s）节流防限流，高优先级（亏损/宏观/控制）不节流 | `DeepSeekStrategyAi` + `DecisionOrchestratorService` |

风控拦截每次落库 `Risk_Intercept`，看板盾牌计数实时递增。
熔断链已端到端实测：权益跌破熔断线 → 市价清仓（Filled）→ `RunState=Halted` → 决策进程永久停止，重启同样保持停止；实测最大亏损 < 初始本金×1%（测试把熔断线拉到 99.9% 触发）。

---

## 四、资金模型与盈亏口径（"给一笔钱，只用这笔钱"）

`Trading:InitialCapital` 就是**本金预算**：系统只动用这一笔钱，下单、盈亏、权益、单笔上限、熔断线全部围绕它核算。

| 项目 | 口径 |
|---|---|
| 总权益 | 现金（含挂单冻结）+ 持仓按最新价市值；起点 = 本金 |
| 累计盈亏 | 总权益 − 本金（= 已实现 + 浮动，均已扣手续费） |
| 已实现盈亏 | 每笔卖出：`成交额×(1−费率) − 数量×持仓均价`；持仓均价含买入手续费，加仓按加权平均 |
| 可用 USD | 现金 − 挂买单冻结（`挂单价×数量×(1+费率)`）；多笔挂单合计不可能超出本金 |
| 单笔买单上限 | `min(总权益 × MaxOrderSizeRatio, MaxOrderUsd)`，盈利后放大、亏损后缩小 |
| 总仓位上限 | 持仓市值 + 挂买单冻结 ≤ 总权益 × `MaxExposureRatio`（默认 60%），永远保留现金 |
| 可新买额度 | `min(单笔上限, 仓位上限剩余, 可用现金)`；< $1 时不再买入，专注卖出 |
| 手续费 | `MakerFeeBps` 挂单（默认 8bp=0.08%）+ `TakerFeeBps` 吃单（默认 10bp=0.10%）：Live 账本买入按 Maker、卖出按 Taker 精确核算；保本价取保守费率 `FeeBps = max(Maker, Taker)` 确保不亏 |
| 熔断线 | 总权益 < 本金 × `CircuitBreakerRatio` |

**买卖循环（盈利只在卖出时兑现）**：低位买入 → 持有 → 到保本价之上卖出落袋 → 现金回笼 → 再买入。为了保证循环转得起来：
1. **总仓位封顶**：持仓 + 挂买单 ≤ 权益 × `MaxExposureRatio`（60%），永远留现金；买单额度会收敛到剩余空间，满了就只卖不买。
2. **持仓管家**（`PositionManagerService`，独立于 AI、2 秒一次）：每个持仓必有退出计划——买一价 ≥ 保本价立即止盈；否则在保本价挂止盈卖单整仓等待；浮亏达止损线则止损离场。加仓后均价变化会自动撤单重挂，保证整仓都被覆盖。
3. **InternDecision 卖出优先**：快照里带 `Exposure_Percent / Buy_Room_USD / Position_Sellable_Now`，持仓已能盈利卖出时，买入/观望的意图会被改成卖出落袋；仓位满时买入改为卖出或观望。
4. 卖单有效期 `SellOrderTtlSeconds`（600s）比买单（120s）更长，且不会重复挂卖。
5. **反空转**：连续 `IdleProbeMinutes`（3 分钟）没有新买单时，InternDecision 的观望会被改成小仓位试单买入——空仓就买目标币；**已持有一个币也不干等**，会在候选池再挑一个无持仓、价差 ≤ `IdleProbeMaxSpreadBps` 的币并行周转（同时持仓币种数 ≤ `MaxOpenPositions`）。候选池为空（Single-Pair 或其他币价差过宽）时，会在已持有的标的上继续小额加仓，仍受总敞口/单笔上限约束；Single-Pair 下一旦该试单了，系统也会临时提供候选池。市值 < $1 的尘埃不算持仓（卖不掉，也不应阻止买入）。试单直接吃卖一价（限价挂在卖一上方 5bp 缓冲，保证入场；上行行情里排买一永远买不到）。**重启恢复**：启动时会恢复库里的未成交订单——Live 向 OKX 查最新状态（已成交/部分成交落库、仍挂着的重新跟踪并按 TTL 撤单），没有 OKX 订单号的（Paper 时代遗留）直接清理；Paper 的挂单在内存里，重启即清理。大脑的风险档位有下限 `Brain:MinRiskRatio`，大脑 LLM 的"禁止新买"只约束 Intern 自己的买入意图，试单只被规则硬止血（回撤逼近熔断线）拦截。
6. **止盈目标随持仓时间下调**：刚买入时要求净赚 `MinNetProfitBps`（大脑可再加最多 `MaxExtraProfitBps`），之后在 `TargetDecayMinutes`（30 分钟）内线性降到 `MinNetProfitFloorBps`（2bp，仍覆盖双边手续费、不亏钱）。持仓管家会把挂在旧高价的卖单撤掉、按新目标降价重挂。目的是保证周转：一笔仓位不会占着额度干等一个够不到的目标价。

- **Paper**：纸面账户从本金起步，本身就是预算账户。挂单会冻结资金/持仓，不会"一份钱花两次"。
- **Live**：OKX 账户余额可能远大于本金。系统用**预算账本**（`BudgetLedger`）按本系统自己的成交流水重放出"这笔钱"的现金/持仓/已实现盈亏，交易所里其它资金与其它币**不参与**盈亏、不会被动用、也不会被熔断清仓误卖；可用额度同时不超过交易所真实可用 USDT。已实现盈亏（OKX 不回传）由账本补算并落库，所以亏损唤醒与复盘统计在实盘同样有效。账本根据买入挂单（`MakerFeeBps`，默认 8bp）与卖出（`TakerFeeBps`，默认 10bp）精确分边核算。
- **预算大于交易所真实 USDT 时**（例如本金预算 80、账户里只有 18.58 USDT）：能动用的现金取两者较小值；"已占用仓位"只统计真实持仓市值 + 挂买单冻结，**不会**把"预算里没有真实资金的那部分"误算成已投入仓位（否则仓位会被判为"已满"而一笔都不买）。建议把 `InitialCapital` 调到与真实可用资金相当，盈亏口径才贴近实际。
- 启动自检：Live 会提示"可用 USDT 少于本金预算"或"单笔上限低于交易所可下单数量"。实测 OKX 现货以币种数量精度（`minSz/lotSz`）为准，无硬性 $10 门槛，小额多笔拆单完全可行。

---

## 五、大脑（LLM）复盘：给 InternDecision 注入"灵魂"

InternDecision 负责每几秒一次的高频战术决策；**大脑**是战略层（`BrainService`），用 LLM 定期复盘并调教它。**默认启用**（`Trading:Brain:Enabled=true`），仅在实际决策提供方为 InternDecision 时生效。

**什么时候复盘**
- 定期：每 `Brain:ReviewIntervalMinutes`（默认 20）分钟。
- 关键阶段（`Brain:ReviewOnKeyEvents=true`）：系统启动、每小时宏观评估、亏损事件唤醒——决策前先复盘，本轮决策就带着新指引。两次复盘至少间隔 `MinReviewIntervalSeconds`（启动阶段不受限）。

**复盘看什么**：本金/权益/盈亏/回撤与熔断线安全垫、持仓与均价、最近 `LookbackOrders` 笔订单的胜率与盈亏、连续亏损、风控拦截、聚焦标的盘面、上一份指引。

**产出什么（`BrainDirective`）**：随输入快照（`Brain_Directive`）注入 InternDecision，并写入复盘库 `Agent_Thought_Log`（`BRAIN_*`），看板思考链面板会显示 "BRAIN_REVIEW"：

| 字段 | 作用 |
|---|---|
| `Posture` | AGGRESSIVE / BALANCED / DEFENSIVE / CAPITAL_PRESERVATION |
| `Max_Risk_Ratio` | 风险档位上限：InternDecision 选出的下单系数不得超过它 |
| `Allow_New_Buys` | false = 持仓期间不再加仓、先卖出回笼现金；**空仓时不生效**（不会冻死账户）。只有规则判定的回撤硬止血（权益回撤 ≥ 20%）才会连空仓也禁买 |
| `Min_Net_Profit_Bps` / `Stop_Loss_Percent` | 覆盖盈利护栏参数（只能更严：保本利润只升不降、止损线只紧不松） |
| `Guidance` / `Reasoning` | 给 InternDecision 读的战术指引 / 复盘归因 |

**安全设计**：LLM 输出会与"规则复盘"（回撤/连亏/胜率的确定性规则）合并，LLM 只能比规则更谨慎，但有下限（风险档位不低于 `MinRiskRatio`、保本利润最多加码 `MaxExtraProfitBps`，"禁止新买"空仓不生效）以免把系统冻死；所有数值钳制；大脑不能突破单笔上限/熔断线/黑名单。LLM 未配置、超时或出错时自动退回规则复盘并冷却 2 分钟，永远有一份朝着盈利方向的指引。指引超过 `DirectiveTtlMinutes` 未刷新即作废。

**LLM 连接**：`Brain:BaseUrl/ApiKey/Model` 留空则复用 `Trading:DeepSeek` 的同名配置（OpenAI 兼容 `/chat/completions`，非流式）。

**"灵魂"（`ProfitSoul`）**：无论哪个提供方、哪个阶段，永远不变的方向是"在给定本金内稳定盈利"——本金是命、不亏钱卖出、不盈利不出手、亏损缩手盈利落袋、只用这笔钱。这段使命被注入 LLM 系统提示词、大脑复盘提示词与 InternDecision 的 state/问题说明，并由盈利护栏在代码层兜底。

---

## 六、appsettings 参数详解

配置文件：`src/Dvc.Web/appsettings.json`（基础）→ `appsettings.{Environment}.json`（如 `appsettings.Live.json`、`appsettings.DeepSeek.json`）→ 环境变量，后者覆盖前者。环境变量用双下划线分隔层级，如 `Trading__Brain__Enabled=false`、`Trading__AiProvider=Mock`。**数组类参数（`WatchSymbols`/`Blacklist`/`RiskAppetiteRatios`）整体覆盖而不是追加**。`System_Config` 库表只保存运行状态（熔断停机、结算基准、盾牌计数、皮肤），不再保存配置；热切换只改内存，重启回到配置文件。

### 6.1 `Trading`（全局）

| 参数 | 默认值 | 说明 |
|---|---|---|
| `DataSourceMode` | `Paper` | 数据源/下单通道：`Paper` 模拟行情+纸面撮合；`Live` OKX 实盘（需填 `Okx` 密钥） |
| `AiProvider` | 代码 `Mock` / 仓库 `InternDecision` | 决策引擎：`Mock` 规则引擎；`DeepSeek` OpenAI 兼容 LLM 流式；`InternDecision` 结构化决策服务 |
| `ForceInternDecision` | 代码 `false` / 仓库 `true` | `true` 时每轮决策强制用 InternDecision，无视 `AiProvider` 与看板热切换（也会让 `appsettings.DeepSeek.json` 的切换失效，需先改为 `false`） |
| `DefaultTradingMode` | `Scalping` | 启动交易模式：`Scalping` 高频盘口微套（2s 节拍）；`Swing` 波段（K 线收盘唤醒）。AI/看板可运行时切换 |
| `DefaultAssetScope` | 代码 `SinglePair` / 仓库 `MultiPair` | 启动标的范围：`SinglePair` 只交易 `ActiveSymbol`；`MultiPair` 在 `WatchSymbols` 中轮动。**同时决定启动时订阅哪些行情**（SinglePair 启动后运行时切到 MultiPair 不会新增订阅） |
| `ActiveSymbol` | `BTC-USDT` | 启动聚焦交易对 |
| `InitialCapital` | `20.0` | **本金预算（USD）**，见第四节；风控、盈亏、熔断线的唯一基准 |
| `LedgerStartUtc` | 空 | **预算账本起算时间**（UTC，仅 Live；如 `2026-10-25T17:20:00Z`）。空 = 重放库内全部成交；库里混有旧 Paper 成交、或调整了 `InitialCapital` 时，设为"从现在起"，账本只统计该时间之后创建的订单，权益/盈亏才不会被历史成交污染 |
| `DbPath` | `data/trading.db` | SQLite 路径，相对路径基于进程工作目录 |
| `WatchSymbols` | 10 个主流币 | MultiPair 轮动观察池 |
| `Blacklist` | `LUNA, FTT, MEME1` | 黑名单基础币（大小写/带 `-USDT` 后缀均可），买卖一票否决 |
| `MaxOrderSizeRatio` | `0.20` | 单笔买单上限比例：当前总权益 × 该比例 |
| `MaxOrderUsd` | `0`（不启用） | 单笔买单绝对上限（USD），与"权益×比例"取较小者 |
| `MaxExposureRatio` | `0.6` | 总仓位上限：持仓市值 + 挂买单冻结 ≤ 总权益 × 该值。永远留一部分现金，保证"买 → 卖回笼 → 再买"的循环转得起来；仓位满了买单会被收敛/拒绝，系统只能卖 |
| `MakerFeeBps` | `8` | 挂单单边手续费（bp，8=0.08%）：OKX 现货 Lv1 费率；Live 账本买入按此计费 |
| `TakerFeeBps` | `10` | 吃单单边手续费（bp，10=0.10%）：OKX 现货 Lv1 费率；Live 账本卖出按此计费 |
| `FeeBps` | `10` | 衍生保守费率（只读属性，`= max(Maker, Taker)`）：用于 Paper 撮合扣费与保本卖出价计算，宁可多算 2bp，绝不亏钱卖出 |
| `CircuitBreakerRatio` | `0.70` | 硬熔断：权益 < 本金×该值 → 清仓+永久停机 |
| `AiMinIntervalSeconds` | `4.0` | LLM 提供方下，低优先级节拍的最小调用间隔（秒），防网关 429；高优先级唤醒不受限；Mock 不生效 |
| `DefaultTimezoneOffsetHours` | `0` | 预留，当前版本未使用 |
| `OrderTtlSeconds` | `120` | 限价单有效期（秒，下限 15），超时自动撤单并唤醒 AI |
| `MaxPriceDeviationBps` | `250` | 挂单价相对盘口内沿（买看买一、卖看卖一）的最大偏离（bp），超出视为 AI 幻觉被否决；盈利护栏改价时留 10bp 余量 |

### 6.2 `Trading:Profit`（盈利护栏）

| 参数 | 默认值 | 说明 |
|---|---|---|
| `Enabled` | `true` | 是否启用卖出盈利护栏（对所有决策提供方生效） |
| `MinNetProfitBps` | `15` | 卖出必须净赚的最小利润（bp）：保本价 = 持仓均价×(1+该值)/(1−费率) |
| `StopLossPercent` | `3.0` | 止损线（%）：浮亏达到才放行认赔卖出；大脑只能把它调得更紧 |
| `AutoExit` | `true` | 持仓管家：独立于 AI 的 2 秒巡检，对**所有持仓**自动退出——买一价 ≥ 保本价立即止盈；否则在保本价挂止盈卖单；浮亏达止损线则止损离场 |
| `SellOrderTtlSeconds` | `600` | 卖单（止盈）有效期（秒）；买单仍按 `OrderTtlSeconds`（120）。卖单是回笼现金的关键，活得更久 |
| `IdleProbeMinutes` | `3` | **空转兜底**（0=关闭）：连续这么多分钟没有新买单（一直 HOLD）时，即使 InternDecision 说观望，也按小仓位试单买入（吃卖一价，成交后持仓管家自动挂卖单），让"买→卖→回笼"的循环转起来。不交易就没有收益；试单仍受单笔上限/总仓位/风控约束，规则硬止血时不触发 |
| `IdleProbeMaxSpreadBps` | `8` | 空转试单允许的最大价差（bp）：试单吃卖一价，价差就是成本，过宽的币种不试 |
| `MaxOpenPositions` | `3` | 空转试单时允许同时持仓的币种数：已持有一个币又空转时，会再挑一个无持仓的币并行周转，到上限为止 |
| `TargetDecayMinutes` | `30` | 止盈目标衰减时长（0=不衰减）：持仓越久要求净利润越低，该时长后降到 `MinNetProfitFloorBps`；卖单会随之降价重挂 |
| `MinNetProfitFloorBps` | `2` | 止盈目标衰减到底的净利润（bp）：仍覆盖双边手续费，不会亏钱卖出 |
| `BlockBuysInDowntrend` | `true` | **下跌趋势接刀拦截**：15M 趋势为 `BEARISH` 或 `BEARISH_STRONG` 时拒绝新开买单，逆势接飞刀期望为负 |
| `NoAverageDownPercent` | `0.3` | **摊平止损限制**（%，0=关闭）：已持仓且现价低于持仓均价超过该比例时禁止加仓，防止越跌越买将整笔本金套死 |
| `BuyCooldownSeconds` | `180` | **买入冷却间隔**（秒，0=关闭）：同一交易对两次买入的最小间隔，防止单点冲动信号连续连环扫货 |

### 6.3 `Trading:Brain`（大脑 LLM 复盘，详见第五节）

| 参数 | 默认值 | 说明 |
|---|---|---|
| `Enabled` | `true` | 总开关；仅 InternDecision 模式下生效 |
| `ReviewIntervalMinutes` | `20` | 定期复盘间隔（分钟） |
| `ReviewOnKeyEvents` | `true` | 启动/每小时宏观/亏损事件时是否同步复盘 |
| `MinReviewIntervalSeconds` | `60` | 两次复盘的最小间隔（秒），防亏损事件密集刷爆 LLM |
| `LookbackOrders` | `40` | 复盘回看订单数 |
| `DirectiveTtlMinutes` | `60` | 指引有效期，超期作废 |
| `TimeoutSeconds` | `45` | 单次复盘 LLM 超时（关键阶段复盘最多阻塞决策这么久，超时退回规则复盘） |
| `MinRiskRatio` | `0.5` | 风险档位下限：LLM 再保守，`Max_Risk_Ratio` 也不会低于它（规则复盘自身判定防守/保本时除外），防止下单额被压到≈0 而永远不交易 |
| `MaxExtraProfitBps` | `5` | LLM 在规则保本利润之上最多再加码多少 bp；要求过高会让卖单永远成交不了 |
| `Temperature` | `0.3` | LLM 温度 |
| `BaseUrl` / `ApiKey` / `Model` | 空 | 大脑 LLM 连接（OpenAI 兼容）；留空复用 `DeepSeek` 同名项；空或 `xxx` 占位视为未配置，走规则复盘 |

### 6.4 `Trading:Simulation`（Paper 行情仿真）

| 参数 | 默认值 | 说明 |
|---|---|---|
| `TickIntervalMs` | `400` | 行情推进间隔（毫秒，下限 50） |
| `BasePrice` | `96000` | BTC-USDT 基准价（其它币用内置参考价） |
| `AnnualVolatility` | `0.55` | GBM 年化波动率 |
| `SpreadBps` | `6` | 盘口价差（bp） |
| `MockAiCharDelayMs` | `6` | Mock AI 思考链打字机每字符延迟（毫秒） |
| `RegimeSwitchSeconds` | `240` | 震荡/上涨/下跌三态切换的平均周期（秒，下限 10） |

### 6.5 `Trading:Okx`（Live 实盘）

| 参数 | 默认值 | 说明 |
|---|---|---|
| `ApiKey` / `SecretKey` / `Passphrase` | 空 | OKX API 凭证（放 `appsettings.Live.json`，已被 `.gitignore` 忽略；勿提交） |
| `PublicWsUrl` | `wss://ws.okx.com:8443/ws/v5/public` | 公共行情 WebSocket |
| `RestBaseUrl` | `https://www.okx.com` | REST 基地址（下单/余额/K 线兜底） |
| `OrderBookChannel` | `books5` | 盘口订阅频道（五档） |
| `ReconnectDelayMs` | `3000` | WebSocket 断线重连基础延迟，按次数线性退避，上限 30s |

### 6.6 `Trading:DeepSeek`（OpenAI 兼容 LLM；`AiProvider=DeepSeek` 时作决策引擎，同时是大脑的默认连接）

| 参数 | 默认值 | 说明 |
|---|---|---|
| `BaseUrl` | `https://api.deepseek.com` | 网关地址（实际请求 `{BaseUrl}/chat/completions`，网关若带 `/v1` 前缀需写进去） |
| `ApiKey` | 空 | Bearer Key |
| `Model` | `deepseek-chat` | 模型名 |
| `UseReasoning` | `true` | 预留，当前版本未使用（思考链按响应里的 `reasoning_content` 自动识别） |
| `Temperature` | `0.2` | 温度 |
| `TimeoutSeconds` | `60` | 单次流式推理超时 |

### 6.7 `Trading:InternDecision`（结构化决策服务）

| 参数 | 默认值 | 说明 |
|---|---|---|
| `BaseUrl` | `http://localhost:8000` | 服务地址（`POST {BaseUrl}/v1/decisions`） |
| `ApiKey` | 空 | Bearer Key |
| `Temperature` | 代码 `1.0` / 仓库 `0.5` | 决策采样温度 |
| `TimeoutSeconds` | `30` | 请求超时（下限 5） |
| `RiskAppetiteRatios` | 代码 `[0.25, 0.5, 1.0]` / 仓库 6 档 | 风险档位 → 下单额占"单笔买单上限"的比例，档位数决定档位命名（保守…激进）；大脑的 `Max_Risk_Ratio` 会再封顶 |

---

## 七、架构（三管道物理隔离 + 决策中心）

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

## 八、验证结果（本次实跑）

- 全解决方案 `dotnet build DecisionVirtualCoin.slnx`：**0 警告 0 错误**。
- 默认 Paper+Mock：决策节拍 ~3.5s/轮，限价买单成交（Filled）、卖单挂单、TTL 120s 自动撤单、亏损唤醒（权益回撤→高优通道）、风控"持仓不足/可用资金不足"拒绝均实测通过。
- DeepSeek 真模型流式：thinking 打字机 + 决策 JSON 解析 + 挂单执行实测通过（修复了两个网关适配问题：new-api 拒收裸 JSON user 消息；max_tokens 截断致 JSON 不闭合 → 8192 + 精炼思考）。
- 看板 http://localhost:5000 与 /history 均 200，四表数据实时可见。

---

## 九、目录结构

```
DecisionVirtualCoin.slnx
src/
  Dvc.Core/          枚举/合约/配置/事件 DTO/Json 源生成上下文
  Dvc.Persistence/   AppDb(WAL) + 四表仓储 + 配置存储（Dapper + SQLite）
  Dvc.Exchange/      SimulationMarket(仿真盘) + Okx(WebSocket 行情/REST 交易网关)
  Dvc.Ai/            IStrategyAi + Mock + DeepSeek(流式) + InternDecision(结构化) + Brain(大脑 LLM 客户端)
  Dvc.Engine/        四管道 + 风控 + 账户/预算账本/结算 + 大脑复盘 + 盈利护栏 + 市场上下文 + AI 输入组装
  Dvc.Web/           Program.cs(组合根/双模式 DI) + Blazor Server 看板(三皮肤) + SignalR
data/                运行期生成的 trading.db
```
