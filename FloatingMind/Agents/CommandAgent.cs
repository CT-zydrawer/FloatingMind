using FloatingMind.Interfaces;
using FloatingMind.Models.Agent;
using FloatingMind.Models.Blackboard;
using FloatingMind.Models.Command;
using FloatingMind.Models.Workflow;
using FloatingMind.Services;
using FloatingMind.Models.Config;
using System.Diagnostics;

namespace FloatingMind.Agents;

/// <summary>
/// Command Agent —— 命令执行Agent
/// </summary>
public class CommandAgent : IAgent
{
    private readonly BlackboardSystem _blackboard;
    private readonly JournalSystem _journal;
    private readonly CommandSafetyService _cmdSafety;
    private readonly DeepSeekService _llm;
    private readonly AppConfig _config;
    private readonly Action<string, CommandRiskLevel>? _onConfirm;

    public string Name => "CommandAgent";
    public string Description => "命令执行(构建/测试/验证)";

    // 需用户确认的命令
    private readonly Queue<(string Command, TaskCompletionSource<bool> Tcs)> _pendingConfirmations = new();

    public CommandAgent(BlackboardSystem blackboard, JournalSystem journal,
        CommandSafetyService cmdSafety, DeepSeekService llm, AppConfig config,
        Action<string, CommandRiskLevel>? onConfirm = null)
    {
        _blackboard = blackboard;
        _journal = journal;
        _cmdSafety = cmdSafety;
        _llm = llm;
        _config = config;
        _onConfirm = onConfirm;
    }

    public Task<DiscoveryOutput> DiscoveryAsync(WorkflowNode node, Dictionary<string, string> context)
    {
        var deps = new List<string> { "Shell", "BuildTools" };
        var files = new List<string>();

        _blackboard.AddObservation(context.GetValueOrDefault("taskId", ""),
            "命令执行环境就绪", Name);

        return Task.FromResult(DiscoveryOutput.FromResult(files, deps));
    }

    public async Task<AgentResult> ExecuteAsync(WorkflowNode node, Dictionary<string, string> context,
        IEnumerable<BlackboardEntry> blackboard)
    {
        var taskId = context.GetValueOrDefault("taskId", "");
        var action = context.GetValueOrDefault("action", "exec");
        var workspaceRoot = context.GetValueOrDefault("workspaceRoot", "");

        // 验证动作: LLM生成验证命令 + 语法保底, 在任务工作区执行
        if (action == "verify")
            return await VerifyCode(context, taskId, workspaceRoot);

        var command = context.GetValueOrDefault("command", "");
        if (string.IsNullOrEmpty(command))
            return AgentResult.Ok(Name, node.Id, "无命令执行");

        // 安全检查
        var safety = _cmdSafety.Analyze(command);

        switch (safety.Level)
        {
            case CommandRiskLevel.L3_Forbidden:
                _journal.LogAgentAction(Name, "Forbidden", $"禁止执行: {command} - {safety.Reason}");
                return AgentResult.Fail(Name, node.Id, $"禁止执行: {safety.Reason}");

            case CommandRiskLevel.L2_Confirm:
                if (!await RequestConfirmation(command, safety))
                    return AgentResult.Fail(Name, node.Id, $"用户拒绝执行: {command}");
                break;

            case CommandRiskLevel.L1_Log:
                _journal.LogCommand(Name, command, $"记录执行 (L1): {safety.Reason}");
                break;
        }

        // 执行
        try
        {
            var result = await ExecuteCommandAsync(command, workspaceRoot);
            _journal.LogCommand(Name, command, result);
            _blackboard.AddObservation(taskId, $"命令执行: {command}", Name);

            return AgentResult.Ok(Name, node.Id, result,
                new List<string> { command });
        }
        catch (Exception ex)
        {
            _journal.LogAgentAction(Name, "CommandFailed", ex.Message);
            return AgentResult.Fail(Name, node.Id, ex.Message);
        }
    }

    /// <summary>
    /// 验证模式 —— LLM根据修复内容生成针对性验证命令, 并附加语法级保底检查。
    /// 全部命令在任务工作区目录下执行, 危险命令(L2+)仍需用户确认。
    /// </summary>
    private async Task<AgentResult> VerifyCode(Dictionary<string, string> context, string taskId,
        string workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot) || !Directory.Exists(workspaceRoot))
            return AgentResult.Ok(Name, "", "验证跳过: 未指定有效的任务工作区");

        var language = LanguageDetector.Detect(workspaceRoot);
        var modifiedFiles = context.GetValueOrDefault("modifiedFiles", "")
            .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .ToList();
        var description = context.GetValueOrDefault("input", "");

        var commands = new List<string>();

        // === 保底: 语法/编译检查(始终执行) ===
        var syntaxCmd = LanguageDetector.GetSyntaxCheckCommand(language);
        if (!string.IsNullOrEmpty(syntaxCmd))
        {
            if (language == ProjectLanguage.Python && modifiedFiles.Count > 0)
            {
                var fileArgs = string.Join(" ", modifiedFiles.Select(f => $"\"{Path.GetFileName(f)}\""));
                commands.Add($"{syntaxCmd} {fileArgs}");
            }
            else
            {
                commands.Add(syntaxCmd);
            }
        }

        // === LLM生成针对性验证命令 ===
        if (_llm.IsConfigured)
        {
            try
            {
                var llmCommands = await GenerateVerifyCommands(description, modifiedFiles, language);
                commands.AddRange(llmCommands.Where(c => !commands.Contains(c)));
            }
            catch (Exception ex)
            {
                _blackboard.AddObservation(taskId, $"LLM验证命令生成失败: {ex.Message}", Name);
            }
        }

        if (commands.Count == 0)
            return AgentResult.Ok(Name, "", "验证跳过: 没有可执行的验证命令");

        // === 执行(逐个, 最多5条; 危险命令走确认) ===
        var report = new System.Text.StringBuilder();
        report.AppendLine($"=== 验证 (语言: {language}) ===");
        var executed = new List<string>();
        foreach (var cmd in commands.Distinct().Take(5))
        {
            var safety = _cmdSafety.Analyze(cmd);
            if (safety.Level == CommandRiskLevel.L3_Forbidden)
            {
                report.AppendLine($"> {cmd}\n[跳过] 禁止执行: {safety.Reason}");
                continue;
            }
            if (safety.Level == CommandRiskLevel.L2_Confirm && !await RequestConfirmation(cmd, safety))
            {
                report.AppendLine($"> {cmd}\n[跳过] 用户拒绝执行");
                continue;
            }

            _journal.LogCommand(Name, cmd, "验证命令执行");
            try
            {
                var output = await ExecuteCommandAsync(cmd, workspaceRoot);
                executed.Add(cmd);
                report.AppendLine($"> {cmd}\n{Truncate(output, 1500)}");
            }
            catch (Exception ex)
            {
                report.AppendLine($"> {cmd}\n[失败] {ex.Message}");
            }
        }

        _blackboard.AddObservation(taskId,
            $"验证完成: 执行{executed.Count}/{commands.Take(5).Count()}条命令", Name);

        return AgentResult.Ok(Name, "", report.ToString(), executed);
    }

    /// <summary>LLM根据修复内容生成验证命令(JSON数组), 限制为安全的验证类操作</summary>
    private async Task<List<string>> GenerateVerifyCommands(string description,
        List<string> modifiedFiles, ProjectLanguage language)
    {
        var result = new List<string>();
        var langName = language switch
        {
            ProjectLanguage.Python => "Python",
            ProjectLanguage.CSharp => "C#",
            _ => "未知"
        };
        var files = modifiedFiles.Count > 0
            ? string.Join(", ", modifiedFiles.Select(f => Path.GetFileName(f)))
            : "(无, 可检查整个项目)";

        var prompt = $"## 项目语言\n{langName}\n" +
                     $"## 修改的文件\n{files}\n" +
                     $"## 任务描述\n{Truncate(description, 800)}\n\n" +
                     "请给出验证修复是否成功的命令列表(最多3条)。\n" +
                     "要求:\n" +
                     "- 只允许安全的验证类命令: 语法/编译检查、单元测试、导入测试、短时运行并退出\n" +
                     "- 禁止: 安装/卸载依赖、删除文件、启动长驻服务、网络操作\n" +
                     "- Python 示例: python -m py_compile xxx.py; python -c \"import xxx\"\n" +
                     "- 严格输出JSON: {\"commands\": [\"命令1\", \"命令2\"]}, 不要其他文字";

        var response = await _llm.ChatAsync(prompt,
            systemPrompt: "你是构建验证专家, 输出安全的验证命令JSON。",
            model: _config.LowCostModel);

        try
        {
            var json = response.Trim();
            var start = json.IndexOf('{');
            var end = json.LastIndexOf('}');
            if (start >= 0 && end > start) json = json[start..(end + 1)];
            var obj = Newtonsoft.Json.Linq.JObject.Parse(json);
            var arr = obj["commands"] as Newtonsoft.Json.Linq.JArray;
            if (arr != null)
                foreach (var item in arr)
                {
                    var cmd = item?.ToString().Trim();
                    if (!string.IsNullOrEmpty(cmd)) result.Add(cmd);
                }
        }
        catch { /* 解析失败返回空, 由调用方忽略 */ }

        return result;
    }

    private async Task<string> ExecuteCommandAsync(string command, string workspaceRoot)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c {command}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        // 在任务工作区目录下执行, 使相对路径/项目命令生效
        if (!string.IsNullOrWhiteSpace(workspaceRoot) && Directory.Exists(workspaceRoot))
            psi.WorkingDirectory = workspaceRoot;

        using var process = Process.Start(psi);
        if (process == null) return "无法启动进程";

        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Exit Code: {process.ExitCode}");
        if (!string.IsNullOrEmpty(output)) sb.AppendLine($"Output:\n{output}");
        if (!string.IsNullOrEmpty(error)) sb.AppendLine($"Error:\n{error}");

        return sb.ToString();
    }

    private static string Truncate(string s, int max)
        => s.Length > max ? s[..max] + "\n...(截断)" : s;

    private async Task<bool> RequestConfirmation(string command, CommandSafetyResult safety)
    {
        _journal.LogAgentAction(Name, "ConfirmPending", $"{command} ({safety.Reason})");

        if (_onConfirm != null)
        {
            _onConfirm(command, safety.Level);
            var tcs = new TaskCompletionSource<bool>();
            _pendingConfirmations.Enqueue((command, tcs));
            return await tcs.Task;
        }

        // 无人确认默认拒绝
        return false;
    }

    public void ProvideConfirmation(bool approved)
    {
        if (_pendingConfirmations.TryDequeue(out var item))
            item.Tcs.SetResult(approved);
    }

    public Task<bool> RollbackAsync(string nodeId) => Task.FromResult(true);
}
