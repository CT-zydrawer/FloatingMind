using FloatingMind.Models.Config;
using FloatingMind.Models.Workflow;

namespace FloatingMind.Services;

/// <summary>
/// 3.2 Workflow Planner —— 从模板库选择或生成任务流程
/// </summary>
public class WorkflowPlanner
{
    private readonly List<WorkflowDef> _customWorkflows = new();

    public WorkflowDef? SelectOrCreate(IntentResult intent)
    {
        // 1. 先匹配预定义模板
        var template = WorkflowTemplates.All
            .FirstOrDefault(t => t.Name.Equals(intent.WorkflowName, StringComparison.OrdinalIgnoreCase));

        if (template != null)
        {
            return template.Clone();
        }

        // 2. 根据意图生成简单流程
        return GenerateDynamicWorkflow(intent);
    }

    private WorkflowDef GenerateDynamicWorkflow(IntentResult intent)
    {
        var wf = new WorkflowDef
        {
            Name = intent.Intent,
            Description = $"动态生成 - {intent.Summary}"
        };

        // 分析类: 读取 → 分析 → 输出
        if (intent.Intent == "Analysis")
        {
            wf.Nodes.Add(new WorkflowNode { Id = "0", Label = "Discovery", AgentType = "FileAgent" });
            wf.Nodes.Add(new WorkflowNode { Id = "1", Label = "Analyze", AgentType = "CodeAgent" });
            wf.Nodes.Add(new WorkflowNode { Id = "2", Label = "Report", AgentType = "CodeAgent" });
            wf.Edges.Add(new WorkflowEdge { Source = "0", Target = "1" });
            wf.Edges.Add(new WorkflowEdge { Source = "1", Target = "2" });
        }
        // 搜索类
        else if (intent.Intent == "Search" || intent.Intent == "FileOps")
        {
            wf.Nodes.Add(new WorkflowNode { Id = "0", Label = "Search", AgentType = "SearchAgent" });
            wf.Nodes.Add(new WorkflowNode { Id = "1", Label = "Execute", AgentType = "FileAgent" });
            wf.Edges.Add(new WorkflowEdge { Source = "0", Target = "1" });
        }
        // 开发/通用
        else
        {
            wf.Nodes.Add(new WorkflowNode { Id = "0", Label = "Analyze", AgentType = "FileAgent" });
            wf.Nodes.Add(new WorkflowNode { Id = "1", Label = "Implement", AgentType = "CodeAgent" });
            wf.Nodes.Add(new WorkflowNode { Id = "2", Label = "Verify", AgentType = "CommandAgent" });
            wf.Edges.Add(new WorkflowEdge { Source = "0", Target = "1" });
            wf.Edges.Add(new WorkflowEdge { Source = "1", Target = "2" });
        }

        return wf;
    }

    public IReadOnlyList<WorkflowDef> GetTemplates() => WorkflowTemplates.All.AsReadOnly();
}
