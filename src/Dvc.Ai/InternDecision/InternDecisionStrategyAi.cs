using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dvc.Core;
using Dvc.Core.Ai;
using Dvc.Core.Configuration;

namespace Dvc.Ai.InternDecision;

/// <summary>
/// Intern-Decision 结构化决策客户端：
/// POST {BaseUrl}/v1/decisions，Body { state, questions, temperature }，
/// 由决策中心将市场/账户状态拆解为 choice / score / noul（决策中心服务端自定义类型，请勿改）三类问题并返回答案。
/// 本实现把答案映射回标准 AiOutputDecision（与 DeepSeek/Mock 输出同构）。
/// 思考链为"问题清单 + 答案即席推理"的本地叙述（打字机同样工作）。
/// </summary>
public sealed class InternDecisionStrategyAi : IStrategyAi
{
    private readonly InternDecisionOptions _opts;
    private readonly HttpClient _http;

    public InternDecisionStrategyAi(InternDecisionOptions opts, HttpClient http)
    {
        _opts = opts;
        _http = http;
    }

    public string ProviderName => "Intern-Decision";

    public async IAsyncEnumerable<AiStreamPart> DecideAsync(AiRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var input = request.Input;
        var fm = input.FinancialMetrics;
        var ms = input.MarketSnapshot;

        var stateText = BuildStateText(input);
        var questions = BuildQuestions(input);
        var payload = new JsonObject
        {
            ["state"] = stateText,
            ["questions"] = questions,
            ["temperature"] = _opts.Temperature,
        };

        // ---------- 组装思考链叙述（问题清单） ----------
        var narrative = BuildNarrative(input, questions);
        var sb = new StringBuilder();
        foreach (var c in narrative)
        {
            ct.ThrowIfCancellationRequested();
            sb.Append(c);
            yield return new AiStreamPart(true, c.ToString());
            await Task.Delay(4, ct);
        }

        // ---------- 调用 Intern-Decision ----------
        var answers = await CallAsync(payload, ct);

        var answerText = string.Join("；",
            new[]
            {
                AnswerText(answers, "market_assessment", "市场评估"),
                AnswerText(answers, "trading_mode", "交易模式"),
                AnswerText(answers, "asset_scope", "标的范围"),
                AnswerText(answers, "focus_symbol", "聚焦标的"),
                AnswerText(answers, "action", "本周期动作"),
                FormatRiskAppetite(answers),
                AnswerText(answers, "stop_and_wait", "清场退守"),
            }.Where(s => !string.IsNullOrEmpty(s)));

        foreach (var c in $"\n<Intern-Decision 返回> {answerText}\n")
        {
            ct.ThrowIfCancellationRequested();
            yield return new AiStreamPart(true, c.ToString());
        }

        // ---------- 映射为标准决策 JSON ----------
        var decision = Map(input, answers);
        var json = JsonSerializer.Serialize(decision, DvcJsonContext.Default.AiOutputDecision);
        yield return new AiStreamPart(false, json);
    }

    // ================= 请求组装 =================

    private static string BuildStateText(AiInputSnapshot i)
    {
        var fm = i.FinancialMetrics;
        var ms = i.MarketSnapshot;
        var cfg = i.CurrentConfig;

        var sb = new StringBuilder();
        sb.AppendLine("【策略】高频现货限价微套，是一个完整的买卖循环：低位买入 → 持有 → 到保本价之上卖出落袋 → 现金回笼 → 再买入。" +
                      "盈利只在卖出时兑现，所以持仓时卖出优先于加仓；仓位满了必须先卖。禁止做空/杠杆，只允许限价单。");
        sb.AppendLine(ProfitSoul.OneLiner);
        sb.AppendLine($"【触发】{i.TriggerReason}，系统时间 {i.SystemTime}");
        sb.AppendLine($"【账户】初始本金 ${fm.InitialCapital:0.00}，当前总权益 ${fm.CurrentTotalEquity:0.00}，累计盈亏 ${fm.AccumulatedProfit:0.00}，" +
                      $"可用 USD ${fm.AvailableUsd:0.00}，风控上限 ${fm.MaxAllowedOrderSize:0.00}，最近亏损 ${fm.LastTradeLossAmount:0.00}，持仓 {fm.PositionBaseQty:0.########}，已实现盈亏 ${fm.RealizedPnl:0.00}");
        if (fm.PositionBaseQty > 0m && fm.PositionAvgCost > 0m)
        {
            sb.AppendLine($"【持仓盈亏】均价 {fm.PositionAvgCost:0.######}（含买入手续费），保本卖出价 {fm.PositionMinProfitSellPrice:0.######}" +
                          $"（卖价低于它就是亏钱，除非已触及止损线），全部持仓浮动盈亏 ${fm.UnrealizedPnl:0.0000}；" +
                          $"已持有 {fm.PositionHeldMinutes:0} 分钟（持仓越久止盈目标越低，最终只要求覆盖手续费，以便尽快周转）");
        }

        if (i.BrainDirective is { } brain)
        {
            sb.AppendLine($"【大脑复盘指引·{brain.Source}/{brain.Trigger}】姿态 {brain.Posture}；风险档位上限 {brain.MaxRiskRatio:0.##}；" +
                          (brain.AllowNewBuys ? "允许开新买单"
                              : brain.HardStop ? "禁止新开买单（硬止血：只许卖出/观望）"
                              : "持仓期间不再加仓（先卖出回笼现金；空仓时照常买入）") +
                          (string.IsNullOrWhiteSpace(brain.Guidance) ? "" : $"；指引：{brain.Guidance}"));
        }

        sb.AppendLine($"【仓位】已占用 ${fm.CommittedUsd:0.00}（{fm.ExposurePercent:0.#}% 权益，上限 {fm.MaxExposurePercent:0.#}%），当前还能新买 ${fm.BuyRoomUsd:0.00}；" +
                      (fm.PositionBaseQty > 0m
                          ? (fm.PositionSellableNow
                              ? $"持仓已在保本价之上（浮动 {fm.PositionUnrealizedPercent:0.###}%）→ 现在就能盈利卖出，请优先选 sell 落袋。"
                              : $"持仓浮动 {fm.PositionUnrealizedPercent:0.###}%，买一价尚未到保本卖出价，卖单会挂在保本价等待，可 hold 或继续观察。")
                          : "当前空仓。"));
        if (fm.PositionBaseQty <= 0m && fm.IdleMinutes >= 1m)
        {
            sb.AppendLine($"【空转提醒】已空仓且 {fm.IdleMinutes:0.#} 分钟没有任何订单。不交易就没有收益：只要价差能覆盖双边手续费、" +
                          "买一有支撑，就应该小仓位挂买单入场，把买→卖→回笼的循环转起来，而不是一直观望。");
        }

        if (fm.BuyRoomUsd < 1m)
        {
            sb.AppendLine("【资金约束】可新买额度已不足 $1（资金已投入持仓/挂单，或达到总仓位上限）。此时切勿选择 buy——会被拦截并反复重试；" +
                          "应选择 sell 回笼现金（价格不低于保本价），或 hold 等待。");
        }
        sb.AppendLine($"【配置】模式 {cfg.TradingMode}，范围 {cfg.AssetScope}，聚焦 {cfg.ActiveSymbol}，黑名单 {string.Join(',', cfg.Blacklist)}");
        if (ms.ActiveSymbolOrderbook is { } book)
        {
            sb.AppendLine($"【盘口】买一~买五 {FormatLevels(book.Bid1To5)}");
            sb.AppendLine($"【盘口】卖一~卖五 {FormatLevels(book.Ask1To5)}");
        }

        sb.AppendLine($"【行情】最新价 {ms.LastPrice}，1H 波幅 {ms.Volatility1HPercent:0.00}%，15M 趋势 {ms.TrendSignal15M}，" +
                      $"15M 涨跌 {ms.Change15MPercent:0.00}%，5M 成交额 ${ms.Turnover5MUsd:0}");
        if (i.PairSnapshots is { Count: > 0 })
        {
            sb.AppendLine("【候选池】" + string.Join(" | ", i.PairSnapshots.Select(p =>
                $"{p.Symbol} 价{p.LastPrice} 价差{p.SpreadBps:0.0}bps 5M成交${p.Turnover5MUsd:0} 不平衡{p.Imbalance:0.00} {p.TrendSignal15M}")));
        }

        // 附完整 JSON 快照（供决策中心原文参考）
        sb.AppendLine("【完整快照】" + JsonSerializer.Serialize(i, DvcJsonContext.Default.AiInputSnapshot));
        return sb.ToString();
    }

    private JsonObject BuildQuestions(AiInputSnapshot i)
    {
        var cfg = i.CurrentConfig;
        var q = new JsonObject
        {
            ["market_assessment"] = new JsonObject
            {
                ["type"] = "choice",
                ["instructions"] = "结合盘口不平衡、1H 波幅、15M 趋势判断市场状态。注意：震荡≠无机会，高频微套在活跃震荡市里最有利；只有死水横盘才无空间。",
                ["criteria"] = new JsonObject
                {
                    ["trending_up"] = "单边上涨（趋势明确向上）",
                    ["trending_down"] = "单边下跌（趋势明确向下）",
                    ["active_range"] = "活跃震荡（价差窄、盘口活跃、有微套空间）",
                    ["quiet_range"] = "死水横盘（价差宽、成交清淡、无微套空间）",
                },
            },
            ["trading_mode"] = new JsonObject
            {
                ["type"] = "choice",
                ["instructions"] = "本系统是高频现货限价微套机器人，默认应持续在市场内挂单；仅当出现明确单边趋势时才切 Swing 波段。",
                ["criteria"] = new JsonObject
                {
                    ["scalping"] = "高频盘口微套（默认，活跃震荡市，秒级挂撤单）",
                    ["swing"] = "中短线波段（仅限明确单边趋势市，追踪大周期）",
                },
            },
            ["asset_scope"] = new JsonObject
            {
                ["type"] = "choice",
                ["instructions"] = "选择标的选择范围：",
                ["criteria"] = new JsonObject
                {
                    ["single_pair"] = "单高流代币（当前聚焦标的，低消耗）",
                    ["multi_pair"] = "多币种轮动（资金聚焦期望值最高单一币种）",
                },
            },
            ["action"] = new JsonObject
            {
                ["type"] = "choice",
                ["instructions"] = "本策略是高频现货限价微套（Scalping）的完整买卖循环，永远朝盈利方向：盈利只在卖出时兑现。先看【仓位】行：已持仓且能盈利卖出就选 sell 落袋；仓位已满/可新买额度不足就不要选 buy，选 sell 或 hold；仅当空仓或仓位有余地、且预期净利润（扣双边手续费）为正时才选 buy；看不到净利润空间选 hold。若【大脑复盘指引】禁止新开买单则不要选 buy；卖出价不得低于【保本卖出价】（止损线除外）。",
                ["criteria"] = new JsonObject
                {
                    ["buy"] = "挂限价买单：仅限【仓位】有余地（可新买额度足够）时；买一支撑厚（不平衡>0）或价格回踩买一/买二支撑时吃回调/微套，且买入后要有明确的保本价之上卖出目标",
                    ["sell"] = "挂限价卖单（整仓）：已持仓且价格不低于【保本卖出价】（覆盖成本+手续费+最小净利润）时落袋，回笼现金才能开始下一轮；卖一压力重（不平衡<0）更应尽快兑现；仅当浮亏触及止损线才允许认赔离场",
                    ["hold"] = "无净利润空间或无事可做时观望：价差过宽、五档无支撑压力、波动不足以覆盖手续费；持仓尚未到保本价且不到止损线时耐心等待（止盈挂单会自动挂在保本价）",
                    ["cancel_all"] = "环境扭转/单边暴跌时撤销全部挂单，退守现金",
                },
            },
            ["risk_appetite"] = new JsonObject
            {
                ["type"] = "score",
                ["instructions"] = "给定当前波动率与趋势，选择风险档位（决定本周期下单额占风控上限的比例）：",
                ["criteria"] = RiskAppetiteCriteria(),
            },
            ["stop_and_wait"] = new JsonObject
            {
                ["type"] = "noul",
                ["instructions"] = "是否判定为市场环境扭转（如单边暴跌），应立即撤销全部挂单退守现金？",
            },
        };

        if (cfg.AssetScope == "Multi-Pair" && i.PairSnapshots is { Count: > 0 })
        {
            var focus = new JsonObject();
            foreach (var p in i.PairSnapshots.Take(10))
            {
                focus[p.Symbol] = $"近5M成交${p.Turnover5MUsd:0} 价差{p.SpreadBps:0.0}bps 不平衡{p.Imbalance:0.00} {p.TrendSignal15M}";
            }

            q["focus_symbol"] = new JsonObject
            {
                ["type"] = "choice",
                ["instructions"] = "本金有限不允许分仓：选择当前周期独占聚焦的单一币种（避开黑名单，选净利润期望最高者）：",
                ["criteria"] = focus,
            };
        }

        return q;
    }

    // ================= 调用 =================

    /// <summary>决策中心 7 个答案键（判断哪个容器才是真正的答案层）。</summary>
    private static readonly string[] KnownAnswerKeys =
        ["market_assessment", "trading_mode", "asset_scope", "focus_symbol", "action", "risk_appetite", "stop_and_wait"];

    private async Task<JsonObject> CallAsync(JsonObject payload, CancellationToken ct)
    {
        var url = _opts.BaseUrl.TrimEnd('/') + "/v1/decisions";
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        if (!string.IsNullOrEmpty(_opts.ApiKey))
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _opts.ApiKey);
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(5, _opts.TimeoutSeconds)));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, linked.Token);
        resp.EnsureSuccessStatusCode();
        var text = await resp.Content.ReadAsStringAsync(linked.Token);

        // 临时诊断：抓取决策中心原始响应，便于确认结构（确认后请移除）
        try
        {
            var logDir = Path.Combine(Directory.GetCurrentDirectory(), "data");
            Directory.CreateDirectory(logDir);
            File.AppendAllText(Path.Combine(logDir, "intern_raw.log"),
                $"\n==== {DateTime.UtcNow:HH:mm:ss} ====\n{text}\n");
        }
        catch
        {
            // 诊断日志失败不阻断主流程
        }

        using var doc = JsonDocument.Parse(text);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            return new JsonObject();
        }

        // 决策中心可能把答案包在 { code, msg, data } 等结构里。
        // 优先选"真正含答案键"的容器（answers/result/data/decisions），避免把只有 code/msg/data 的包装
        // 误当 answers 返回（否则 7 个答案键全部取不到，只会得到 HOLD）。
        var root = doc.RootElement;
        foreach (var container in new[] { "answers", "result", "data", "decisions" })
        {
            if (root.TryGetProperty(container, out var inner) && inner.ValueKind == JsonValueKind.Object)
            {
                var ans = ToAnswers(inner);
                if (KnownAnswerKeys.Any(ans.ContainsKey))
                {
                    return ans;
                }
            }
        }

        return ToAnswers(root);
    }

    private static JsonObject ToAnswers(JsonElement e)
    {
        var answers = new JsonObject();
        foreach (var prop in e.EnumerateObject())
        {
            answers[prop.Name] = JsonNode.Parse(prop.Value.GetRawText());
        }

        return answers;
    }

    // ================= 答案→决策映射 =================

    private AiOutputDecision Map(AiInputSnapshot i, JsonObject answers)
    {
        var fm = i.FinancialMetrics;
        var d = new AiOutputDecision
        {
            DecisionLogic = new AiDecisionLogic(),
            TargetConfigStatus = new AiTargetConfig
            {
                TradingMode = i.CurrentConfig.TradingMode,
                AssetScope = i.CurrentConfig.AssetScope,
                ActiveSymbol = i.CurrentConfig.ActiveSymbol,
            },
            ImmediateExecutionOrder = new AiExecutionOrder
            {
                ExecuteNow = true,
                Action = "HOLD",
                TargetSymbol = i.CurrentConfig.ActiveSymbol,
                TtlSeconds = 120,
            },
        };

        var market = GetString(answers, "market_assessment");
        var mode = GetString(answers, "trading_mode");
        var scope = GetString(answers, "asset_scope");
        var focus = GetString(answers, "focus_symbol");
        var action = GetString(answers, "action");
        var risk = GetAnswer(answers, "risk_appetite");
        var bailOut = GetBool(answers, "stop_and_wait");

        // 观察用：把 action / stop_and_wait 答案里的 probabilities 带出来，供前端展示与优化评估
        d.ActionProbabilities = ProbabilitiesText(answers, "action");
        d.StopWaitProbabilities = ProbabilitiesText(answers, "stop_and_wait");

        if (mode == "swing")
        {
            d.TargetConfigStatus.TradingMode = "Swing";
        }
        else if (mode == "scalping")
        {
            d.TargetConfigStatus.TradingMode = "Scalping";
        }

        if (scope == "multi_pair")
        {
            d.TargetConfigStatus.AssetScope = "Multi-Pair";
        }
        else if (scope == "single_pair")
        {
            d.TargetConfigStatus.AssetScope = "Single-Pair";
        }

        if (!string.IsNullOrEmpty(focus))
        {
            d.TargetConfigStatus.ActiveSymbol = focus;
            d.ImmediateExecutionOrder.TargetSymbol = focus;
        }

        // 止损/清场优先级最高
        if (bailOut)
        {
            d.DecisionLogic.Diagnosis = "Intern-Decision 判定环境扭转：撤销全部挂单，退守 USD 现金";
            d.DecisionLogic.ActionType = "CANCEL_ALL_AND_WAIT";
            d.ImmediateExecutionOrder.Action = "CANCEL_ALL_AND_WAIT";
            d.ImmediateExecutionOrder.ProposedOrderSizeUsd = 0m;
            return d;
        }

        var ratio = ResolveRatio(risk, Ratios());
        var brainNote = "";
        if (i.BrainDirective is { } dir && ratio > dir.MaxRiskRatio)
        {
            brainNote = $"（大脑限档：{ratio:0%}→{dir.MaxRiskRatio:0%}）";
            ratio = dir.MaxRiskRatio;
        }

        var size = Math.Round(fm.MaxAllowedOrderSize * ratio, 2, MidpointRounding.ToZero);
        // 市值不足 $1 的尘埃卖不出去（交易所最小下单额/持仓管家也忽略它），不能算"已持仓"——
        // 否则试单永远认为"已持有，去候选池挑别的币"，而候选池为空时就一直空转
        var lastPx = i.MarketSnapshot.LastPrice;
        var holding = fm.PositionBaseQty > 0m && (lastPx <= 0m || fm.PositionBaseQty * lastPx >= 1m);

        // 卖出优先：持仓已在保本价之上时，买入/观望的意图一律改为落袋（盈利只在卖出时兑现）
        if (holding && fm.PositionSellableNow && action is "buy" or "hold" or null or "")
        {
            action = "sell";
            brainNote += "（持仓已达保本价之上，优先落袋）";
        }

        // 空转兜底：空仓且长时间没有任何订单（决策中心一直 hold）= 零收益。小仓位试单买入，把循环转起来：
        // 买入后持仓管家会自动在保本价挂止盈卖单，成交即回笼现金。价差过宽（吃价差必亏）或大脑硬止血时不试。
        // 已持有一个币时也不能干等：只要还有仓位额度，就在候选池再挑一个无持仓、价差窄的币并行周转（受 MaxOpenPositions 限制）。
        var probed = false;
        var targetSymbol = d.ImmediateExecutionOrder.TargetSymbol;
        var targetHeld = holding || fm.HeldSymbols.Contains(targetSymbol, StringComparer.OrdinalIgnoreCase);
        if ((action is "hold" or null or "" || (action == "sell" && !targetHeld)) &&
            fm.IdleProbeAfterMinutes > 0m && fm.IdleMinutes >= fm.IdleProbeAfterMinutes &&
            fm.BuyRoomUsd >= 1m)
        {
            string? probeSymbol = null;
            if (!targetHeld)
            {
                var spread = SpreadBps(i, targetSymbol);
                if (i.BrainDirective?.BlocksBuys(false) != true && (spread is null || spread <= fm.IdleProbeMaxSpreadBps))
                {
                    probeSymbol = targetSymbol;
                }
            }
            else if (fm.HeldSymbols.Count < fm.MaxOpenPositions && i.BrainDirective?.BlocksBuys(false) != true)
            {
                // 注意用 BlocksBuys(false)：大脑 LLM 的"禁止新买"只约束 Intern 自己的买入意图，
                // 空转兜底是"不能一直不交易"的安全网，只被规则硬止血（回撤逼近熔断线）拦截
                probeSymbol = PickProbeCandidate(i, fm);

                // 候选池为空（行情只订阅了一个币 / 其他币价差过宽）也不能干等：在已持有的标的上继续小额加仓周转，
                // 总敞口仍受 MaxExposureRatio / 单笔上限约束，成交后卖单按整仓均价重新计算
                if (probeSymbol is null)
                {
                    var spread = SpreadBps(i, targetSymbol);
                    if (spread is null || spread <= fm.IdleProbeMaxSpreadBps)
                    {
                        probeSymbol = targetSymbol;
                    }
                }
            }

            if (probeSymbol is not null)
            {
                probed = true;
                action = "buy";
                d.ImmediateExecutionOrder.TargetSymbol = probeSymbol;
                d.ImmediateExecutionOrder.CrossSpread = true; // 吃卖一价，确保入场（排买一在上行行情里永远买不到）
                size =Math.Min(fm.BuyRoomUsd,
                    Math.Round(fm.MaxAllowedOrderSize * Math.Max(ratio, 0.5m), 2, MidpointRounding.ToZero));
                brainNote += $"（已空转 {fm.IdleMinutes:0.#} 分钟没有新买单，启动试单兜底：小仓位买入 {probeSymbol}，" +
                             "成交后由持仓管家挂保本卖单，多路并行周转）";
            }
        }

        // 仓位已满（或资金耗尽）：买入无法执行，改为有持仓则卖/否则观望
        if (action == "buy" && fm.BuyRoomUsd < 1m)
        {
            action = holding ? "sell" : "hold";
            brainNote += holding ? "（仓位已满，买入改为卖出回笼现金）" : "（无可新买额度，改为观望）";
        }
        else if (action == "buy")
        {
            size = Math.Min(size, fm.BuyRoomUsd);
        }

        // 大脑止血：禁止新开买单时，买入一律降级为观望
        if (action == "buy" && !probed && i.BrainDirective is { } stopBuy && stopBuy.BlocksBuys(holding))
        {
            d.DecisionLogic.Diagnosis = $"大脑指引（{stopBuy.Posture}）禁止新开买单，Intern-Decision 的买入意图降级为观望";
            d.DecisionLogic.ActionType = "HOLD";
            d.ImmediateExecutionOrder.Action = "HOLD";
            d.ImmediateExecutionOrder.ExecuteNow = false;
            return d;
        }

        switch (action)
        {
            case "buy":
                d.DecisionLogic.Diagnosis =
                    $"Intern-Decision 决策：{market} → 挂限价买单，额度 ${size:0.00}（风控上限 ${fm.MaxAllowedOrderSize:0.00} × {ratio:0%}）{brainNote}";
                d.DecisionLogic.ActionType = "PLACE_LIMIT_BUY";
                d.ImmediateExecutionOrder.Action = "PLACE_LIMIT_BUY";
                d.ImmediateExecutionOrder.Side = "BUY";
                d.ImmediateExecutionOrder.ProposedOrderSizeUsd = size;
                break;

            case "sell":
                d.DecisionLogic.Diagnosis =
                    $"Intern-Decision 决策：{market} → 挂限价卖单，整仓落袋（价格受盈利护栏约束：不低于保本价，止损线除外）{brainNote}";
                d.DecisionLogic.ActionType = "PLACE_LIMIT_SELL";
                d.ImmediateExecutionOrder.Action = "PLACE_LIMIT_SELL";
                d.ImmediateExecutionOrder.Side = "SELL";
                d.ImmediateExecutionOrder.ProposedOrderSizeUsd = 0m; // 0 = 整仓（卖单不受买单额度限制，决策中心按持仓市值折算）
                break;

            case "cancel_all":
                d.DecisionLogic.Diagnosis = "Intern-Decision 决策：撤销全部残余挂单，锁定 USD 现金";
                d.DecisionLogic.ActionType = "CANCEL_ALL_AND_WAIT";
                d.ImmediateExecutionOrder.Action = "CANCEL_ALL_AND_WAIT";
                d.ImmediateExecutionOrder.ProposedOrderSizeUsd = 0m;
                break;

            default:
                d.DecisionLogic.Diagnosis = $"Intern-Decision 决策：{market} → 观望等待更优期望值窗口{brainNote}";
                d.DecisionLogic.ActionType = "HOLD";
                d.ImmediateExecutionOrder.Action = "HOLD";
                d.ImmediateExecutionOrder.ExecuteNow = false;
                break;
        }

        return d;
    }

    /// <summary>候选池里挑试单标的：非黑名单、无持仓、价差 ≤ 上限，5 分钟成交额最大者；没有则 null。</summary>
    private static string? PickProbeCandidate(AiInputSnapshot i, AiFinancialMetrics fm)
    {
        if (i.PairSnapshots is not { Count: > 0 })
        {
            return null;
        }

        return i.PairSnapshots
            .Where(p => p.LastPrice > 0m && p.SpreadBps <= fm.IdleProbeMaxSpreadBps)
            .Where(p => !fm.HeldSymbols.Contains(p.Symbol, StringComparer.OrdinalIgnoreCase))
            .Where(p => !i.CurrentConfig.Blacklist.Any(b =>
                p.Symbol.StartsWith(b, StringComparison.OrdinalIgnoreCase) && (p.Symbol.Length == b.Length || p.Symbol[b.Length] == '-')))
            .OrderByDescending(p => p.Turnover5MUsd)
            .Select(p => p.Symbol)
            .FirstOrDefault();
    }

    /// <summary>目标标的的买卖价差（bps）：优先取候选池快照，其次聚焦标的盘口；取不到返回 null（不据此拦截）。</summary>
    private static decimal? SpreadBps(AiInputSnapshot i, string symbol)
    {
        var pair = i.PairSnapshots?.FirstOrDefault(p => p.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase));
        if (pair is not null)
        {
            return pair.SpreadBps;
        }

        if (i.MarketSnapshot.ActiveSymbolOrderbook is { Bid1To5.Count: > 0, Ask1To5.Count: > 0 } book &&
            symbol.Equals(i.CurrentConfig.ActiveSymbol, StringComparison.OrdinalIgnoreCase))
        {
            var bid = book.Bid1To5[0][0];
            var ask = book.Ask1To5[0][0];
            var mid = (bid + ask) / 2m;
            return mid > 0m ? (ask - bid) / mid * 10000m : null;
        }

        return null;
    }

    /// <summary>风险档位候选文案（跟随 InternDecision:RiskAppetiteRatios：档位命名 + 实际占比，图例不再写死 25/50/100%）。</summary>
    private JsonArray RiskAppetiteCriteria()
    {
        var ratios = Ratios();
        var names = ratios.Length switch
        {
            <= 3 => new[] { "保守", "中性", "激进" },
            4 => new[] { "保守", "偏保守", "偏激进", "激进" },
            5 => new[] { "保守", "偏保守", "中性", "偏激进", "激进" },
            _ => Enumerable.Range(1, ratios.Length).Select(i => $"档位{i}").ToArray(),
        };

        var arr = new JsonArray();
        for (var i = 0; i < ratios.Length; i++)
        {
            var name = i < names.Length ? names[i] : $"档位{i + 1}";
            arr.Add($"{name}（占用上限 {ratios[i]:0%}）");
        }

        return arr;
    }

    /// <summary>风险档位比例（读 InternDecision:RiskAppetiteRatios 配置；缺省 [0.25, 0.5, 1.0]）。</summary>
    private decimal[] Ratios() =>
        _opts.RiskAppetiteRatios is { Length: > 0 } custom
            ? custom.Select(x => (decimal)x).ToArray()
            : [0.25m, 0.5m, 1.0m];

    private decimal ResolveRatio(JsonNode? risk, decimal[] ratios)
    {
        var mid = ratios.Length >= 2 ? ratios[ratios.Length / 2] : ratios[0];
        switch (risk)
        {
            case null:
                return mid;
            case JsonObject obj:
                return ResolveRatioObject(obj, ratios, mid);
            case JsonValue v:
            {
                if (v.TryGetValue<int>(out var idx))
                {
                    return ratios[Math.Clamp(idx - 1, 0, ratios.Length - 1)];
                }

                if (v.TryGetValue<double>(out var dd))
                {
                    return Math.Clamp((decimal)dd, 0m, 1m);
                }

                var s = v.GetValue<string>();
                if (string.IsNullOrWhiteSpace(s))
                {
                    return mid;
                }

                // 回显选项文案（动态图例）与关键词均需可解析：
                //   1) "占用上限 40%" → 找最接近档位
                //   2) "档位3" / "3" / "3（占用上限 40%）" → 按索引
                var pct = System.Text.RegularExpressions.Regex.Match(s, @"占用上限\s*(\d+(?:\.\d+)?)\s*%");
                if (pct.Success && decimal.TryParse(pct.Groups[1].Value, System.Globalization.NumberStyles.Number,
                        System.Globalization.CultureInfo.InvariantCulture, out var p))
                {
                    return ratios.OrderBy(r => Math.Abs(r * 100m - p)).First();
                }

                var num = System.Text.RegularExpressions.Regex.Match(s, @"\d+");
                if (num.Success && int.TryParse(num.Groups[0].Value, out var k) && k is >= 1 and <= 999)
                {
                    return ratios[Math.Clamp(k - 1, 0, ratios.Length - 1)];
                }

                return s switch
                {
                    "保守" or "Low" or "low" or "conservative" => ratios[0],
                    "激进" or "High" or "high" or "aggressive" => ratios[^1],
                    _ => mid,
                };
            }

            default:
                return mid;
        }
    }

    /// <summary>
    /// 格式化风险档位回答（看板展示用）：
    /// 不再回显决策中心原始 9 档 JSON，而是先剔除内置默认档（25/50% 等与配置不一致的候选），
    /// 显示"选中档位 → 生效比例"，让"内置档已移除"在界面上可见。
    /// </summary>
    private string FormatRiskAppetite(JsonObject answers)
    {
        if (GetAnswer(answers, "risk_appetite") is JsonObject obj && SelectConfigTier(obj, out _, out var ratio, out var text))
        {
            return $"风险档位={text} → 生效比例 {ratio:0%}（决策中心内置档已剔除）";
        }

        return AnswerText(answers, "risk_appetite", "风险档位");
    }

    /// <summary>
    /// 结构化档位回答（决策中心真实格式）：{ decision, legend, probabilities, confidence, score }。
    /// 生效顺序：
    ///   1) 【移除决策中心自带默认档】仅保留占用上限与配置档位（±0.5%）一致的候选，
    ///      在幸存候选上按 probabilities 重新取最大概率选档 —— 内置 25/50% 等档位从此不再参与；
    ///   2) 无 probabilities 时回退：decision 索引 → legend[index] 的"占用上限 X%" → 就近匹配配置档位；
    ///   3) 再回退：嵌套 value/answer/choice/selected、score 期望档位。
    /// </summary>
    private decimal ResolveRatioObject(JsonObject obj, decimal[] ratios, decimal mid)
    {
        // ---- 1) 按配置档位过滤 + 概率重选（去掉决策中心内置默认档） ----
        if (SelectConfigTier(obj, out _, out var ratio, out _))
        {
            return ratio;
        }

        // ---- 2) 回退：decision 索引 → legend 百分比 → 就近档位 ----
        if (obj.TryGetPropertyValue("decision", out var dec) && DecodeInt(dec) is int di)
        {
            if (obj.TryGetPropertyValue("legend", out var lg2) && lg2 is JsonObject lo2 &&
                lo2.TryGetPropertyValue(di.ToString(), out var entry) &&
                entry?.GetValue<string>() is { Length: > 0 } text)
            {
                var m = System.Text.RegularExpressions.Regex.Match(text, @"占用上限\s*(\d+(?:\.\d+)?)\s*%");
                if (m.Success && decimal.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Number,
                        System.Globalization.CultureInfo.InvariantCulture, out var p))
                {
                    return ratios.OrderBy(r => Math.Abs(r * 100m - p)).First();
                }
            }

            return ratios[Math.Clamp(di, 0, ratios.Length - 1)];
        }

        // ---- 3) 回退：嵌套 value/answer/choice/selected、score 期望档位 ----
        foreach (var sub in new[] { "value", "answer", "choice", "selected" })
        {
            if (obj.TryGetPropertyValue(sub, out var sv) && sv is not null)
            {
                return ResolveRatio(sv, ratios);
            }
        }

        if (obj.TryGetPropertyValue("score", out var sc) &&
            sc is System.Text.Json.Nodes.JsonValue sjv &&
            sjv.TryGetValue<double>(out var s) && s is >= 0 and <= 64)
        {
            return ratios[Math.Clamp((int)Math.Round(s), 0, ratios.Length - 1)];
        }

        return mid;
    }

    /// <summary>
    /// 在回答对象中按"配置档位过滤 + 概率重选"选出生效档（剔除决策中心内置默认档）。
    /// </summary>
    private bool SelectConfigTier(JsonObject obj, out int chosenIdx, out decimal ratio, out string? chosenText)
    {
        chosenIdx = -1;
        ratio = 0m;
        chosenText = null;
        var ratios = Ratios();

        if (!obj.TryGetPropertyValue("probabilities", out var probs) || probs is not JsonObject pObj ||
            !obj.TryGetPropertyValue("legend", out var lg) || lg is not JsonObject lo)
        {
            return false;
        }

        var survivors = new List<(int Idx, decimal Ratio)>();
        foreach (var kv in lo)
        {
            if (!int.TryParse(kv.Key, out var idx) ||
                kv.Value?.GetValue<string>() is not { Length: > 0 } txt)
            {
                continue;
            }

            var m = System.Text.RegularExpressions.Regex.Match(txt, @"占用上限\s*(\d+(?:\.\d+)?)\s*%");
            if (!m.Success ||
                !decimal.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Number,
                    System.Globalization.CultureInfo.InvariantCulture, out var pct))
            {
                continue;
            }

            var best = ratios.OrderBy(r => Math.Abs(r * 100m - pct)).First();
            if (Math.Abs(best * 100m - pct) <= 0.5m)
            {
                survivors.Add((idx, best));
            }
        }

        if (survivors.Count == 0)
        {
            return false;
        }

        (int Idx, decimal Ratio) chosen = (survivors[0].Idx, survivors[0].Ratio);
        var bestP = -1d;
        foreach (var (idx, r) in survivors)
        {
            var p = pObj.TryGetPropertyValue(idx.ToString(), out var pv) &&
                    pv is System.Text.Json.Nodes.JsonValue pjv &&
                    pjv.TryGetValue<double>(out var pp)
                ? pp
                : 0d;
            if (p > bestP)
            {
                bestP = p;
                chosen = (idx, r);
            }
        }

        chosenIdx = chosen.Idx;
        ratio = chosen.Ratio;
        chosenText = lo.TryGetPropertyValue(chosen.Idx.ToString(), out var textNode)
            ? textNode?.GetValue<string>()
            : null;
        return true;
    }

    private static int? DecodeInt(JsonNode? n)
    {
        if (n is not JsonValue v)
        {
            return null;
        }

        if (v.TryGetValue<int>(out var i))
        {
            return i;
        }

        if (v.TryGetValue<double>(out var d) && d == Math.Floor(d))
        {
            return (int)d;
        }

        if (v.TryGetValue<string>(out var s) && int.TryParse(s, out var k))
        {
            return k;
        }

        return null;
    }

    // ================= 答案提取（宽容解析） =================

    private static JsonNode? GetAnswer(JsonObject answers, string key)
    {
        if (answers.TryGetPropertyValue(key, out var v))
        {
            return v;
        }

        // 可能嵌套在 {key:{value/answer/choice}} 等形式
        if (answers.TryGetPropertyValue(key, out var node) && node is JsonObject obj)
        {
            foreach (var sub in new[] { "value", "answer", "choice", "score", "selected" })
            {
                if (obj.TryGetPropertyValue(sub, out var sv))
                {
                    return sv;
                }
            }
        }

        return null;
    }

    private static string? GetString(JsonObject answers, string key)
    {
        var v = GetAnswer(answers, key);
        if (v is JsonValue jv && jv.TryGetValue<string>(out var s))
        {
            return s;
        }

        // 结构化答案（{ decision / value / choice / selected : "..." }）：取其中的字符串值
        if (v is JsonObject obj)
        {
            foreach (var sub in new[] { "decision", "value", "choice", "answer", "selected" })
            {
                if (obj.TryGetPropertyValue(sub, out var sv) && sv is JsonValue svj && svj.TryGetValue<string>(out var ss))
                {
                    return ss;
                }
            }
        }

        return v?.ToJsonString().Trim('"');
    }

    /// <summary>从某题答案里提取 probabilities，并格式化为紧凑文本（如 "buy 0.60 | hold 0.30"）。</summary>
    private static string? ProbabilitiesText(JsonObject answers, string key)
    {
        if (GetAnswer(answers, key) is not JsonObject obj)
        {
            return null;
        }

        if (!TryGetProbabilities(obj, out var probs))
        {
            return null;
        }

        return string.Join(" | ", probs.Select(p =>
        {
            var val = p.Value is JsonValue v && v.TryGetValue<double>(out var d) ? d : 0;
            return $"{p.Key} {val:0.00}";
        }));
    }

    /// <summary>在答案对象（或其嵌套 value/answer/choice/score/selected/decision）里找 probabilities。</summary>
    private static bool TryGetProbabilities(JsonObject obj, out JsonObject probs)
    {
        probs = null!;
        if (obj.TryGetPropertyValue("probabilities", out var p) && p is JsonObject po)
        {
            probs = po;
            return true;
        }

        foreach (var sub in new[] { "value", "answer", "choice", "score", "selected", "decision" })
        {
            if (obj.TryGetPropertyValue(sub, out var sv) && sv is JsonObject so
                && so.TryGetPropertyValue("probabilities", out var sp) && sp is JsonObject spo)
            {
                probs = spo;
                return true;
            }
        }

        return false;
    }

    private static bool GetBool(JsonObject answers, string key)
    {
        var v = GetAnswer(answers, key);
        switch (v)
        {
            case JsonValue jv when jv.TryGetValue<bool>(out var b):
                return b;
            case JsonValue jv when jv.TryGetValue<int>(out var n):
                return n != 0;
            case JsonValue jv when jv.TryGetValue<string>(out var s):
                return s is "true" or "yes" or "是" or "1" or "bail_out";
            default:
                return false;
        }
    }

    private static string? AnswerText(JsonObject answers, string key, string label)
    {
        var v = GetAnswer(answers, key);
        if (v is null)
        {
            return null;
        }

        if (v is JsonValue jv && jv.TryGetValue<string>(out var s))
        {
            return $"{label}={s}";
        }

        return $"{label}={v.ToJsonString()}";
    }

    private static string FormatLevels(List<List<decimal>> levels) =>
        string.Join(" / ", levels.Select(l => l.Count > 1 ? $"{l[0]:0.######}×{l[1]:0.####}" : string.Empty));

    private static string BuildNarrative(AiInputSnapshot i, JsonObject questions) =>
        $"""
        触发源: {i.TriggerReason} | 权益 ${i.FinancialMetrics.CurrentTotalEquity:0.00} / 初始 ${i.FinancialMetrics.InitialCapital:0.00}
        —— Intern-Decision 结构化决策 —— 将盘面/账户状态拆解为 {questions.Count} 个问题：
        {string.Join('\n', questions.Select(kv => $"· {kv.Key}: {Describe(kv.Value)}"))}
        —— 等待决策中心返回答案并映射为标准动作 ——
        """;

    private static string Describe(JsonNode? node)
    {
        if (node is not JsonObject o)
        {
            return node?.ToJsonString() ?? "";
        }

        var type = o.TryGetPropertyValue("type", out var t) ? t?.GetValue<string>() : "";
        var instr = o.TryGetPropertyValue("instructions", out var ins) ? ins?.GetValue<string>() : "";
        return $"[{type}] {instr}";
    }
}
