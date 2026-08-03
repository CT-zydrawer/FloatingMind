namespace FloatingMind.Models.Agent;

/// <summary>
/// Agent发现阶段的输出
/// </summary>
public class DiscoveryOutput
{
    public List<string> NeedModify { get; set; } = new();
    public List<string> Dependencies { get; set; } = new();
    public List<string> Observations { get; set; } = new();
    public bool Success { get; set; }
    public string Error { get; set; } = string.Empty;

    public static DiscoveryOutput FromResult(List<string> files, List<string> deps) => new()
    {
        NeedModify = files,
        Dependencies = deps,
        Success = true
    };

    public static DiscoveryOutput Failed(string error) => new()
    {
        Success = false,
        Error = error
    };
}

/// <summary>
/// Agent执行结果
/// </summary>
public class AgentResult
{
    public string AgentName { get; set; } = string.Empty;
    public string NodeId { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string Output { get; set; } = string.Empty;
    public string Error { get; set; } = string.Empty;
    public List<string> ModifiedFiles { get; set; } = new();
    public List<string> CommandsExecuted { get; set; } = new();
    public TimeSpan Duration { get; set; }

    public static AgentResult Ok(string agent, string node, string output, List<string>? files = null) => new()
    {
        AgentName = agent,
        NodeId = node,
        Success = true,
        Output = output,
        ModifiedFiles = files ?? new()
    };

    public static AgentResult Fail(string agent, string node, string error) => new()
    {
        AgentName = agent,
        NodeId = node,
        Success = false,
        Error = error
    };
}
