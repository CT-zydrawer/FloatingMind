using System.Text.RegularExpressions;
using FloatingMind.Interfaces;
using FloatingMind.Models.Agent;
using FloatingMind.Models.Blackboard;
using FloatingMind.Models.Workflow;
using FloatingMind.Services.LLM;

namespace FloatingMind.Services;

/// <summary>
/// 7. Validator系统 —— 检查体系(不是Agent)
/// 四个阶段: Pre / Operation / Stage / Acceptance
/// 闭环验证(需求2): Operation校验文件真实性与保护边界; Stage校验Verify节点退出码与产出;
/// Acceptance用用户原始目标做LLM需求审查(设计文档7.4), LLM不可用时回退规则检查。
/// </summary>
public class ValidatorChain
{
    private readonly List<IValidator> _validators = new();
    private readonly EventBus _eventBus;
    private readonly JournalSystem _journal;

    public ValidatorChain(EventBus eventBus, JournalSystem journal,
        DeepSeekService? llm = null, PromptBuilder? promptBuilder = null)
    {
        _eventBus = eventBus;
        _journal = journal;
        // 注册内置Validator (Acceptance持有LLM引用, 用于最终需求审查)
        _validators.Add(new OperationValidator());
        _validators.Add(new StageValidator());
        _validators.Add(new AcceptanceValidator(llm, promptBuilder));
    }

    public async Task<ValidationReport> ValidatePerNode(WorkflowNode node, AgentResult result,
        List<BlackboardEntry> blackboard)
    {
        var report = new ValidationReport();
        // 每次节点只运行前两个Validator（不包含Acceptance）
        var perNodeValidators = _validators.Where(v => v is not AcceptanceValidator);
        foreach (var validator in perNodeValidators)
        {
            var (passed, reason, issues) = await validator.ValidateAsync(node, result, blackboard);
            report.Add(validator.Name, passed, reason, issues);
            _eventBus.Publish("Validation", new { Validator = validator.Name, Passed = passed, Reason = reason });
            _journal.LogValidation(validator.Name, passed ? "Pass" : "Fail", reason);

            if (!passed)
            {
                report.Failed = true;
                break;
            }
        }
        return report;
    }

    public async Task<ValidationReport> ValidateFinalAcceptance(WorkflowNode lastNode, AgentResult lastResult,
        List<BlackboardEntry> blackboard, string? originalInput = null)
    {
        var report = new ValidationReport();
        var acceptance = _validators.OfType<AcceptanceValidator>().FirstOrDefault();
        if (acceptance != null)
        {
            // 传入用户原始目标, 供LLM需求审查
            acceptance.OriginalGoal = originalInput ?? string.Empty;
            var (passed, reason, issues) = await acceptance.ValidateAsync(lastNode, lastResult, blackboard);
            report.Add(acceptance.Name, passed, reason, issues);
            _eventBus.Publish("Validation", new { Validator = acceptance.Name, Passed = passed, Reason = reason });
            _journal.LogValidation(acceptance.Name, passed ? "Pass" : "Fail", reason);
            if (!passed) report.Failed = true;
        }
        return report;
    }

    public async Task<bool> ValidatePreExecution(WorkflowDef workflow)
    {
        // 7.1 Pre Validator: 检查计划合理性、依赖完整性、边引用有效性
        if (workflow.Nodes.Count == 0)
        {
            _journal.LogValidation("PreValidator", "Fail", "Workflow没有节点");
            return false;
        }
        if (workflow.Edges.Count == 0 && workflow.Nodes.Count > 1)
        {
            _journal.LogValidation("PreValidator", "Fail", "多节点Workflow缺少边定义");
            return false;
        }
        // 边引用的源/目标节点必须存在
        foreach (var edge in workflow.Edges)
        {
            if (workflow.Nodes.All(n => n.Id != edge.Source))
            {
                _journal.LogValidation("PreValidator", "Fail", $"边源节点不存在: {edge.Source}");
                return false;
            }
            if (workflow.Nodes.All(n => n.Id != edge.Target))
            {
                _journal.LogValidation("PreValidator", "Fail", $"边目标节点不存在: {edge.Target}");
                return false;
            }
        }
        // 节点AgentType必须非空
        if (workflow.Nodes.Any(n => string.IsNullOrWhiteSpace(n.AgentType)))
        {
            _journal.LogValidation("PreValidator", "Fail", "存在未指定Agent类型的节点");
            return false;
        }
        _journal.LogValidation("PreValidator", "Pass", "计划合理性检查通过");
        return true;
    }
}

/// <summary>
/// 7.2 Operation Validator —— 每次操作检查(闭环: 文件真实性/保护边界/声明一致性)
/// </summary>
public class OperationValidator : IValidator
{
    public string Name => "OperationValidator";

    public Task<(bool Passed, string Reason, List<string> Issues)> ValidateAsync(
        WorkflowNode node, AgentResult result, List<BlackboardEntry> blackboard)
    {
        var issues = new List<string>();

        if (string.IsNullOrEmpty(result.AgentName))
            issues.Add("AgentName为空");

        if (!result.Success && string.IsNullOrEmpty(result.Error))
            issues.Add("Execution失败但Error为空");

        // 修改文件列表检查: 路径穿越 / 系统保护文件
        foreach (var file in result.ModifiedFiles)
        {
            if (file.Contains(".."))
                issues.Add($"可疑路径: {file}");
            if (PathMapper.IsSystemProtected(file))
                issues.Add($"修改系统保护文件: {file}");
        }

        // 文件操作类Agent(非CommandAgent): 声明的修改文件必须真实存在(闭环: 不许虚报产出)
        if (node.AgentType != "CommandAgent")
        {
            foreach (var file in result.ModifiedFiles)
            {
                if (!File.Exists(file))
                    issues.Add($"修改文件不存在: {file}");
            }
        }

        // 声明写操作但未产生任何文件修改 → 声明不一致
        var action = node.Parameters.GetValueOrDefault("action", "");
        if (result.Success && action is "write" or "generate" or "fix" or "refactor"
            && result.ModifiedFiles.Count == 0)
        {
            issues.Add($"声明操作[{action}]但未产生文件修改");
        }

        bool passed = issues.Count == 0;
        return Task.FromResult((passed,
            passed ? "操作校验通过" : $"操作校验失败: {string.Join("; ", issues)}",
            issues));
    }
}

/// <summary>
/// 7.3 Stage Validator —— 阶段完成检查(闭环: Verify节点退出码/产出文件非空)
/// </summary>
public class StageValidator : IValidator
{
    public string Name => "StageValidator";

    public Task<(bool Passed, string Reason, List<string> Issues)> ValidateAsync(
        WorkflowNode node, AgentResult result, List<BlackboardEntry> blackboard)
    {
        var issues = new List<string>();

        if (!result.Success)
            issues.Add($"阶段[{node.Label}]执行失败: {result.Error}");

        // Verify节点(CommandAgent verify): 校验所有命令退出码为0, 且无失败项
        if (node.AgentType == "CommandAgent" && result.Success && !string.IsNullOrEmpty(result.Output))
        {
            if (result.Output.Contains("[失败]"))
                issues.Add($"阶段[{node.Label}]存在验证失败项");

            foreach (Match m in Regex.Matches(result.Output, @"Exit Code: (-?\d+)"))
            {
                if (m.Groups[1].Value != "0")
                    issues.Add($"阶段[{node.Label}]验证命令退出码非0: {m.Groups[1].Value}");
            }
        }

        // 成功但完全无产出
        if (result.Success && string.IsNullOrEmpty(result.Output) && result.ModifiedFiles.Count == 0)
            issues.Add($"阶段[{node.Label}]成功但无文本产出和文件修改");

        // 文件操作类Agent: 产出文件必须存在且非空(闭环: 写空文件/写失败都要暴露)
        if (node.AgentType != "CommandAgent")
        {
            foreach (var file in result.ModifiedFiles)
            {
                try
                {
                    if (!File.Exists(file) || new FileInfo(file).Length == 0)
                        issues.Add($"产出文件缺失或为空: {file}");
                }
                catch { issues.Add($"产出文件不可读: {file}"); }
            }
        }

        bool passed = issues.Count == 0;
        return Task.FromResult((passed,
            passed ? $"阶段[{node.Label}]校验通过" : $"阶段[{node.Label}]校验失败",
            issues));
    }
}

/// <summary>
/// 7.4 Acceptance Validator —— 最终检查(闭环: 用用户原始目标做LLM需求审查, 规则兜底)
/// </summary>
public class AcceptanceValidator : IValidator
{
    private readonly DeepSeekService? _llm;
    private readonly PromptBuilder? _promptBuilder;

    /// <summary>用户原始目标(由 ValidatorChain.ValidateFinalAcceptance 注入)</summary>
    public string? OriginalGoal { get; set; }

    public string Name => "AcceptanceValidator";

    public AcceptanceValidator(DeepSeekService? llm = null, PromptBuilder? promptBuilder = null)
    {
        _llm = llm;
        _promptBuilder = promptBuilder;
    }

    public async Task<(bool Passed, string Reason, List<string> Issues)> ValidateAsync(
        WorkflowNode node, AgentResult result, List<BlackboardEntry> blackboard)
    {
        var issues = new List<string>();

        // 所有节点都必须成功完成
        if (!result.Success)
            issues.Add("最终节点执行失败");

        // 至少有一些产出
        bool hasOutput = !string.IsNullOrEmpty(result.Output) || result.ModifiedFiles.Count > 0;
        if (!hasOutput)
            issues.Add("没有最终产出");

        // 检查是否有未解决的Conflict
        var openConflicts = blackboard.OfType<ConflictEntry>().Count(c => c.Status == "Open");
        if (openConflicts > 0)
            issues.Add($"仍有{openConflicts}个未解决冲突");

        // === LLM需求审查(设计文档7.4: 输入用户原始目标, 输出Pass/Fail) ===
        // 仅在基础规则通过且配置了LLM与原始目标时执行, 控制成本(每任务一次)
        if (issues.Count == 0 && _llm != null && _llm.IsConfigured
            && !string.IsNullOrWhiteSpace(OriginalGoal))
        {
            try
            {
                var (llmPassed, reason, missing) = await AskLlmAcceptanceAsync(blackboard, result);
                if (!llmPassed)
                {
                    issues.Add($"需求未满足: {reason}" +
                               (missing.Count > 0 ? $" 缺失: {string.Join("; ", missing)}" : ""));
                }
            }
            catch (Exception ex)
            {
                // LLM验收失败不阻塞: 规则检查已通过, 记录原因
                issues.Add($"LLM验收不可用: {ex.Message}");
            }
        }

        bool passed = issues.Count == 0;
        return (passed,
            passed ? "验收通过 ✓" : "验收失败 ✗",
            issues);
    }

    /// <summary>LLM需求审查: 用户原始目标 vs 最终产出, 返回JSON解析结果</summary>
    private async Task<(bool Passed, string Reason, List<string> Missing)> AskLlmAcceptanceAsync(
        List<BlackboardEntry> blackboard, AgentResult result)
    {
        var goal = OriginalGoal ?? "";
        var files = result.ModifiedFiles.Count > 0
            ? string.Join(", ", result.ModifiedFiles)
            : "(无文件产出)";
        var summary = string.Join("\n", blackboard.Take(30).Select(b => $"  {b}"));
        var output = Truncate(result.Output, 2000);

        var prompt = $"## 用户原始目标\n{Truncate(goal, 1500)}\n\n" +
                     $"## 最终产出文件\n{files}\n\n" +
                     $"## 最终阶段输出\n{output}\n\n" +
                     $"## 任务过程摘要(Blackboard)\n{(summary.Length > 0 ? summary : "(空)")}\n\n" +
                     "请判断用户原始目标是否被满足。严格输出JSON(不要markdown代码块): " +
                     "{\"passed\": true/false, \"reason\": \"一句话原因\", \"missing\": [\"未满足的点\"]}";

        var response = await _llm!.ChatAsync(prompt,
            systemPrompt: "你是严格的验收评审员, 只报告有实际价值的问题。只输出JSON。",
            temperature: 0.2);

        try
        {
            var json = response.Trim();
            var start = json.IndexOf('{');
            var end = json.LastIndexOf('}');
            if (start >= 0 && end > start) json = json[start..(end + 1)];
            var obj = Newtonsoft.Json.Linq.JObject.Parse(json);
            var passed = (bool?)obj["passed"] ?? false;
            var reason = obj["reason"]?.ToString() ?? "需求未完全满足";
            var missing = new List<string>();
            if (obj["missing"] is Newtonsoft.Json.Linq.JArray arr)
                foreach (var item in arr) missing.Add(item.ToString());
            return (passed, reason, missing);
        }
        catch
        {
            return (false, "验收JSON解析失败", new());
        }
    }

    private static string Truncate(string s, int max)
        => s.Length > max ? s[..max] + "...(截断)" : s;
}

public class ValidationReport
{
    public bool Failed { get; set; }
    public List<ValidationStep> Steps { get; } = new();

    public void Add(string validator, bool passed, string reason, List<string> issues)
        => Steps.Add(new ValidationStep(validator, passed, reason, issues));

    public record ValidationStep(string Validator, bool Passed, string Reason, List<string> Issues);
}
