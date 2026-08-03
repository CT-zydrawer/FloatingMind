using FloatingMind.Interfaces;
using FloatingMind.Models.Agent;
using FloatingMind.Models.Blackboard;
using FloatingMind.Models.Config;
using FloatingMind.Models.Workflow;
using FloatingMind.Services;
using System.Diagnostics;

namespace FloatingMind.Agents;

/// <summary>
/// Search Agent —— 信息检索Agent
/// 能主动搜索工作区文件、代码内容、以及通过LLM做知识检索
/// </summary>
public class SearchAgent : IAgent
{
    private readonly BlackboardSystem _blackboard;
    private readonly JournalSystem _journal;
    private readonly AppConfig _config;
    private readonly DeepSeekService _llm;
    private readonly string _workspaceRoot;

    public string Name => "SearchAgent";
    public string Description => "信息检索(本地文件/代码搜索/LLM知识查询)";

    public SearchAgent(BlackboardSystem blackboard, JournalSystem journal, AppConfig config,
        DeepSeekService llm, string workspaceRoot)
    {
        _blackboard = blackboard;
        _journal = journal;
        _config = config;
        _llm = llm;
        _workspaceRoot = workspaceRoot;
    }

    public Task<DiscoveryOutput> DiscoveryAsync(WorkflowNode node, Dictionary<string, string> context)
    {
        var deps = new List<string> { "FileSystem", "LLM" };
        var query = context.GetValueOrDefault("query", "");
        if (!string.IsNullOrEmpty(query))
            _blackboard.AddObservation(context.GetValueOrDefault("taskId", ""),
                $"搜索查询: {query}", Name);
        return Task.FromResult(DiscoveryOutput.FromResult(new(), deps));
    }

    public async Task<AgentResult> ExecuteAsync(WorkflowNode node, Dictionary<string, string> context,
        IEnumerable<BlackboardEntry> blackboard)
    {
        var taskId = context.GetValueOrDefault("taskId", "");
        var query = context.GetValueOrDefault("query", "");
        var action = context.GetValueOrDefault("action", "search");

        try
        {
            return action switch
            {
                "search" => await SmartSearch(query, taskId, context),
                "code_search" => await CodeSearch(context),
                "file_search" => await FileSearch(context),
                _ => AgentResult.Ok(Name, node.Id, $"SearchAgent [{action}] 完成")
            };
        }
        catch (Exception ex)
        {
            return AgentResult.Fail(Name, node.Id, ex.Message);
        }
    }

    /// <summary>
    /// 智能搜索: 先用本地文件系统搜索, 再用LLM综合分析
    /// </summary>
    private async Task<AgentResult> SmartSearch(string query, string taskId,
        Dictionary<string, string> context)
    {
        if (string.IsNullOrEmpty(query))
            return AgentResult.Ok(Name, "", "无搜索查询词");

        _journal.LogAgentAction(Name, "SmartSearch", query);

        // Step 1: 本地文件系统搜一把
        var localResults = new List<string>();
        var matchingFiles = new List<string>();
        try
        {
            matchingFiles = Directory.GetFiles(_workspaceRoot, $"*{query}*", SearchOption.AllDirectories)
                .Where(f => !IsExcludedPath(f))
                .Take(15).ToList();
            if (matchingFiles.Count > 0)
            {
                localResults.Add($"找到 {matchingFiles.Count} 个匹配文件:");
                foreach (var f in matchingFiles)
                    localResults.Add($"  · {Path.GetRelativePath(_workspaceRoot, f)}");
            }

            // 代码内容搜索 (C# 文件)
            var codeMatches = Directory.GetFiles(_workspaceRoot, "*.cs", SearchOption.AllDirectories)
                .Where(f => !IsExcludedPath(f))
                .Where(f =>
                {
                    try { return File.ReadAllText(f).Contains(query, StringComparison.OrdinalIgnoreCase); }
                    catch { return false; }
                })
                .Take(10).ToList();
            if (codeMatches.Count > 0)
            {
                localResults.Add($"代码中包含 \"{query}\" 的文件:");
                foreach (var f in codeMatches)
                    localResults.Add($"  · {Path.GetRelativePath(_workspaceRoot, f)}");
            }
        }
        catch { /* 本地搜索失败不影响LLM搜索 */ }

        _blackboard.AddObservation(taskId, $"本地搜索: 找到相关文件/代码", Name);

        // Step 2: LLM综合分析
        if (!_llm.IsConfigured)
        {
            var localReport = localResults.Count > 0
                ? string.Join("\n", localResults)
                : $"工作区中未找到与 \"{query}\" 直接匹配的文件。请尝试其他关键词。";
            return AgentResult.Ok(Name, "",
                $"=== 本地搜索结果: {query} ===\n{localReport}\n\n(配置 DeepSeek API Key 可启用 LLM 深度分析)");
        }

        var localContext = localResults.Count > 0
            ? $"已在工作区找到以下相关文件:\n{string.Join("\n", localResults)}"
            : "工作区中未找到直接匹配的文件。";

        // 读取部分匹配文件的内容供LLM分析
        var fileContents = "";
        if (matchingFiles.Count > 0)
        {
            var firstFile = matchingFiles.First();
            try
            {
                var content = await File.ReadAllTextAsync(firstFile);
                if (content.Length > 2000) content = content[..2000] + "\n// ... (截断)";
                fileContents = $"\n首个匹配文件 ({Path.GetRelativePath(_workspaceRoot, firstFile)}) 内容预览:\n{content}";
            }
            catch { }
        }

        var systemPrompt = @"你是 Floating Mind 的搜索分析助手，运行在用户的本地工作区内。

## 你的能力
- 你能访问用户的本地文件系统，可以读取和搜索文件
- 你已经在本地执行了文件/代码搜索，结果附在下方
- 结合本地搜索结果和你的知识，给出全面、有深度的回答

## 核心原则
1. **主动**: 不要告诉用户""你无法访问本地文件""——你已经搜索过了
2. **结合本地结果**: 先解读本地找到的文件/代码，再补充你的知识
3. **实用**: 直接告诉用户结论，不是教用户怎么做
4. **诚实**: 如果信息不足以回答，明确说明还需要什么

## 工作区
{workspaceContext}
";

        var userPrompt = $@"## 用户查询
{query}

## 本地搜索结果
{localContext}
{fileContents}

请基于以上信息，直接回答用户的问题。不要说""你无法访问文件""——搜索结果已经在上面了。";

        try
        {
            var answer = await _llm.ChatAsync(userPrompt,
                systemPrompt.Replace("{workspaceContext}", GetWorkspaceOverview()));
            return AgentResult.Ok(Name, "",
                $"=== 搜索结果: {query} ===\n{answer}");
        }
        catch (Exception ex)
        {
            // LLM失败时返回本地搜索结果
            return AgentResult.Ok(Name, "",
                $"=== 本地搜索结果: {query} ===\n{(localResults.Count > 0 ? string.Join("\n", localResults) : "未找到相关结果")}\n\n(LLM分析暂时不可用: {ex.Message})");
        }
    }

    private async Task<AgentResult> CodeSearch(Dictionary<string, string> context)
    {
        var query = context.GetValueOrDefault("query", "");
        var path = context.GetValueOrDefault("path", _workspaceRoot);

        var files = Directory.GetFiles(path, "*.cs", SearchOption.AllDirectories)
            .Where(f => !IsExcludedPath(f))
            .Where(f =>
            {
                try { return File.ReadAllText(f).Contains(query, StringComparison.OrdinalIgnoreCase); }
                catch { return false; }
            })
            .Take(20).ToList();

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"=== 代码搜索: \"{query}\" ===\n找到{files.Count}个匹配\n");
        foreach (var f in files)
        {
            var relPath = Path.GetRelativePath(path, f);
            sb.AppendLine($"  {relPath}");
            // 显示匹配行
            try
            {
                var lines = File.ReadAllLines(f);
                for (int i = 0; i < lines.Length; i++)
                {
                    if (lines[i].Contains(query, StringComparison.OrdinalIgnoreCase))
                        sb.AppendLine($"    行{i + 1}: {lines[i].Trim()[..Math.Min(100, lines[i].Trim().Length)]}");
                }
            }
            catch { }
        }
        return AgentResult.Ok(Name, "", sb.ToString());
    }

    private Task<AgentResult> FileSearch(Dictionary<string, string> context)
    {
        var query = context.GetValueOrDefault("query", "");
        var path = context.GetValueOrDefault("path", _workspaceRoot);

        var files = Directory.GetFiles(path, $"*{query}*", SearchOption.AllDirectories)
            .Where(f => !IsExcludedPath(f))
            .Take(30)
            .Select(f => $"  {Path.GetRelativePath(path, f)}")
            .ToList();

        return Task.FromResult(AgentResult.Ok(Name, "",
            $"=== 文件搜索: \"{query}\" ===\n找到{files.Count}个文件\n{string.Join("\n", files)}"));
    }

    private bool IsExcludedPath(string fullPath)
    {
        var relPath = Path.GetRelativePath(_workspaceRoot, fullPath);
        return relPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(seg => seg is "bin" or "obj" or ".vs" or ".git" or ".floatingmind"
                        or ".agents" or ".deepcode" or "node_modules" or "packages"
                        or "Debug" or "Release");
    }

    private string GetWorkspaceOverview()
    {
        try
        {
            var entries = Directory.GetFileSystemEntries(_workspaceRoot)
                .Where(p => !IsExcludedPath(p))
                .Take(30)
                .Select(p =>
                {
                    var rel = Path.GetRelativePath(_workspaceRoot, p);
                    var isDir = (File.GetAttributes(p) & FileAttributes.Directory) != 0;
                    return isDir ? $"[DIR] {rel}/" : $"      {rel}";
                });
            return string.Join("\n", entries);
        }
        catch { return "(工作区不可读)"; }
    }

    public Task<bool> RollbackAsync(string nodeId) => Task.FromResult(true);
}
