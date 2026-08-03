using System.Collections.Concurrent;

namespace FloatingMind.Services;

/// <summary>
/// 内部Event Bus —— Agent间通信 + 事件驱动
/// </summary>
public class EventBus
{
    private readonly ConcurrentDictionary<string, List<Func<object, Task>>> _subscribers = new();
    private readonly List<EventLog> _history = new();

    // 系统事件
    public event Action<string, object>? OnEvent;

    public void Subscribe(string eventType, Func<object, Task> handler)
    {
        _subscribers.AddOrUpdate(eventType,
            _ => new List<Func<object, Task>> { handler },
            (_, list) => { list.Add(handler); return list; });
    }

    public async Task PublishAsync(string eventType, object data)
    {
        _history.Add(new EventLog(eventType, data, DateTime.Now));
        OnEvent?.Invoke(eventType, data);

        if (_subscribers.TryGetValue(eventType, out var handlers))
        {
            foreach (var handler in handlers)
                await handler(data);
        }
    }

    public void Publish(string eventType, object data)
    {
        _history.Add(new EventLog(eventType, data, DateTime.Now));
        OnEvent?.Invoke(eventType, data);
    }

    public IReadOnlyList<EventLog> History => _history.AsReadOnly();

    public record EventLog(string EventType, object Data, DateTime Timestamp);
}
