using FloatingMind.Models.Memory;
using FloatingMind.Services;

namespace FloatingMind.Services.LLM;

/// <summary>
/// 提示词构建器 —— 将Memory/Blackboard/Project上下文注入到 Agent System Prompt
/// 每个Agent获取其完成任务所需的精确上下文, 不会获得多余信息
/// </summary>
public class PromptBuilder
{
    private readonly MemorySystem _memory;
    private readonly BlackboardSystem _blackboard;

    public PromptBuilder(MemorySystem memory, BlackboardSystem blackboard)
    {
        _memory = memory;
        _blackboard = blackboard;
    }

    /// ===== Intent Router =====
    public (string systemPrompt, string userMessage) BuildIntent(string userInput)
    {
        var pm = _memory.GetProjectMemory();
        var projectCtx = pm.Project.Length > 0
            ? $"项目: {pm.Project}, 框架: {pm.Framework}, 语言: {pm.Language}, 关键文件: {string.Join(", ", pm.ImportantFiles)}"
            : "未初始化项目, 工作区路径: " + pm.ProjectRoot;

        var archiveHints = _memory.GetArchiveMemories();
        var archiveText = archiveHints.Count > 0
            ? string.Join("\n", archiveHints.Take(3).Select(a => $"- 任务: {a.TaskSummary}"))
            : "无";

        return (AgentPrompts.IntentRouter
            .Replace("{projectContext}", projectCtx)
            .Replace("{archiveHints}", archiveText),
            userInput);
    }

    /// ===== File Agent =====
    public (string system, string user) BuildFileAgent(string taskId, string action,
        string taskDesc, string stage, string workspaceRoot)
    {
        var bb = _blackboard.GetSummary(taskId);
        var structure = GetDirSummary(workspaceRoot);
        var instruction = action switch
        {
            "list" => "列出工作区完整目录结构, 分析文件组织方式, 产出关键发现到Blackboard",
            "read" => "读取指定文件，输出内容摘要和关键分析",
            "write" => "根据Blackboard上的决策和当前任务需求，生成修改后的文件内容(完整文件)",
            _ => "分析当前阶段需要做什么，输出你的计划"
        };

        return (AgentPrompts.FileAgent
            .Replace("{taskDescription}", taskDesc)
            .Replace("{currentStage}", stage)
            .Replace("{blackboardSummary}", bb)
            .Replace("{workspaceStructure}", structure)
            .Replace("{actionInstruction}", instruction),
            $"执行操作: {action}");
    }

    /// ===== Code Agent =====
    public (string system, string user) BuildCodeAgent(string taskId, string action,
        string taskDesc, string stage)
    {
        var pm = _memory.GetProjectMemory();
        var bb = _blackboard.GetSummary(taskId);
        var fileContents = LoadRelevantFiles(pm);
        var instruction = action switch
        {
            "analyze" => "分析项目代码架构: 目录结构、类关系、依赖图、设计模式, 输出发现到Blackboard",
            "generate" => "根据任务需求和现有代码风格, 生成完整可编译的代码文件(.cs), 包含命名空间和using",
            "refactor" => "分析目标代码的问题(耦合/可读性/性能), 给出重构后的完整代码",
            "review" => "审查代码质量: 命名规范、异常处理、安全性、测试覆盖，逐一列出问题",
            _ => "分析当前阶段需要做什么，输出你的行动计划"
        };

        return (AgentPrompts.CodeAgent
            .Replace("{taskDescription}", taskDesc)
            .Replace("{currentStage}", stage)
            .Replace("{blackboardSummary}", bb)
            .Replace("{projectName}", pm.Project)
            .Replace("{framework}", pm.Framework)
            .Replace("{language}", pm.Language)
            .Replace("{importantFiles}", string.Join(", ", pm.ImportantFiles))
            .Replace("{fileContents}", fileContents)
            .Replace("{actionInstruction}", instruction),
            $"执行操作: {action}");
    }

    /// ===== Search Agent =====
    public (string system, string user) BuildSearchAgent(string taskId, string query)
    {
        var bb = _blackboard.GetSummary(taskId);
        return (AgentPrompts.SearchAgent
            .Replace("{taskDescription}", query)
            .Replace("{query}", query)
            .Replace("{blackboardSummary}", bb),
            $"搜索查询: {query}");
    }

    /// ===== Command Agent =====
    public (string system, string user) BuildCommandAgent(string taskId, string action,
        string taskDesc, string stage)
    {
        var pm = _memory.GetProjectMemory();
        var bb = _blackboard.GetSummary(taskId);
        var builder = pm.Framework?.ToLower() switch
        {
            "wpf" or "avalonia" => "dotnet build",
            _ => "dotnet build"
        };
        var instruction = action switch
        {
            "exec" => "分析当前阶段需要执行什么命令来完成目标(构建/测试/运行/安装依赖), 输出建议的命令列表",
            _ => "根据任务需求建议合适的命令"
        };

        return (AgentPrompts.CommandAgent
            .Replace("{taskDescription}", taskDesc)
            .Replace("{currentStage}", stage)
            .Replace("{blackboardSummary}", bb)
            .Replace("{framework}", pm.Framework)
            .Replace("{buildTool}", builder)
            .Replace("{packageManager}", "NuGet")
            .Replace("{workspaceRoot}", pm.ProjectRoot)
            .Replace("{actionInstruction}", instruction),
            $"执行操作: {action}");
    }

    /// ===== Discovery 阶段 =====
    public (string system, string user) BuildDiscovery(string agentName, string taskDesc,
        string workspaceRoot)
    {
        var overview = GetDirSummary(workspaceRoot);
        return (AgentPrompts.Discovery
            .Replace("{agentName}", agentName)
            .Replace("{taskDescription}", taskDesc)
            .Replace("{workspaceOverview}", overview),
            $"探索环境并分析影响范围");
    }

    /// ===== Supervisor 验收总结 =====
    public (string system, string user) BuildAcceptanceSummary(string taskId,
        string originalInput, string workflowName, string stageResults)
    {
        var bb = _blackboard.GetSummary(taskId);
        return (AgentPrompts.SupervisorSummary
            .Replace("{originalInput}", originalInput)
            .Replace("{workflowName}", workflowName)
            .Replace("{stageResults}", stageResults)
            .Replace("{blackboardSummary}", bb),
            $"请验收");
    }

    /// ===== 澄清提问生成 =====
    public (string system, string user) BuildClarificationQuestions(string userInput)
    {
        var pm = _memory.GetProjectMemory();
        var projectCtx = pm.Project.Length > 0
            ? $"项目: {pm.Project}, 框架: {pm.Framework}, 语言: {pm.Language}, 工作区: {pm.ProjectRoot}"
            : "未初始化项目, 工作区路径: " + pm.ProjectRoot;

        return (AgentPrompts.QuestionGenerator
            .Replace("{projectContext}", projectCtx),
            userInput);
    }

    // ===== 辅助 =====

    private static string GetDirSummary(string root)
    {
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            return "(工作区未配置或不可访问)";
        try
        {
            var entries = Directory.GetFileSystemEntries(root)
                .Where(p => !PromptExcludedDirs.Contains(Path.GetFileName(p)))
                .OrderBy(p => (File.GetAttributes(p) & FileAttributes.Directory) == 0 ? 1 : 0)
                .Take(40).Select(p =>
                {
                    var rel = Path.GetRelativePath(root, p);
                    var isDir = (File.GetAttributes(p) & FileAttributes.Directory) != 0;
                    return isDir ? $"[DIR]  {rel}/" : $"       {rel}";
                });
            return string.Join("\n", entries);
        }
        catch { return "(读取工作区失败)"; }
    }

    private static string LoadRelevantFiles(ProjectMemory pm)
    {
        if (pm.ImportantFiles.Count == 0)
            return "(无关键文件)";
        var sb = new System.Text.StringBuilder();
        foreach (var file in pm.ImportantFiles.Take(5))
        {
            var path = Path.Combine(pm.ProjectRoot, file);
            if (File.Exists(path))
            {
                try
                {
                    var content = File.ReadAllText(path);
                    if (content.Length > 1500) content = content[..1500] + "\n// ... (截断)";
                    sb.AppendLine($"### {file} ###");
                    sb.AppendLine(content);
                    sb.AppendLine();
                }
                catch { sb.AppendLine($"### {file} ### (读取失败)"); }
            }
        }
        return sb.Length > 0 ? sb.ToString() : "(无关键文件)";
    }

    private static readonly HashSet<string> PromptExcludedDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", ".vs", ".git", ".floatingmind", ".agents", ".deepcode",
        "node_modules", "packages", "Debug", "Release"
    };
}
