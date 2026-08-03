using FloatingMind.Agents;
using FloatingMind.Interfaces;
using FloatingMind.Models.Agent;
using FloatingMind.Models.Blackboard;
using FloatingMind.Models.Config;
using FloatingMind.Models.Lock;
using FloatingMind.Models.Workflow;
using System.Collections.Concurrent;

namespace FloatingMind.Services;

/// <summary>
/// Agent调度器 —— 负责Agent注册/查找/并发控制/资源锁
/// </summary>
public class AgentScheduler
{
    private readonly Dictionary<string, IAgent> _agents = new();
    private readonly BlackboardSystem _blackboard;
    private readonly JournalSystem _journal;
    private readonly EventBus _eventBus;
    private readonly List<ResourceLock> _locks = new();
    private readonly SemaphoreSlim _concurrencyLimit;
    private readonly object _lockObj = new();

    public event Action<string, string>? OnAgentStatusChanged;

    public AgentScheduler(BlackboardSystem blackboard, JournalSystem journal,
        EventBus eventBus, int maxConcurrency = 2)
    {
        _blackboard = blackboard;
        _journal = journal;
        _eventBus = eventBus;
        _concurrencyLimit = new SemaphoreSlim(maxConcurrency, maxConcurrency);
    }

    public void Register(IAgent agent) => _agents[agent.Name] = agent;

    public IAgent? GetAgent(string name) => _agents.GetValueOrDefault(name);

    public IReadOnlyList<string> RegisteredAgents => _agents.Keys.ToList().AsReadOnly();

    /// <summary>
    /// 在Dependency Graph确定后调度执行
    /// </summary>
    public async Task<AgentResult> ExecuteNodeAsync(WorkflowDef workflow, WorkflowNode node, string taskId)
    {
        var agent = GetAgent(node.AgentType);
        if (agent == null)
            return AgentResult.Fail("Unknown", node.Id, $"Agent未注册: {node.AgentType}");

        // 并发控制
        await _concurrencyLimit.WaitAsync();
        try
        {
            var startTime = DateTime.Now;
            OnAgentStatusChanged?.Invoke(agent.Name, "Running");
            _journal.LogAgentAction(agent.Name, "Start", $"Node: {node.Label}");

            var context = new Dictionary<string, string>
            {
                ["taskId"] = taskId,
                ["nodeId"] = node.Id,
                ["label"] = node.Label,
                ["agentType"] = node.AgentType
            };
            foreach (var kv in node.Parameters) context[kv.Key] = kv.Value;

            // 1. Discovery阶段 (只读)
            var discovery = await agent.DiscoveryAsync(node, context);
            if (!discovery.Success)
                return AgentResult.Fail(agent.Name, node.Id, discovery.Error);

            // 记录发现
            foreach (var obs in discovery.Observations)
                _blackboard.AddObservation(taskId, obs, agent.Name);

            // 2. 获取资源锁
            foreach (var file in discovery.NeedModify)
            {
                if (!TryAcquireLock(file, agent.Name))
                    return AgentResult.Fail(agent.Name, node.Id, $"无法获取资源锁: {file}");
            }

            // 3. 执行阶段
            var blackboardEntries = _blackboard.GetAll(taskId);
            var result = await agent.ExecuteAsync(node, context, blackboardEntries);
            result.Duration = DateTime.Now - startTime;

            // 4. 释放锁
            foreach (var file in discovery.NeedModify)
                ReleaseLock(file, agent.Name);

            OnAgentStatusChanged?.Invoke(agent.Name, result.Success ? "Completed" : "Failed");
            _journal.LogAgentAction(agent.Name, "Complete",
                $"Success: {result.Success}, Duration: {result.Duration.TotalSeconds:F1}s");

            _eventBus.Publish("AgentResult", result);
            return result;
        }
        catch (Exception ex)
        {
            _journal.LogAgentAction(agent.Name, "Error", ex.Message);
            return AgentResult.Fail(agent.Name, node.Id, ex.Message);
        }
        finally
        {
            _concurrencyLimit.Release();
        }
    }

    // === Resource Lock ===
    private bool TryAcquireLock(string resource, string owner)
    {
        lock (_lockObj)
        {
            // 清理过期锁
            _locks.RemoveAll(l => l.IsExpired);

            // 检查是否已被锁定
            if (_locks.Any(l => l.Resource == resource && !l.IsExpired))
                return false;

            _locks.Add(new ResourceLock
            {
                Resource = resource,
                Owner = owner,
                ExpireSeconds = 300
            });

            _journal.LogLock(owner, resource, true);
            return true;
        }
    }

    private void ReleaseLock(string resource, string owner)
    {
        lock (_lockObj)
        {
            _locks.RemoveAll(l => l.Resource == resource && l.Owner == owner);
            _journal.LogLock(owner, resource, false);
        }
    }

    public IReadOnlyList<ResourceLock> ActiveLocks
    {
        get { lock (_lockObj) { return _locks.Where(l => !l.IsExpired).ToList().AsReadOnly(); } }
    }
}
