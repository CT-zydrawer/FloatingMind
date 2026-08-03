using Newtonsoft.Json;

namespace FloatingMind.Models.Workflow;

public class WorkflowDef
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<WorkflowNode> Nodes { get; set; } = new();
    public List<WorkflowEdge> Edges { get; set; } = new();
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public string Status { get; set; } = "Pending"; // Pending/Running/Completed/Failed
    public int CurrentNodeIndex { get; set; } = 0;

    public WorkflowNode? CurrentNode =>
        CurrentNodeIndex < Nodes.Count ? Nodes[CurrentNodeIndex] : null;

    public List<WorkflowNode> GetNextNodes(string nodeId)
    {
        var targets = Edges.Where(e => e.Source == nodeId).Select(e => e.Target).ToList();
        return Nodes.Where(n => targets.Contains(n.Id)).ToList();
    }

    /// <summary>Deep clone with ID preservation</summary>
    public WorkflowDef Clone()
    {
        return new WorkflowDef
        {
            Name = Name,
            Description = Description,
            Nodes = Nodes.Select(n => new WorkflowNode
            {
                Id = n.Id,
                Label = n.Label,
                AgentType = n.AgentType,
                Parameters = new Dictionary<string, string>(n.Parameters),
                OutputKeys = new List<string>(n.OutputKeys)
            }).ToList(),
            Edges = Edges.Select(e => new WorkflowEdge
            {
                Source = e.Source,
                Target = e.Target,
                Condition = e.Condition
            }).ToList()
        };
    }
}

public class WorkflowNode
{
    public string Id { get; set; } = string.Empty;  // 由模板显式设定，不从Guid
    public string Label { get; set; } = string.Empty;    // Research/Writer/Reviewer...
    public string AgentType { get; set; } = string.Empty; // FileAgent/CodeAgent...
    public string Status { get; set; } = "Pending";       // Pending/Running/Completed/Failed
    public Dictionary<string, string> Parameters { get; set; } = new();
    public List<string> OutputKeys { get; set; } = new(); // Blackboard key set by this node
}

public class WorkflowEdge
{
    public string Source { get; set; } = string.Empty;
    public string Target { get; set; } = string.Empty;
    public string Condition { get; set; } = "default";
}

/// <summary>
/// Workflow模板库预定义的模板
/// </summary>
public static class WorkflowTemplates
{
    public static WorkflowDef EssayCreation => new()
    {
        Name = "EssayCreation",
        Description = "论文创建流程",
        Nodes = new()
        {
            new() { Id = "0", Label = "Research", AgentType = "SearchAgent" },
            new() { Id = "1", Label = "Outline", AgentType = "CodeAgent" },
            new() { Id = "2", Label = "Writing", AgentType = "CodeAgent" },
            new() { Id = "3", Label = "Review", AgentType = "CodeAgent" },
            new() { Id = "4", Label = "Format", AgentType = "FileAgent" }
        },
        Edges = new()
        {
            new() { Source = "0", Target = "1" },
            new() { Source = "1", Target = "2" },
            new() { Source = "2", Target = "3" },
            new() { Source = "3", Target = "4" }
        }
    };

    public static WorkflowDef CodeDevelopment => new()
    {
        Name = "CodeDevelopment",
        Description = "代码开发流程",
        Nodes = new()
        {
            new() { Id = "0", Label = "Analysis", AgentType = "FileAgent" },
            new() { Id = "1", Label = "Architecture", AgentType = "CodeAgent" },
            new() { Id = "2", Label = "Coding", AgentType = "CodeAgent" },
            new() { Id = "3", Label = "Testing", AgentType = "CommandAgent" },
            new() { Id = "4", Label = "Review", AgentType = "CodeAgent" }
        },
        Edges = new()
        {
            new() { Source = "0", Target = "1" },
            new() { Source = "1", Target = "2" },
            new() { Source = "2", Target = "3" },
            new() { Source = "3", Target = "4" }
        }
    };

    public static WorkflowDef FileRefactor => new()
    {
        Name = "FileRefactor",
        Description = "文件重构流程",
        Nodes = new()
        {
            new() { Id = "0", Label = "Discovery", AgentType = "FileAgent" },
            new() { Id = "1", Label = "Refactor", AgentType = "CodeAgent" },
            new() { Id = "2", Label = "Verify", AgentType = "CommandAgent" }
        },
        Edges = new()
        {
            new() { Source = "0", Target = "1" },
            new() { Source = "1", Target = "2" }
        }
    };

    public static WorkflowDef QuickQuery => new()
    {
        Name = "QuickQuery",
        Description = "快速查询",
        Nodes = new()
        {
            new() { Id = "0", Label = "Search", AgentType = "SearchAgent" },
            new() { Id = "1", Label = "Summarize", AgentType = "CodeAgent" }
        },
        Edges = new()
        {
            new() { Source = "0", Target = "1" }
        }
    };

    public static List<WorkflowDef> All => new()
    {
        EssayCreation, CodeDevelopment, FileRefactor, QuickQuery
    };
}
