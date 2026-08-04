using FloatingMind.Interfaces;
using FloatingMind.Models.Agent;
using FloatingMind.Models.Blackboard;
using FloatingMind.Models.Workflow;
using FloatingMind.Services;
using FloatingMind.Models.Config;

namespace FloatingMind.Agents;

/// <summary>
/// File Agent —— 文件操作Agent(读取/写入/搜索/分析)
/// 核心约束: 禁止修改 Floating Mind 系统自身的关键源文件, 避免自毁
/// </summary>
public class FileAgent : IAgent
{
    private readonly BlackboardSystem _blackboard;
    private readonly JournalSystem _journal;
    private readonly CommandSafetyService _cmdSafety;
    private readonly string _workspaceRoot;
    private readonly FileHistoryService _fileHistory;

    public string Name => "FileAgent";
    public string Description => "文件操作(读取/写入/搜索/分析)";

    public FileAgent(BlackboardSystem blackboard, JournalSystem journal,
        CommandSafetyService cmdSafety, string workspaceRoot, FileHistoryService fileHistory)
    {
        _blackboard = blackboard;
        _journal = journal;
        _cmdSafety = cmdSafety;
        _workspaceRoot = workspaceRoot;
        _fileHistory = fileHistory;
    }

    public Task<DiscoveryOutput> DiscoveryAsync(WorkflowNode node, Dictionary<string, string> context)
    {
        var deps = new List<string> { "FileSystem" };
        var taskId = context.GetValueOrDefault("taskId", "");
        var action = context.GetValueOrDefault("action", "read");
        var workspaceRoot = context.GetValueOrDefault("workspaceRoot", _workspaceRoot);
        var observations = new List<string>();
        var needModify = new List<string>();

        if (action == "write")
        {
            // 只有写操作才声明需要修改的文件,从而触发资源锁
            var path = ResolveWritePath(context, workspaceRoot);
            if (!string.IsNullOrEmpty(path))
            {
                if (IsSystemProtectedFile(path))
                {
                    observations.Add($"写入目标 '{path}' 属于系统保护文件, 将跳过写入");
                }
                else
                {
                    needModify.Add(path);
                }
            }
            observations.Add($"准备写入: {path}");
        }
        else
        {
            // 只读发现: 发现的文件作为 Observation 记录到 Blackboard,不占用资源锁
            var target = context.GetValueOrDefault("target", "");
            var files = !string.IsNullOrEmpty(target)
                ? FindFiles(target, workspaceRoot)
                : SafeListWorkspace(workspaceRoot, workspaceRoot);
            observations.Add($"发现{files.Count}个相关文件");
        }

        return Task.FromResult(new DiscoveryOutput
        {
            NeedModify = needModify,
            Dependencies = deps,
            Observations = observations,
            Success = true
        });
    }

    public Task<string> ReadTextAsync(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("File not found", path);
        return File.ReadAllTextAsync(path);
    }

    public async Task<AgentResult> ExecuteAsync(WorkflowNode node, Dictionary<string, string> context,
        IEnumerable<BlackboardEntry> blackboard)
    {
        var taskId = context.GetValueOrDefault("taskId", "");
        var action = context.GetValueOrDefault("action", "read");
        var workspaceRoot = context.GetValueOrDefault("workspaceRoot", _workspaceRoot);

        try
        {
            return action switch
            {
                "read" => await ReadFile(context, workspaceRoot),
                "write" => await WriteFile(context, taskId),
                "list" => await ListDirectory(context, workspaceRoot),
                _ => AgentResult.Ok(Name, node.Id, $"FileAgent 执行 [{action}] 完成")
            };
        }
        catch (Exception ex)
        {
            return AgentResult.Fail(Name, node.Id, ex.Message);
        }
    }

    private Task<AgentResult> ReadFile(Dictionary<string, string> context, string workspaceRoot)
    {
        var path = context.GetValueOrDefault("path", "");

        // === 智能路径定位: path 为空时从 target/input 中定位(绝对路径/相对路径/文件名) ===
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            path = PathMapper.LocateExisting(context.GetValueOrDefault("target", ""), workspaceRoot);
        }
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            path = PathMapper.LocateExisting(context.GetValueOrDefault("input", ""), workspaceRoot);
        }

        // 无有效路径 → 退化为列出工作区
        if (string.IsNullOrEmpty(path))
        {
            return ListDirectory(context, workspaceRoot);
        }

        // 路径存在 → 读取
        if (File.Exists(path))
        {
            var content = File.ReadAllText(path);
            var preview = content.Length > 500 ? content[..500] + "..." : content;
            // 读取的文件记入 ModifiedFiles: 让后续节点(重构/审查)能通过 path 参数定位到目标
            return Task.FromResult(AgentResult.Ok(Name, "", $"读取: {path}\n{preview}",
                new List<string> { path }));
        }

        return Task.FromResult(AgentResult.Fail(Name, "", $"文件不存在: {path}"));
    }

    /// <summary>
    /// 解析写目标路径: 显式 path > 用户输入(target/input)中的路径候选(可尚不存在)。
    /// 与 Discovery 阶段共用, 保证资源锁与实际写入路径一致。
    /// </summary>
    private static string ResolveWritePath(Dictionary<string, string> context, string workspaceRoot)
    {
        var path = context.GetValueOrDefault("path", "");
        if (!string.IsNullOrEmpty(path)) return path;

        var text = $"{context.GetValueOrDefault("target", "")} {context.GetValueOrDefault("input", "")}";
        foreach (var cand in PathMapper.ExtractPathTokens(text))
        {
            if (Path.IsPathRooted(cand)) return cand;
            return Path.Combine(workspaceRoot, cand);
        }
        return string.Empty;
    }

    private async Task<AgentResult> WriteFile(Dictionary<string, string> context, string taskId)
    {
        // 目标路径: 显式 path > 用户输入中的路径候选(可尚不存在), 再映射到工作区内安全绝对路径
        var workspaceRoot = context.GetValueOrDefault("workspaceRoot", _workspaceRoot);
        var rawPath = ResolveWritePath(context, workspaceRoot);
        var path = PathMapper.MapToWorkspace(rawPath, workspaceRoot, out var mapErr);
        if (path == null)
        {
            return AgentResult.Ok(Name, "",
                $"文件写入跳过: {mapErr}");
        }

        // 内容: 显式 content > 上一节点文本产出
        var content = context.GetValueOrDefault("content", "");
        if (string.IsNullOrEmpty(content))
            content = context.GetValueOrDefault("previousOutput", "");

        // === 系统保护文件检查 ===
        if (IsSystemProtectedFile(path))
        {
            return AgentResult.Ok(Name, "",
                $"文件写入跳过: '{path}' 属于 Floating Mind 系统保护文件, 不允许修改。");
        }

        if (string.IsNullOrEmpty(content))
        {
            // 避免用空内容覆盖已有文件
            return AgentResult.Ok(Name, "",
                $"文件写入跳过: 无内容可写(上一节点也无文本产出)。目标: {path}");
        }

        // === 防覆盖守卫: 目标已存在且内容直接来自上一节点文本产出时跳过,
        // 避免流程链(如 Format 节点拿到 Review 文本)把已成稿文件覆盖成报告内容 ===
        if (File.Exists(path) && string.Equals(content, context.GetValueOrDefault("previousOutput", ""),
                StringComparison.Ordinal))
        {
            return AgentResult.Ok(Name, "",
                $"文件写入跳过: 目标已存在且内容与上一节点产出相同, 不覆盖已有文件。目标: {path}");
        }

        // 备份原文件
        string beforeState = "";
        if (File.Exists(path))
        {
            beforeState = await File.ReadAllTextAsync(path);
        }

        await File.WriteAllTextAsync(path, content);
        await _fileHistory.RecordWriteAsync(taskId, Name, path, beforeState, content, "文件写入");

        _journal.LogFileWrite(Name, path, beforeState, content);
        _blackboard.AddObservation(taskId, $"写入文件: {path}", Name);

        return AgentResult.Ok(Name, "", $"写入: {path}", new List<string> { path });
    }

    private Task<AgentResult> ListDirectory(Dictionary<string, string> context, string workspaceRoot)
    {
        var path = context.GetValueOrDefault("path", workspaceRoot);
        if (!Directory.Exists(path))
            return Task.FromResult(AgentResult.Fail(Name, "", $"目录不存在: {path}"));

        var files = SafeListWorkspace(path, workspaceRoot);
        var listing = string.Join("\n", files.Take(50).Select(f => $"  {f}"));
        if (files.Count > 50) listing += $"\n  ... 还有{files.Count - 50}个文件";

        return Task.FromResult(AgentResult.Ok(Name, "", $"目录: {path}\n{listing}"));
    }

    private List<string> FindFiles(string pattern, string workspaceRoot)
    {
        try
        {
            return Directory.GetFiles(workspaceRoot, $"*{pattern}*", SearchOption.AllDirectories)
                .Where(f => !Path.GetRelativePath(workspaceRoot, f)
                    .Split(Path.DirectorySeparatorChar)
                    .Any(seg => ExcludedDirs.Contains(seg)))
                .Take(30)
                .Select(f => Path.GetRelativePath(workspaceRoot, f))
                .ToList();
        }
        catch { return new(); }
    }

    private static readonly HashSet<string> ExcludedDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", ".vs", ".git", ".floatingmind", ".agents", ".deepcode", "node_modules", "packages", "Debug", "Release"
    };

    /// <summary>
    /// 系统保护文件 —— 禁止 Agent 修改 Floating Mind 自身的关键源文件
    /// 这是防止 Agent "自毁" 的核心安全机制
    /// </summary>
    private static bool IsSystemProtectedFile(string path)
    {
        var fileName = Path.GetFileName(path);
        return SystemProtectedFiles.Contains(fileName);
    }

    private static readonly HashSet<string> SystemProtectedFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "Generated.cs",          // Agent 输出冲突文件(历史遗留,必须保护)
        "MainWindow.xaml.cs",    // UI入口
        "MainWindow.xaml",       // XAML定义
        "App.xaml.cs",           // 应用入口
        "App.xaml",              // 应用定义
        "FloatingMind.csproj",   // 项目文件
        "FloatingMind.sln",      // 解决方案文件
        "Program.cs",            // 程序入口
        "AssemblyInfo.cs",       // 程序集信息
    };

    private List<string> SafeListWorkspace(string path, string workspaceRoot)
    {
        try
        {
            return Directory.GetFileSystemEntries(path)
                .Where(f => !ExcludedDirs.Contains(Path.GetFileName(f)))
                .Take(100)
                .Select(f => Path.GetRelativePath(workspaceRoot, f))
                .ToList();
        }
        catch { return new(); }
    }

    public Task<bool> RollbackAsync(string nodeId)
    {
        return Task.FromResult(true); // Journal based rollback handled by RollbackManager
    }
}
