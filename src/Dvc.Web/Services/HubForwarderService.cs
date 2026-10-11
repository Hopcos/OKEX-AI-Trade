using System.Threading.Channels;
using Dvc.Core.Events;
using Dvc.Engine;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Dvc.Web.Hubs;

namespace Dvc.Web.Services;

/// <summary>引擎事件总线 → SignalR Hub（对外推送通道）。独立管道，完整接收每条事件。</summary>
public sealed class HubForwarderService : BackgroundService
{
    private readonly Channel<EngineEvent> _channel;
    private readonly IHubContext<TradingHub> _hub;

    public HubForwarderService(EngineEventBus bus, IHubContext<TradingHub> hub)
    {
        _channel = bus.CreateChannel();
        _hub = hub;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var ev in _channel.Reader.ReadAllAsync(stoppingToken))
            {
                await _hub.Clients.All.SendAsync("OnEngineEvent", ev.Kind.ToString(), ev.PayloadJson, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停机
        }
    }
}
