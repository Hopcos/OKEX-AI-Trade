// ============================================================
// OKEX-AI 自动交易 · 前端 JS
// 1) 多皮肤动态切换（无刷新）：data-theme + localStorage 记忆
// 2) SignalR 直连演示：引擎事件经 /hubs/trading 推送到任意客户端
// ============================================================
(function () {
    "use strict";

    // ---------- 皮肤 ----------
    var saved = null;
    try { saved = localStorage.getItem("dvc-theme"); } catch (e) { /* ignore */ }
    if (saved) {
        document.documentElement.setAttribute("data-theme", saved);
    }

    window.dvc = window.dvc || {};

    dvc.applyTheme = function (theme) {
        document.documentElement.setAttribute("data-theme", theme);
        try { localStorage.setItem("dvc-theme", theme); } catch (e) { /* ignore */ }
    };

    // ---------- SignalR 直连 ----------
    var badge = document.getElementById("signalr-status");

    function setBadge(text, ok) {
        if (!badge) return;
        badge.textContent = text;
        badge.classList.toggle("ok", ok === true);
        badge.classList.toggle("err", ok === false);
    }

    dvc.connectHub = function () {
        if (!window.signalR) {
            setBadge("SignalR 直连: 库未加载", false);
            return;
        }

        var conn = new signalR.HubConnectionBuilder()
            .withUrl("/hubs/trading")
            .withAutomaticReconnect([1000, 3000, 5000])
            .build();

        conn.on("OnEngineEvent", function (kind, payload) {
            setBadge("SignalR 直连: " + kind + " " + new Date().toLocaleTimeString(), true);
            // 供控制台观察：console.debug(kind, payload);
        });

        conn.start().then(function () {
            setBadge("SignalR 直连: 已连接", true);
        }).catch(function () {
            setBadge("SignalR 直连: 失败", false);
        });
    };

    // ---------- 复利阶梯图：本地悬停（纯前端，不经 SignalR/Blazor 往返） ----------
    // 数据由 StepLineChart.razor 写入 .chart-wrap[data-points]；这里只做 DOM 查表与定位。
    function chartHover(wrap, ev) {
        var raw = wrap.getAttribute("data-points");
        if (!raw) return;
        var pts;
        try { pts = JSON.parse(raw); } catch (e) { return; }
        if (!pts || pts.length < 2) return;

        var svg = wrap.querySelector("svg");
        var rect = svg.getBoundingClientRect();
        var localX = ev.clientX - rect.left;
        var localY = ev.clientY - rect.top;
        var ratio = Math.min(1, Math.max(0, localX / rect.width));
        var idx = Math.round(ratio * (pts.length - 1));
        var p = pts[idx];
        var vx = p.x / 600 * rect.width;          // 映射回像素坐标

        var q = function (k) { return wrap.querySelector('[data-hover="' + k + '"]'); };
        var line = q("line"), eq = q("eq"), mx = q("mx");
        var tip = q("tip");

        line.setAttribute("x1", p.x); line.setAttribute("x2", p.x);
        line.style.display = "";
        eq.setAttribute("cx", p.x); eq.setAttribute("cy", p.ey); eq.style.display = "";
        mx.setAttribute("cx", p.x); mx.setAttribute("cy", p.my); mx.style.display = "";

        q("time").textContent = p.t;
        q("eqv").textContent = p.eq;
        q("mxv").textContent = p.mx;
        q("avail").textContent = p.av;

        // 提示框：跟随鼠标，靠右超出翻到左侧，纵向限制在图表区内
        var tw = tip.offsetWidth || 170, th = tip.offsetHeight || 92;
        var left = localX + 14;
        if (left + tw > rect.width) left = localX - tw - 14;
        left = Math.max(0, left);
        var top = localY + 10;
        if (top + th > rect.height) top = rect.height - th;
        top = Math.max(0, top);
        tip.style.left = left + "px";
        tip.style.top = top + "px";
        tip.style.display = "";
    }

    function chartLeave(wrap) {
        ["line", "eq", "mx", "tip"].forEach(function (k) {
            var el = wrap.querySelector('[data-hover="' + k + '"]');
            if (el) el.style.display = "none";
        });
    }

    // 事件委托：Blazor 重渲染后依然有效（监听挂在 document 上）
    document.addEventListener("mousemove", function (ev) {
        var wrap = ev.target && ev.target.closest ? ev.target.closest(".chart-wrap") : null;
        if (wrap && wrap.getAttribute("data-points")) chartHover(wrap, ev);
    }, { passive: true });
    document.addEventListener("mouseleave", function (ev) {
        var wrap = ev.target && ev.target.closest ? ev.target.closest(".chart-wrap") : null;
        if (wrap) chartLeave(wrap);
    }, true);

    // Blazor 场景：等 blazor.server.js 就绪后连接（_Host 加载顺序在前）
    if (document.readyState === "complete" || document.readyState === "interactive") {
        setTimeout(dvc.connectHub, 1500);
    } else {
        window.addEventListener("load", function () { setTimeout(dvc.connectHub, 1500); });
    }
})();
