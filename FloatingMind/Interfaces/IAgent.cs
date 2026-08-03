using FloatingMind.Models.Agent;
using FloatingMind.Models.Blackboard;
using FloatingMind.Models.Workflow;

namespace FloatingMind.Interfaces;

/// <summary>
/// Agent接口 —— 所有专业Agent的基类
/// </summary>
public interface IAgent
{
    string Name { get; }
    string Description { get; }

    /// <summary>
    /// 6.1 Discovery阶段 —— 只读探索,不能修改
    /// </summary>
    Task<DiscoveryOutput> DiscoveryAsync(WorkflowNode node, Dictionary<string, string> context);

    /// <summary>
    /// 执行阶段 —— 实际修改操作
    /// </summary>
    Task<AgentResult> ExecuteAsync(WorkflowNode node, Dictionary<string, string> context,
        IEnumerable<BlackboardEntry> blackboard);

    /// <summary>
    /// 回滚操作
    /// </summary>
    Task<bool> RollbackAsync(string nodeId);
}

/// <summary>
/// Validator接口 —— 检查体系(不是Agent)
/// </summary>
public interface IValidator
{
    string Name { get; }
    Task<(bool Passed, string Reason, List<string> Issues)> ValidateAsync(
        WorkflowNode node, AgentResult result, List<BlackboardEntry> blackboard);
}

/// <summary>
/// Memory接口
/// </summary>
public interface IMemoryStore<T>
{
    Task<T?> LoadAsync(string id);
    Task SaveAsync(string id, T data);
    Task DeleteAsync(string id);
    Task<List<T>> ListAsync();
}
