using Dvc.Core.Events;
using Microsoft.AspNetCore.SignalR;

namespace Dvc.Web.Hubs;

/// <summary>
/// 内网极速 SignalR 通道：把引擎事件总线原样推送到所有连接端（前端看板/外部客户端）。
/// 负载为 JSON 字符串，与 EngineEventBus 同构。Blazor 组件走内存订阅，Hub 面向
/// 跨进程/跨标签页客户端（JS 客户端示例见 wwwroot/js/site.js）。
/// </summary>
public sealed class TradingHub : Hub
{
    public override Task OnConnectedAsync()
    {
        _ = Clients.Caller.SendAsync("OnEngineEvent", "connected", "{}");
        return base.OnConnectedAsync();
    }
}
