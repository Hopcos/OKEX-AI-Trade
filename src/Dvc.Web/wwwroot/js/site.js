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

    // Blazor 场景：等 blazor.server.js 就绪后连接（_Host 加载顺序在前）
    if (document.readyState === "complete" || document.readyState === "interactive") {
        setTimeout(dvc.connectHub, 1500);
    } else {
        window.addEventListener("load", function () { setTimeout(dvc.connectHub, 1500); });
    }
})();
