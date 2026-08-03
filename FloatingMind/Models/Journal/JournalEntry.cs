namespace FloatingMind.Models.Journal;

/// <summary>
/// 8. Journal系统 —— 所有不可逆事件记录
/// </summary>
public class JournalEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..16];
    public string Event { get; set; } = string.Empty;     // FileWrite/CommandExec/Decision...
    public string Agent { get; set; } = string.Empty;
    public string Target { get; set; } = string.Empty;
    public string Details { get; set; } = string.Empty;
    public string BeforeState { get; set; } = string.Empty;  // 修改前
    public string AfterState { get; set; } = string.Empty;   // 修改后
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public bool IsReversible { get; set; } = true;
    public bool RolledBack { get; set; } = false;

    public static JournalEntry FileWrite(string agent, string filePath, string before, string after)
        => new()
        {
            Event = "FileWrite",
            Agent = agent,
            Target = filePath,
            BeforeState = before,
            AfterState = after,
            Details = $"Modified: {filePath}"
        };

    public static JournalEntry CommandExec(string agent, string command, string result)
        => new()
        {
            Event = "CommandExec",
            Agent = agent,
            Target = command,
            Details = result,
            IsReversible = false
        };

    public static JournalEntry DecisionMade(string agent, string topic, string choice, string reason)
        => new()
        {
            Event = "DecisionMade",
            Agent = agent,
            Target = topic,
            Details = $"Choice: {choice}, Reason: {reason}",
            IsReversible = false
        };

    public static JournalEntry AgentAction(string agent, string action, string detail)
        => new()
        {
            Event = "AgentAction",
            Agent = agent,
            Target = action,
            Details = detail
        };

    public static JournalEntry ValidationResult(string validator, string result, string detail)
        => new()
        {
            Event = "ValidationResult",
            Agent = validator,
            Target = result,
            Details = detail,
            IsReversible = false
        };

    public static JournalEntry LockAcquire(string agent, string resource)
        => new()
        {
            Event = "LockAcquire",
            Agent = agent,
            Target = resource,
            Details = $"Lock acquired on {resource}"
        };

    public static JournalEntry LockRelease(string agent, string resource)
        => new()
        {
            Event = "LockRelease",
            Agent = agent,
            Target = resource,
            Details = $"Lock released on {resource}"
        };
}
