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
            var path = context.GetValueOrDefault("path", "");
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

        // === 智能路径解析: 如果 path 参数为空, 尝试从 target/input 中提取 ===
        if (string.IsNullOrEmpty(path))
        {
            path = ExtractPathFromInput(
                context.GetValueOrDefault("target", ""),
                context.GetValueOrDefault("input", ""),
                workspaceRoot);
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
            return Task.FromResult(AgentResult.Ok(Name, "", $"读取: {path}\n{preview}"));
        }

        return Task.FromResult(AgentResult.Fail(Name, "", $"文件不存在: {path}"));
    }

    /// <summary>
    /// 从用户输入(target/input)中智能提取文件/目录路径
    /// 支持带引号路径(如 "C:\foo\bar.py")、绝对路径、相对路径
    /// </summary>
    private static string ExtractPathFromInput(string target, string input, string workspaceRoot)
    {
        var candidates = new[] { target, input }.Where(s => !string.IsNullOrWhiteSpace(s));

        foreach (var text in candidates)
        {
            // 尝试提取带引号的路径: "C:\foo\bar.py" 或 'C:\foo\bar.py'
            var quoted = ExtractQuotedPath(text);
            if (!string.IsNullOrEmpty(quoted) && (File.Exists(quoted) || Directory.Exists(quoted)))
                return quoted;

            // 尝试提取绝对路径(以盘符或 \\ 开头)
            if (text.Length >= 3 && char.IsLetter(text[0]) && text[1] == ':' && text[2] == '\\')
            {
                var absPath = text.Trim().TrimEnd('"', '\'');
                if (File.Exists(absPath) || Directory.Exists(absPath))
                    return absPath;
            }

            // 尝试作为相对路径(在工作区下查找)
            var relPath = Path.Combine(workspaceRoot, text.Trim().Trim('"', '\''));
            if (File.Exists(relPath) || Directory.Exists(relPath))
                return relPath;
        }

        return string.Empty;
    }

    private static string ExtractQuotedPath(string text)
    {
        // 匹配 "..." 或 '...' 中的路径
        foreach (var quote in new[] { '"', '\'' })
        {
            int start = text.IndexOf(quote);
            while (start >= 0)
            {
                int end = text.IndexOf(quote, start + 1);
                if (end > start + 1)
                {
                    var candidate = text[(start + 1)..end];
                    // 简单启发: 包含目录分隔符或文件扩展名则认为是路径
                    if (candidate.Contains(Path.DirectorySeparatorChar) ||
                        candidate.Contains(Path.AltDirectorySeparatorChar) ||
                        Path.HasExtension(candidate))
                    {
                        return candidate;
                    }
                }
                start = text.IndexOf(quote, start + 1);
            }
        }
        return string.Empty;
    }

    private async Task<AgentResult> WriteFile(Dictionary<string, string> context, string taskId)
    {
        var path = context.GetValueOrDefault("path", "");
        // 无path时优先用上一节点文本产出作为内容; 仍无目标则降级跳过而非失败
        var content = context.GetValueOrDefault("content", "");
        if (string.IsNullOrEmpty(content))
            content = context.GetValueOrDefault("previousOutput", "");

        if (string.IsNullOrEmpty(path))
        {
            return AgentResult.Ok(Name, "",
                "文件写入跳过: 未指定目标路径。如需写入请在节点参数中提供 path。");
        }

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
