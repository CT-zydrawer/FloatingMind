using FloatingMind.Interfaces;
using FloatingMind.Models.Agent;
using FloatingMind.Models.Blackboard;
using FloatingMind.Models.Workflow;

namespace FloatingMind.Services;

/// <summary>
/// 7. Validator系统 —— 检查体系(不是Agent)
/// 三个阶段: Operation / Stage / Acceptance
/// </summary>
public class ValidatorChain
{
    private readonly List<IValidator> _validators = new();
    private readonly EventBus _eventBus;
    private readonly JournalSystem _journal;

    public ValidatorChain(EventBus eventBus, JournalSystem journal)
    {
        _eventBus = eventBus;
        _journal = journal;
        // 注册内置Validator
        _validators.Add(new OperationValidator());
        _validators.Add(new StageValidator());
        _validators.Add(new AcceptanceValidator());
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
        List<BlackboardEntry> blackboard)
    {
        var report = new ValidationReport();
        var acceptance = _validators.OfType<AcceptanceValidator>().FirstOrDefault();
        if (acceptance != null)
        {
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
        // 7.1 Pre Validator: 检查计划合理性和依赖完整性
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
        _journal.LogValidation("PreValidator", "Pass", "计划合理性检查通过");
        return true;
    }
}

/// <summary>
/// 7.2 Operation Validator —— 每次操作检查
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

        // 检查是否有非法文件操作
        foreach (var file in result.ModifiedFiles)
        {
            if (file.Contains(".."))  // 路径穿越
                issues.Add($"可疑路径: {file}");
        }

        bool passed = issues.Count == 0;
        return Task.FromResult((passed,
            passed ? "操作校验通过" : $"操作校验失败: {string.Join("; ", issues)}",
            issues));
    }
}

/// <summary>
/// 7.3 Stage Validator —— 阶段完成检查
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

        // 只要成功执行就算有产出（Output或ModifiedFiles二选一即可）
        // 只有明确返回 Success=true 但完全无产出才告警
        if (result.Success && string.IsNullOrEmpty(result.Output) && result.ModifiedFiles.Count == 0)
            issues.Add($"阶段[{node.Label}]成功但无文本产出和文件修改");

        bool passed = issues.Count == 0;
        return Task.FromResult((passed,
            passed ? $"阶段[{node.Label}]校验通过" : $"阶段[{node.Label}]校验失败",
            issues));
    }
}

/// <summary>
/// 7.4 Acceptance Validator —— 最终检查
/// </summary>
public class AcceptanceValidator : IValidator
{
    public string Name => "AcceptanceValidator";

    public Task<(bool Passed, string Reason, List<string> Issues)> ValidateAsync(
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

        bool passed = issues.Count == 0;
        return Task.FromResult((passed,
            passed ? "验收通过 ✓" : "验收失败 ✗",
            issues));
    }
}

public class ValidationReport
{
    public bool Failed { get; set; }
    public List<ValidationStep> Steps { get; } = new();

    public void Add(string validator, bool passed, string reason, List<string> issues)
        => Steps.Add(new ValidationStep(validator, passed, reason, issues));

    public record ValidationStep(string Validator, bool Passed, string Reason, List<string> Issues);
}
