using System.Threading.Channels;
using Dvc.Core;
using Dvc.Core.Events;

namespace Dvc.Engine;

/// <summary>
/// 全系统事件总线（组播）。每个消费者通过 CreateChannel() 领取自己的独立管道，
/// Publish 将每条事件复制投递给所有管道——看板(Hub) 与 内存仓库(UiEventStore) 各自拿到全量流。
///
/// ⚠️ 历史教训：此前是"单 Channel 多读者"，.NET Channel 每条事件只被一个读者取走，
/// 两个读者（HubForwarder 与 UiEventStore）竞抢，思考链逐字块被随机瓜分，
/// 导致看板打字机文字残缺乱序（如"等待决策中心…"显示成"等待策心并射标"）。
/// 现改为组播后每个消费者都收到完整事件序列。
/// </summary>
public sealed class EngineEventBus
{
    private readonly object _lock = new();
    private readonly List<Channel<EngineEvent>> _channels = [];

    /// <summary>为单个消费者创建一个独立事件管道（每个消费者必须各领一条，不得共用）。</summary>
    public Channel<EngineEvent> CreateChannel()
    {
        var ch = Channel.CreateUnbounded<EngineEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });
        lock (_lock)
        {
            _channels.Add(ch);
        }

        return ch;
    }

    public void Publish(EngineEvent ev)
    {
        Channel<EngineEvent>[] snaps;
        lock (_lock)
        {
            snaps = _channels.ToArray();
        }

        foreach (var ch in snaps)
        {
            ch.Writer.TryWrite(ev);
        }
    }

    public void Publish(EngineEventKind kind, string payloadJson, string? sessionId = null) =>
        Publish(new EngineEvent(kind, payloadJson, DateTime.UtcNow, sessionId));
}
