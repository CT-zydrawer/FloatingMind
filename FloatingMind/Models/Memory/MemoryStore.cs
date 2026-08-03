using Newtonsoft.Json;

namespace FloatingMind.Models.Memory;

/// <summary>
/// 4.1 Project Memory —— 长期项目知识
/// </summary>
public class ProjectMemory
{
    public string Project { get; set; } = string.Empty;
    public string Framework { get; set; } = string.Empty;
    public string Language { get; set; } = "C#";
    public List<string> ImportantFiles { get; set; } = new();
    public string ProjectRoot { get; set; } = string.Empty;
    public Dictionary<string, string> TechStack { get; set; } = new();
    public List<string> CompletedDesigns { get; set; } = new();

    public DateTime LastUpdated { get; set; } = DateTime.Now;
}

/// <summary>
/// 4.2 Working Memory —— 当前任务临时上下文
/// </summary>
public class WorkingMemory
{
    public string TaskId { get; set; } = string.Empty;
    public string TaskDescription { get; set; } = string.Empty;
    public List<string> RelatedFiles { get; set; } = new();
    public string CurrentStage { get; set; } = string.Empty;
    public Dictionary<string, object> Context { get; set; } = new();
    public List<AgentContext> AgentContexts { get; set; } = new();
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public void Clear()
    {
        RelatedFiles.Clear();
        Context.Clear();
        AgentContexts.Clear();
        CurrentStage = string.Empty;
    }
}

public class AgentContext
{
    public string AgentName { get; set; } = string.Empty;
    public string GivenContext { get; set; } = string.Empty;
    public List<string> KeyFindings { get; set; } = new();
}

/// <summary>
/// 4.3 Archive Memory —— 历史任务摘要
/// </summary>
public class ArchiveMemory
{
    public string TaskId { get; set; } = string.Empty;
    public string TaskSummary { get; set; } = string.Empty;
    public string WhyThisDesign { get; set; } = string.Empty;
    public List<string> IssuesEncountered { get; set; } = new();
    public Dictionary<string, string> Decisions { get; set; } = new();
    public DateTime CompletedAt { get; set; } = DateTime.Now;
    public string Status { get; set; } = "Completed";
}
