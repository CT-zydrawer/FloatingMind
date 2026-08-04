using FloatingMind.Interfaces;
using FloatingMind.Models.Agent;
using FloatingMind.Models.Blackboard;
using FloatingMind.Models.Config;
using FloatingMind.Models.Workflow;
using FloatingMind.Services;
using System.Diagnostics;

namespace FloatingMind.Agents;

/// <summary>
/// Code Agent —— 代码生成/分析/修改/修复(多语言支持)
/// </summary>
public class CodeAgent : IAgent
{
    private readonly BlackboardSystem _blackboard;
    private readonly JournalSystem _journal;
    private readonly ModelRouter _modelRouter;
    private readonly AppConfig _config;
    private readonly string _workspaceRoot;
    private readonly DeepSeekService _llm;
    private readonly FileHistoryService _fileHistory;

    public string Name => "CodeAgent";
    public string Description => "代码生成/分析/修改/修复";

    public CodeAgent(BlackboardSystem blackboard, JournalSystem journal,
        ModelRouter modelRouter, AppConfig config, string workspaceRoot, DeepSeekService llm,
        FileHistoryService fileHistory)
    {
        _blackboard = blackboard;
        _journal = journal;
        _modelRouter = modelRouter;
        _config = config;
        _workspaceRoot = workspaceRoot;
        _llm = llm;
        _fileHistory = fileHistory;
    }

    public Task<DiscoveryOutput> DiscoveryAsync(WorkflowNode node, Dictionary<string, string> context)
    {
        var deps = new List<string> { "ProjectMemory", "Compiler" };
        var observations = new List<string>();
        var workspaceRoot = context.GetValueOrDefault("workspaceRoot", _workspaceRoot);
        var language = LanguageDetector.Detect(workspaceRoot);

        if (context.TryGetValue("target", out var target) && !string.IsNullOrEmpty(target))
        {
            // target 已是定位后的具体文件/目录(由Supervisor注入), 不再做整句匹配
            if (File.Exists(target) || Directory.Exists(target))
            {
                observations.Add($"代码分析目标: {target}");
            }
            else
            {
                var files = EnumerateSourceFiles(workspaceRoot, language)
                    .Where(f => f.Contains(target, StringComparison.OrdinalIgnoreCase))
                    .Take(20).ToList();
                observations.Add($"代码分析: 发现{files.Count}个相关文件 ({language})");
            }
        }

        // 只读发现: 不占用资源锁,文件列表记入 Blackboard
        return Task.FromResult(new DiscoveryOutput
        {
            NeedModify = new(),
            Dependencies = deps,
            Observations = observations,
            Success = true
        });
    }

    /// <summary>
    /// 按语言枚举工作区源码文件,排除 bin/obj/.vs/.git/__pycache__ 等目录
    /// </summary>
    private static IEnumerable<string> EnumerateSourceFiles(string root, ProjectLanguage language)
    {
        if (!Directory.Exists(root)) return Array.Empty<string>();
        var pattern = LanguageDetector.GetFilePattern(language);
        return Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories)
            .Where(f =>
            {
                var parts = Path.GetRelativePath(root, f)
                    .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return !parts.Any(p => p is "bin" or "obj" or ".vs" or ".git" or "__pycache__"
                    or "node_modules" or "venv" or ".venv" or "model" or "models");
            });
    }

    public async Task<AgentResult> ExecuteAsync(WorkflowNode node, Dictionary<string, string> context,
        IEnumerable<BlackboardEntry> blackboard)
    {
        var taskId = context.GetValueOrDefault("taskId", "");
        var action = context.GetValueOrDefault("action", "analyze");

        try
        {
            return action switch
            {
                "analyze" => AnalyzeStructure(context),
                "generate" => await GenerateCode(context, taskId, blackboard),
                "fix" => await FixCode(context, taskId, blackboard),
                "refactor" => await RefactorCode(context, taskId),
                "review" => await ReviewCode(context),
                _ => AgentResult.Ok(Name, node.Id, $"CodeAgent 执行 [{action}] 完成")
            };
        }
        catch (Exception ex)
        {
            return AgentResult.Fail(Name, node.Id, ex.Message);
        }
    }

    private AgentResult AnalyzeStructure(Dictionary<string, string> context)
    {
        var workspaceRoot = context.GetValueOrDefault("workspaceRoot", _workspaceRoot);
        var language = LanguageDetector.Detect(workspaceRoot);
        var path = context.GetValueOrDefault("path", workspaceRoot);
        var files = EnumerateSourceFiles(path, language).Take(30).ToList();

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("=== 项目代码结构分析 ===");
        sb.AppendLine($"语言: {LanguageDetector.GetFileExtension(language).TrimStart('.')} | 源码文件总数: {files.Count}");

        var nsSet = new HashSet<string>();
        foreach (var f in files)
        {
            try
            {
                var relPath = Path.GetRelativePath(workspaceRoot, f);
                sb.AppendLine($"  {relPath}");
                var lines = File.ReadAllLines(f).Take(5);
                foreach (var line in lines)
                {
                    var t = line.TrimStart();
                    if (t.StartsWith("namespace ") || t.StartsWith("import ") || t.StartsWith("from "))
                        nsSet.Add(t);
                }
            }
            catch { }
        }

        sb.AppendLine($"\n模块/命名空间: {nsSet.Count}");
        foreach (var ns in nsSet.Take(20)) sb.AppendLine($"  {ns}");

        return AgentResult.Ok(Name, "", sb.ToString());
    }

    private async Task<AgentResult> GenerateCode(Dictionary<string, string> context, string taskId,
        IEnumerable<BlackboardEntry> blackboard)
    {
        // 描述来源: 显式 description > 用户原始输入(由Supervisor注入)
        var description = context.GetValueOrDefault("description", "");
        if (string.IsNullOrWhiteSpace(description))
            description = context.GetValueOrDefault("input", "");

        if (string.IsNullOrWhiteSpace(description))
        {
            return AgentResult.Ok(Name, "",
                "代码生成跳过: 缺少任务描述");
        }

        var workspaceRoot = context.GetValueOrDefault("workspaceRoot", _workspaceRoot);
        var language = LanguageDetector.Detect(workspaceRoot);
        var languageName = GetLanguageName(language);
        var ext = LanguageDetector.GetFileExtension(language);

        // 未配置API Key: 回退占位符模板,不污染工作区
        if (!_llm.IsConfigured)
        {
            return AgentResult.Ok(Name, "",
                "代码生成跳过: 未配置 DeepSeek API Key (请在设置页填写)");
        }

        var summary = _blackboard.GetSummary(taskId);
        var previousOutput = context.GetValueOrDefault("previousOutput", "");
        var explicitOutput = context.GetValueOrDefault("output", "");

        // === 模式A: 显式 output 参数(模板/调度指定) → 单文件生成, 写入该路径 ===
        if (!string.IsNullOrWhiteSpace(explicitOutput))
        {
            var target = PathMapper.MapToWorkspace(explicitOutput, workspaceRoot, out var mapErr);
            if (target == null)
                return AgentResult.Ok(Name, "", $"代码生成跳过: 输出路径无效 - {mapErr}");

            var code = await GenerateSingleFileContentAsync(description, summary,
                previousOutput, languageName, target, null);
            if (code == null)
                return AgentResult.Ok(Name, "", "代码生成跳过: LLM调用失败");

            return await WriteGeneratedFileAsync(taskId, target, code, "代码生成");
        }

        // === 模式B: 两阶段多文件生成(设计文档: 结果可靠 + 成本可控) ===
        // 阶段1: LLM先出文件清单(仅路径+用途, 输出量小不易截断)
        var plan = await GenerateFilePlanAsync(taskId, description, summary, previousOutput,
            languageName, workspaceRoot);

        if (plan.Count > 0)
        {
            var modified = new List<string>();
            var skipped = new List<string>();
            const int maxFiles = 8;

            // 阶段2: 逐文件生成完整内容并写入(映射到工作区内的正确位置)
            foreach (var item in plan)
            {
                var target = PathMapper.MapToWorkspace(item.Path, workspaceRoot, out var mapErr);
                if (target == null)
                {
                    skipped.Add($"{item.Path}({mapErr})");
                    continue;
                }
                if (modified.Count >= maxFiles)
                {
                    skipped.Add($"{item.Path}(超出单次生成上限{maxFiles}个文件)");
                    continue;
                }

                var code = await GenerateSingleFileContentAsync(description, summary,
                    previousOutput, languageName, target, item.Purpose, plan);
                if (code == null)
                {
                    skipped.Add($"{item.Path}(LLM调用失败)");
                    continue;
                }

                var r = await WriteGeneratedFileAsync(taskId, target, code,
                    $"代码生成: {item.Purpose}");
                if (r.Success) modified.Add(target);
                else skipped.Add($"{item.Path}(写入失败)");
            }

            if (modified.Count == 0)
                return AgentResult.Ok(Name, "",
                    $"多文件生成未完成: 计划{plan.Count}个文件均未生成" +
                    (skipped.Count > 0 ? $" [{string.Join("; ", skipped)}]" : ""));

            return AgentResult.Ok(Name, "",
                $"已生成 {modified.Count} 个文件:\n" +
                string.Join("\n", modified.Select(m => $"  {Path.GetRelativePath(workspaceRoot, m)}")) +
                (skipped.Count > 0 ? $"\n跳过: {string.Join("; ", skipped)}" : ""),
                modified);
        }

        // === 模式C: 兜底单文件生成(清单解析失败/为空) ===
        // 输出位置: 用户输入中的显式路径(可尚不存在) > 工作区根目录+默认文件名
        var fallbackPath = PathMapper.ResolveOutputPath(null, description, workspaceRoot,
            $"Gen_{DateTime.Now:yyyyMMdd_HHmmss}{ext}", out var fbErr);
        if (fallbackPath == null)
            return AgentResult.Ok(Name, "", $"代码生成跳过: 无法确定输出位置 - {fbErr}");

        var fallbackCode = await GenerateSingleFileContentAsync(description, summary,
            previousOutput, languageName, fallbackPath, null);
        if (fallbackCode == null)
            return AgentResult.Ok(Name, "", "代码生成跳过: LLM调用失败");

        return await WriteGeneratedFileAsync(taskId, fallbackPath, fallbackCode, "代码生成");
    }

    /// <summary>
    /// 两阶段生成·阶段1: 生成文件清单(仅路径+用途, 不含内容)。
    /// LLM先规划拆分为哪些文件, 输出量小, 避免大JSON截断。
    /// 解析失败/为空返回空列表(调用方走兜底单文件模式)。
    /// </summary>
    private async Task<List<(string Path, string Purpose)>> GenerateFilePlanAsync(
        string taskId, string description, string summary, string previousOutput,
        string languageName, string workspaceRoot)
    {
        var prompt = $"## 任务描述\n{Truncate(description, 1500)}\n" +
                     (string.IsNullOrWhiteSpace(summary) ? "" : $"\n## 黑板上下文\n{Truncate(summary, 1200)}\n") +
                     (string.IsNullOrWhiteSpace(previousOutput) ? "" : $"\n## 上一阶段产出\n{Truncate(previousOutput, 1200)}\n") +
                     $"\n## 工作区根目录\n{workspaceRoot}\n" +
                     "\n## 要求\n" +
                     $"1. 为完成上述任务, 规划需要创建的文件清单(相对工作区根目录的路径, 不要绝对路径)\n" +
                     "2. 只输出文件路径和用途, 不要输出文件内容\n" +
                     "3. 文件数量 1-8 个; 代码文件使用正确的扩展名\n" +
                     "4. 路径使用正斜杠分隔(如 src/main.py)\n" +
                     "5. 严格输出JSON(不要markdown代码块, 不要其他文字): {\"files\": [{\"path\": \"相对路径\", \"purpose\": \"一句话用途\"}]}";

        string response;
        try
        {
            // 清单规划简单, 走低成本模型
            response = await _llm.ChatAsync(prompt,
                systemPrompt: $"你是资深 {languageName} 项目规划师, 负责拆分输出文件。只输出JSON。",
                model: _config.LowCostModel,
                temperature: 0.3);
        }
        catch (Exception ex)
        {
            _blackboard.AddObservation(taskId, $"文件清单生成失败: {ex.Message}", Name);
            return new();
        }

        var plan = ParseFilePlan(response);
        _blackboard.AddObservation(taskId, $"文件清单: {plan.Count}个文件", Name);
        return plan.Take(10).ToList();
    }

    /// <summary>解析文件清单JSON: {"files": [{"path","purpose"}]}, 失败返回空列表</summary>
    private static List<(string Path, string Purpose)> ParseFilePlan(string response)
    {
        var result = new List<(string, string)>();
        try
        {
            var json = StripCodeFence(response).Trim();
            var start = json.IndexOf('{');
            var end = json.LastIndexOf('}');
            if (start < 0 || end <= start) return result;
            json = json[start..(end + 1)];

            var obj = Newtonsoft.Json.Linq.JObject.Parse(json);
            var arr = obj["files"] as Newtonsoft.Json.Linq.JArray;
            if (arr == null) return result;

            foreach (var item in arr)
            {
                var path = item["path"]?.ToString().Trim();
                if (string.IsNullOrWhiteSpace(path)) continue;
                var purpose = item["purpose"]?.ToString().Trim() ?? "";
                result.Add((path, purpose));
            }
        }
        catch { return result; }
        return result;
    }

    /// <summary>为单个文件生成完整内容(阶段2 / 单文件模式共用)</summary>
    private async Task<string?> GenerateSingleFileContentAsync(string description, string summary,
        string previousOutput, string languageName, string targetPath, string? purpose,
        List<(string Path, string Purpose)>? plan = null)
    {
        var prompt = $"## 任务描述\n{Truncate(description, 1500)}\n" +
                     (string.IsNullOrWhiteSpace(summary) ? "" : $"\n## 黑板上下文\n{Truncate(summary, 1200)}\n") +
                     (string.IsNullOrWhiteSpace(previousOutput) ? "" : $"\n## 上一阶段产出\n{Truncate(previousOutput, 1200)}\n");

        if (plan is { Count: > 1 })
        {
            prompt += "\n## 本次要生成的全部文件(供你了解整体结构, 只写当前文件)\n";
            foreach (var (p, pur) in plan)
                prompt += $"  - {p} : {pur}\n";
        }

        prompt += $"\n## 当前要生成的文件\n{targetPath}" +
                  (string.IsNullOrWhiteSpace(purpose) ? "" : $"\n用途: {purpose}") +
                  $"\n\n请生成该文件的完整{languageName}内容。只输出内容本身, 不要markdown代码块标记, 不要解释。";

        try
        {
            // 代码内容走高性能模型(reasoner), 质量更好
            var code = await _llm.ChatAsync(prompt,
                systemPrompt: $"你是一个资深 {languageName} 工程师, 生成可直接运行的高质量代码。",
                model: _config.HighPerformanceModel);
            return StripCodeFence(code);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>写文件 + SQLite快照 + Journal + Blackboard, 返回带 ModifiedFiles 的结果</summary>
    private async Task<AgentResult> WriteGeneratedFileAsync(string taskId, string path,
        string content, string reason)
    {
        var before = File.Exists(path) ? await File.ReadAllTextAsync(path) : "";
        await File.WriteAllTextAsync(path, content);
        await _fileHistory.RecordWriteAsync(taskId, Name, path, before, content, reason);

        _journal.LogFileWrite(Name, path, before, content);
        _blackboard.AddObservation(taskId, $"生成文件: {path}", Name);

        return AgentResult.Ok(Name, "", $"已生成: {path} ({content.Split('\n').Length}行)",
            new List<string> { path });
    }

    /// <summary>
    /// 修复模式 —— 补丁式修复: LLM只输出最小修改点(查找/替换片段), CodeAgent精准应用后写回(SQLite快照)。
    /// 不要求LLM重写完整文件, 推理短、速度快、大文件也适用。
    /// </summary>
    private async Task<AgentResult> FixCode(Dictionary<string, string> context, string taskId,
        IEnumerable<BlackboardEntry> blackboard)
    {
        var workspaceRoot = context.GetValueOrDefault("workspaceRoot", _workspaceRoot);
        var language = LanguageDetector.Detect(workspaceRoot);
        var languageName = GetLanguageName(language);

        var description = context.GetValueOrDefault("description", "");
        if (string.IsNullOrWhiteSpace(description))
            description = context.GetValueOrDefault("input", "");

        // 1. 确定修复目标文件(最多3个)
        var targets = ResolveFixTargets(context, workspaceRoot, language).Take(3).ToList();
        if (targets.Count == 0)
            return AgentResult.Ok(Name, "", "修复跳过: 工作区中未找到可修复的源码文件");

        if (!_llm.IsConfigured)
            return AgentResult.Ok(Name, "", "修复跳过: 未配置 DeepSeek API Key (请在设置页填写)");

        var summary = _blackboard.GetSummary(taskId);
        var modified = new List<string>();
        var skipped = new List<string>();

        // 2. 逐文件补丁修复
        foreach (var file in targets)
        {
            var rel = Path.GetRelativePath(workspaceRoot, file);
            if (IsSystemProtectedFile(file))
            {
                skipped.Add($"{rel}(系统保护)");
                continue;
            }

            // 读取文件(单文件上限30000字符, 补丁模式不需要完整重写)
            string content;
            try
            {
                content = await File.ReadAllTextAsync(file);
            }
            catch (Exception ex)
            {
                skipped.Add($"{rel}(读取失败: {ex.Message})");
                continue;
            }
            if (content.Length > 30000)
                content = content[..30000] + "\n# ... (内容过长已截断, 超出部分未显示)";

            _blackboard.AddObservation(taskId, $"开始修复: {rel}", Name);

            // 补丁式prompt: LLM只输出需要修改的片段
            var prompt = $"## 用户报告\n{description}\n\n" +
                         (string.IsNullOrWhiteSpace(summary) ? "" : $"## 前序分析\n{Truncate(summary, 1500)}\n\n") +
                         $"## 待修复文件({languageName})\n### {rel} ###\n{content}\n\n" +
                         "## 要求\n1. 诊断该文件中导致项目无法运行的问题\n" +
                         "2. 输出最小修改点: 每个修改点包含原文片段(old, 必须逐字来自上面文件内容, 用足够上下文确保唯一)和替换内容(new)\n" +
                         "3. 若文件无需修改, 输出空edits数组\n" +
                         "4. 严格输出JSON(不要任何其他文字): {\"edits\": [{\"old\": \"原文片段\", \"new\": \"替换内容\", \"reason\": \"修改原因\"}]}";

            string response;
            try
            {
                // 修复任务用低成本模型(flash); 单文件补丁推理耗时较长, 超时放宽到300s
                response = await _llm.ChatAsync(prompt,
                    systemPrompt: $"你是资深 {languageName} 工程师,擅长诊断并修复代码运行问题。只输出JSON。",
                    model: _config.LowCostModel,
                    timeoutSeconds: 300);
            }
            catch (Exception ex)
            {
                skipped.Add($"{rel}(LLM调用失败: {ex.Message})");
                continue;
            }

            // 解析补丁并应用
            var edits = ParseEdits(response);
            if (edits.Count == 0)
            {
                skipped.Add($"{rel}(LLM未输出有效修改或输出无法解析)");
                continue;
            }

            var (newContent, appliedReasons) = ApplyEdits(content, edits);
            if (string.Equals(newContent, content, StringComparison.Ordinal))
            {
                skipped.Add($"{rel}(补丁未产生变化)");
                continue;
            }

            // 写回 + SQLite快照
            await File.WriteAllTextAsync(file, newContent);
            await _fileHistory.RecordWriteAsync(taskId, Name, file, content, newContent,
                string.Join("; ", appliedReasons));

            _journal.LogFileWrite(Name, file, content, newContent);
            _blackboard.AddObservation(taskId,
                $"修复: {rel} ({string.Join("; ", appliedReasons)})", Name);
            modified.Add(file);
        }

        if (modified.Count == 0)
            return AgentResult.Ok(Name, "",
                $"修复未完成: 共{targets.Count}个目标, 均未修改{(skipped.Count > 0 ? $" [{string.Join("; ", skipped)}]" : "")}");

        return AgentResult.Ok(Name, "",
            $"已修复 {modified.Count}/{targets.Count} 个文件: {string.Join(", ", modified.Select(m => Path.GetRelativePath(workspaceRoot, m)))}{(skipped.Count > 0 ? $" 未处理: {string.Join("; ", skipped)}" : "")}",
            modified);
    }

    private sealed class CodeEdit
    {
        public string Old { get; set; } = "";
        public string New { get; set; } = "";
        public string Reason { get; set; } = "";
    }

    /// <summary>解析LLM补丁JSON: {"edits": [{"old","new","reason"}]}, 失败返回空列表</summary>
    private static List<CodeEdit> ParseEdits(string response)
    {
        var result = new List<CodeEdit>();
        try
        {
            var json = StripCodeFence(response).Trim();
            var start = json.IndexOf('{');
            var end = json.LastIndexOf('}');
            if (start < 0 || end <= start) return result;
            json = json[start..(end + 1)];

            var obj = Newtonsoft.Json.Linq.JObject.Parse(json);
            var arr = obj["edits"] as Newtonsoft.Json.Linq.JArray;
            if (arr == null) return result;

            foreach (var item in arr)
            {
                var old = item["old"]?.ToString() ?? "";
                var new_ = item["new"]?.ToString() ?? "";
                var reason = item["reason"]?.ToString() ?? "";
                if (string.IsNullOrWhiteSpace(old)) continue;
                result.Add(new CodeEdit { Old = old, New = new_, Reason = reason });
            }
        }
        catch
        {
            return result;
        }
        return result;
    }

    /// <summary>应用补丁: 在原文中查找old片段并替换为new。未匹配的补丁跳过并记录原因。</summary>
    private static (string Content, List<string> AppliedReasons) ApplyEdits(string original,
        List<CodeEdit> edits)
    {
        var result = original;
        var applied = new List<string>();
        foreach (var edit in edits)
        {
            if (!result.Contains(edit.Old, StringComparison.Ordinal))
            {
                applied.Add($"未找到匹配片段(跳过): {edit.Reason}");
                continue;
            }
            result = result.Replace(edit.Old, edit.New);
            applied.Add(string.IsNullOrWhiteSpace(edit.Reason) ? "修复" : edit.Reason);
        }
        return (result, applied);
    }

    /// <summary>确定修复目标文件: 显式path > 输入中的文件路径(含相对路径/文件名) > 项目源码文件(优先入口)</summary>
    private static List<string> ResolveFixTargets(Dictionary<string, string> context,
        string workspaceRoot, ProjectLanguage language)
    {
        var result = new List<string>();

        var explicitPath = context.GetValueOrDefault("path", "");
        if (!string.IsNullOrEmpty(explicitPath) && File.Exists(explicitPath))
            return new List<string> { explicitPath };

        // 从输入中定位文件(绝对路径/工作区相对路径/文件名, 如 "修复 login.py")
        var input = context.GetValueOrDefault("input", "");
        var extracted = PathMapper.LocateExisting(input, workspaceRoot);
        if (!string.IsNullOrEmpty(extracted) && File.Exists(extracted))
            return new List<string> { extracted };

        // 项目源码文件: 优先入口文件, 再取其他(最多5个)
        var files = EnumerateSourceFiles(workspaceRoot, language).ToList();
        var entryNames = new[] { "main", "__main__", "app", "server", "run", "api_server", "program" };
        var entries = files
            .Where(f => entryNames.Any(n => Path.GetFileNameWithoutExtension(f).Equals(n, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        result.AddRange(entries.Take(3));
        foreach (var f in files)
        {
            if (result.Count >= 5) break;
            if (!result.Contains(f)) result.Add(f);
        }
        return result;
    }

    private static string GetLanguageName(ProjectLanguage language) => language switch
    {
        ProjectLanguage.Python => "Python",
        ProjectLanguage.CSharp => "C#",
        ProjectLanguage.JavaScript => "JavaScript",
        ProjectLanguage.TypeScript => "TypeScript",
        ProjectLanguage.Java => "Java",
        ProjectLanguage.Go => "Go",
        _ => "通用"
    };

    private static string Truncate(string s, int max)
        => s.Length > max ? s[..max] + "\n...(截断)" : s;

    /// <summary>去除LLM输出可能附带的markdown代码块标记</summary>
    private static string StripCodeFence(string code)
    {
        var trimmed = code.Trim();
        if (!trimmed.StartsWith("```")) return code;

        var lines = trimmed.Split('\n').ToList();
        if (lines.Count > 0 && lines[0].TrimStart().StartsWith("```"))
            lines.RemoveAt(0);
        if (lines.Count > 0 && lines[^1].Trim() == "```")
            lines.RemoveAt(lines.Count - 1);
        return string.Join('\n', lines);
    }

    /// <summary>
    /// 系统保护文件检查 —— 禁止 Agent 覆盖 Floating Mind 自身的关键源文件
    /// </summary>
    private static bool IsSystemProtectedFile(string path)
    {
        var fileName = Path.GetFileName(path).ToLowerInvariant();
        var protectedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Generated.cs",          // Agent 输出冲突文件
            "MainWindow.xaml.cs",    // UI入口
            "App.xaml.cs",           // 应用入口
            "MainWindow.xaml",       // XAML定义
            "App.xaml",              // 应用定义
            "FloatingMind.csproj",   // 项目文件
            "Program.cs",            // 程序入口
        };
        return protectedFiles.Contains(fileName);
    }

    private async Task<AgentResult> RefactorCode(Dictionary<string, string> context, string taskId)
    {
        var workspaceRoot = context.GetValueOrDefault("workspaceRoot", _workspaceRoot);

        // 定位重构目标: 显式path > 输入中的文件路径 > 项目入口文件
        var path = context.GetValueOrDefault("path", "");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            path = PathMapper.LocateExisting(context.GetValueOrDefault("input", ""), workspaceRoot);

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            var entry = EnumerateSourceFiles(workspaceRoot, LanguageDetector.Detect(workspaceRoot))
                .OrderBy(f => new[] { "main", "__main__", "app", "program" }
                    .Contains(Path.GetFileNameWithoutExtension(f)) ? 0 : 1)
                .FirstOrDefault();
            path = entry ?? "";
        }

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return AgentResult.Fail(Name, "", $"目标文件不存在或无法定位: {path}");

        if (IsSystemProtectedFile(path))
        {
            return AgentResult.Ok(Name, "",
                $"重构跳过: 目标文件 '{path}' 属于 Floating Mind 系统保护文件, 不允许修改。");
        }

        var languageName = GetLanguageName(LanguageDetector.Detect(workspaceRoot));
        var content = await File.ReadAllTextAsync(path);

        if (!_llm.IsConfigured)
            return AgentResult.Ok(Name, "",
                "重构跳过: 未配置 DeepSeek API Key (请在设置页填写)");

        string refactored;
        try
        {
            refactored = await _llm.ChatAsync(
                $"请重构以下 {languageName} 代码,改善可读性和结构但不改变行为。只输出重构后的完整代码:\n\n{Truncate(content, 6000)}",
                systemPrompt: $"你是资深 {languageName} 重构专家,只输出代码本身,不要markdown标记。",
                model: _config.HighPerformanceModel);
            refactored = StripCodeFence(refactored);
        }
        catch (Exception ex)
        {
            return AgentResult.Ok(Name, "", $"重构跳过: LLM调用失败 - {ex.Message}");
        }

        await File.WriteAllTextAsync(path, refactored);
        await _fileHistory.RecordWriteAsync(taskId, Name, path, content, refactored, "重构");

        _journal.LogFileWrite(Name, path, content, refactored);
        _blackboard.AddObservation(taskId, $"重构: {path}", Name);

        return AgentResult.Ok(Name, "", $"重构: {path}", new List<string> { path });
    }

    private async Task<AgentResult> ReviewCode(Dictionary<string, string> context)
    {
        var workspaceRoot = context.GetValueOrDefault("workspaceRoot", _workspaceRoot);
        var languageName = GetLanguageName(LanguageDetector.Detect(workspaceRoot));

        // === 定位审查目标(修复"检查Agent无法定位路径"): 显式path > 输入中的文件 > 前序修改文件 > 入口文件 ===
        var path = context.GetValueOrDefault("path", "");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            path = PathMapper.LocateExisting(context.GetValueOrDefault("target", ""), workspaceRoot);

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            path = PathMapper.LocateExisting(context.GetValueOrDefault("input", ""), workspaceRoot);

        if ((string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            && context.TryGetValue("modifiedFiles", out var modifiedFiles))
        {
            path = modifiedFiles.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault(f => File.Exists(f)) ?? "";
        }

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            // 降级: 定位不到具体文件时审查项目入口文件, 仍无则跳过
            path = EnumerateSourceFiles(workspaceRoot, LanguageDetector.Detect(workspaceRoot))
                .OrderBy(f => new[] { "main", "__main__", "app", "server", "run", "program" }
                    .Contains(Path.GetFileNameWithoutExtension(f)) ? 0 : 1)
                .FirstOrDefault() ?? "";
        }

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            var reason = string.IsNullOrWhiteSpace(path)
                ? "未指定目标文件(工作区无源码可审查)"
                : $"目标文件不存在: {path}";
            return AgentResult.Ok(Name, "",
                $"代码审查跳过: {reason}。如需审查请在节点参数中提供 path。");
        }

        var content = File.ReadAllText(path);
        var lines = content.Split('\n').Length;
        var issues = new List<string>();

        if (content.Contains("TODO")) issues.Add("发现 TODO 标记");
        if (lines > 500) issues.Add("文件过长(>500行)");
        if (content.Contains("catch { }")) issues.Add("空 catch 块");
        if (content.Contains("except:") || content.Contains("except Exception: pass")) issues.Add("宽泛/空异常处理");

        var report = $"=== Code Review: {Path.GetFileName(path)} ===\n"
            + $"行数: {lines}\n"
            + (issues.Count > 0
                ? $"静态检查:\n{string.Join("\n", issues.Select(i => $"  ⚠ {i}"))}\n"
                : "静态检查: ✓ 未发现明显问题\n");

        // 已配置API Key时追加LLM深度审查(低成本模型)
        if (_llm.IsConfigured)
        {
            try
            {
                var llmReview = await _llm.ChatAsync(
                    $"请审查以下 {languageName} 代码,指出潜在bug、可维护性问题和改进建议,用中文简洁列出:\n\n{Truncate(content, 6000)}",
                    systemPrompt: "你是严格的代码审查员,只报告有实际价值的问题。");
                report += $"\nLLM审查:\n{llmReview}";
            }
            catch (Exception ex)
            {
                report += $"\nLLM审查不可用: {ex.Message}";
            }
        }

        // === 审查报告落盘: 输出文件放到工作区内, 路径可回溯(满足"输出文件放在该放的位置") ===
        // 注意: userInput 传空串, 避免被审查的文件名(如 login.py)被当作输出候选而覆盖原文件
        try
        {
            var reportPath = PathMapper.ResolveOutputPath(null, "", workspaceRoot,
                $"审查报告_{Path.GetFileNameWithoutExtension(path)}_{DateTime.Now:yyyyMMdd_HHmmss}.md",
                out var rErr);
            if (reportPath != null)
            {
                var taskId = context.GetValueOrDefault("taskId", "");
                await File.WriteAllTextAsync(reportPath, report);
                await _fileHistory.RecordWriteAsync(taskId, Name, reportPath, "", report, "代码审查报告");
                _journal.LogFileWrite(Name, reportPath, "", report);
                report += $"\n\n[审查报告已保存] {reportPath}";
            }
        }
        catch { /* 报告落盘失败不影响审查结论 */ }

        return AgentResult.Ok(Name, "", report);
    }

    public Task<bool> RollbackAsync(string nodeId) => Task.FromResult(true);
}
