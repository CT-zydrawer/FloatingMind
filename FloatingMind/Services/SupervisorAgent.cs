using FloatingMind.Interfaces;
using FloatingMind.Models.Agent;
using FloatingMind.Models.Blackboard;
using FloatingMind.Models.Config;
using FloatingMind.Models.Workflow;
using System.Collections.Concurrent;

namespace FloatingMind.Services;

/// <summary>
/// 3.3 Supervisor Agent —— 系统总控制中心(不直接执行任务)
/// 职责: 管理Workflow、分配Agent、管理Blackboard、处理异常、向用户询问
/// 
/// 核心设计原则(来自系统文档):
/// - 成本可控: 信息不足的输入不盲目调度, 先向用户提问澄清
/// - 上下文最小化: Agent只获取完成任务所需的信息
/// - 过程透明: 明确度评分让用户知道系统如何理解输入
/// </summary>
public class SupervisorAgent
{
    private readonly IntentRouter _intentRouter;
    private readonly WorkflowPlanner _workflowPlanner;
    private readonly ModelRouter _modelRouter;
    private readonly AgentScheduler _scheduler;
    private readonly BlackboardSystem _blackboard;
    private readonly MemorySystem _memory;
    private readonly JournalSystem _journal;
    private readonly EventBus _eventBus;
    private readonly ValidatorChain _validator;
    private readonly RollbackManager _rollback;

    /// <summary>信息不足时最多向用户提问的轮数</summary>
    private const int MaxClarificationRounds = 3;

    private readonly ConcurrentDictionary<string, WorkflowDef> _activeWorkflows = new();
    private UserTask? _currentTask;
    private TaskCompletionSource<string>? _askTcs;

    public event Action<string, string>? OnStatusChanged;
    public event Action<UserTask>? OnTaskCreated;
    public event Action<WorkflowDef>? OnWorkflowStarted;
    public event Action<WorkflowNode, AgentResult>? OnNodeCompleted;
    public event Action<string>? OnLogMessage;
    /// <summary>信息不足时向用户提问, 等待回答</summary>
    public event Action<string>? OnAskUser;

    public SupervisorAgent(IntentRouter intentRouter, WorkflowPlanner workflowPlanner,
        ModelRouter modelRouter, AgentScheduler scheduler, BlackboardSystem blackboard,
        MemorySystem memory, JournalSystem journal, EventBus eventBus,
        ValidatorChain validator, RollbackManager rollback)
    {
        _intentRouter = intentRouter;
        _workflowPlanner = workflowPlanner;
        _modelRouter = modelRouter;
        _scheduler = scheduler;
        _blackboard = blackboard;
        _memory = memory;
        _journal = journal;
        _eventBus = eventBus;
        _validator = validator;
        _rollback = rollback;
    }

    /// <summary>
    /// 完整处理用户请求的入口
    /// 信息不足时向用户提问澄清(替代原闲聊模式), 最多 MaxClarificationRounds 轮
    /// </summary>
    public async Task HandleUserRequestAsync(string userInput)
    {
        Log("========================================");
        Log($"用户输入: {userInput}");

        // === 澄清循环: 信息不足时不闲聊, 而是向用户提问补充信息 ===
        for (int round = 0; round < MaxClarificationRounds; round++)
        {
            // Step 1: Intent Router 意图分析 (LLM深度理解 + 规则回退)
            var intent = await _intentRouter.AnalyzeAsync(userInput);
            Log($"意图分析: {intent.Summary}");
            Log($"复杂度: {intent.Complexity}/10, 明确度: {intent.Clarity}/10, 需要Workflow: {intent.NeedsWorkflow}");

            // 信息不足 → 向用户提问, 用补充后的输入重新分析
            if (!intent.NeedsWorkflow || intent.Intent == "General")
            {
                Log($"[信息不足] 向用户提问 (第{round + 1}/{MaxClarificationRounds}轮)");
                var question = BuildClarificationQuestion();
                var answer = await AskUserAsync(question);

                if (string.IsNullOrWhiteSpace(answer) || IsCancelRequest(answer))
                {
                    Log("用户未补充信息, 任务终止");
                    EmitStatus("Cancelled", "信息不足, 任务已取消");
                    return;
                }

                userInput = $"{userInput}\n[补充] {answer.Trim()}";
                continue;
            }

            // 明确的任务 → 创建任务并执行
            var task = new UserTask
            {
                OriginalInput = userInput,
                Intent = intent,
                // 探测用户输入中显式指定的路径作为本次任务工作区(如 "修复 D:\minimind-plus")
                WorkspaceRoot = PathExtractor.ExtractExistingPath(userInput)
            };
            _currentTask = task;
            OnTaskCreated?.Invoke(task);

            if (!string.IsNullOrEmpty(task.WorkspaceRoot))
                Log($"[任务工作区] {task.WorkspaceRoot}");

            // 初始化Working Memory
            _memory.SetWorkingContext(task.Id, userInput, new(), "IntentAnalysis");
            _journal.LogAgentAction("Supervisor", "IntentAnalysis", intent.Summary);

            await ExecuteTaskAsync(task, intent);
            return;
        }

        Log("多次提问后仍无法明确任务, 终止");
        EmitStatus("Cancelled", "任务无法明确, 已取消");
    }

    /// <summary>
    /// 执行已明确的任务: 模型选择 → Workflow规划 → 节点执行 → 验收 → 归档
    /// </summary>
    private async Task ExecuteTaskAsync(UserTask task, IntentResult intent)
    {
        var userInput = task.OriginalInput;

        // Step 2: Model Router 模型选择
        var model = _modelRouter.SelectModel(intent);
        Log($"模型选择: {model.ModelName} (分数: {model.TotalScore:F1})");
        _journal.LogAgentAction("Supervisor", "ModelSelect", model.ModelName);

        // Step 3: Workflow Planner 工作流规划
        var workflow = _workflowPlanner.SelectOrCreate(intent);
        if (workflow == null)
        {
            Log("无法生成Workflow，使用简单查询模式");
            workflow = new WorkflowDef { Name = "QuickQuery" };
        }

        task.WorkflowId = workflow.Id;
        _activeWorkflows[workflow.Id] = workflow;
        Log($"Workflow: {workflow.Name} ({workflow.Nodes.Count}节点)");
        OnWorkflowStarted?.Invoke(workflow);

        // Step 4: Pre Validator
        if (!await _validator.ValidatePreExecution(workflow))
        {
            Log("预校验失败，终止执行");
            EmitStatus("Failed", "预校验失败");
            return;
        }

        // Step 5: 依次执行每个Node
        workflow.Status = "Running";
        EmitStatus("Running", $"执行: {workflow.Name}");

        AgentResult? previousResult = null; // 上一节点产出(参数管线)

        for (int i = 0; i < workflow.Nodes.Count; i++)
        {
            var node = workflow.Nodes[i];
            workflow.CurrentNodeIndex = i;
            node.Status = "Running";

            Log($"--- 阶段[{i + 1}/{workflow.Nodes.Count}]: {node.Label} ({node.AgentType}) ---");
            _memory.SetWorkingContext(task.Id, userInput, new(), node.Label);

            // 注入用户输入参数,确保Agent知道要处理什么内容
            InjectUserInput(node, userInput, task.WorkspaceRoot);

            // 参数管线: 把上一节点产出注入当前节点(模板显式参数优先,不覆盖)
            PropagatePreviousOutput(node, previousResult);

            // 调度Agent执行
            var result = await _scheduler.ExecuteNodeAsync(workflow, node, task.Id);
            node.Status = result.Success ? "Completed" : "Failed";
            OnNodeCompleted?.Invoke(node, result);

            // Per-node Validation (Operation + Stage)
            var blackboardEntries = _blackboard.GetAll(task.Id).ToList();
            var validation = await _validator.ValidatePerNode(node, result, blackboardEntries);

            if (validation.Failed)
            {
                Log($"校验失败，回溯阶段: {node.Label}");
                var recovered = await HandleNodeFailure(workflow, node, i, result);
                if (recovered == null)
                {
                    workflow.Status = "Failed";
                    EmitStatus("Failed", $"阶段 [{node.Label}] 失败");
                    return;
                }
                result = recovered;
            }

            // 记录成功产出,供下一节点消费
            if (result.Success)
                previousResult = result;

            Log($"阶段 [{node.Label}] 完成 ✓");
            _memory.SetWorkingContext(task.Id, userInput, result.ModifiedFiles,
                $"{node.Label} (完成)");
        }

        // Step 6: Final Acceptance Validation (最后一个节点)
        var lastNode = workflow.Nodes.Last();
        var lastResult = new AgentResult { AgentName = lastNode.AgentType, Success = true,
            Output = "Workflow completed" };
        var finalEntries = _blackboard.GetAll(task.Id).ToList();
        var finalValidation = await _validator.ValidateFinalAcceptance(lastNode, lastResult, finalEntries);
        if (finalValidation.Failed)
        {
            Log($"最终验收失败: {string.Join("; ", finalValidation.Steps.Where(s => !s.Passed).Select(s => s.Reason))}");
            workflow.Status = "Failed";
            EmitStatus("Failed", "最终验收未通过");
            return;
        }

        // Step 7: Finalize
        workflow.Status = "Completed";
        task.Status = "Completed";
        EmitStatus("Completed", $"任务完成: {workflow.Name}");

        // Archive
        _memory.Archive(_memory.GetWorkingMemory(task.Id),
            $"完成: {task.OriginalInput[..Math.Min(50, task.OriginalInput.Length)]}",
            $"使用Workflow: {workflow.Name}",
            new());

        Log("========================================");
        Log("任务完成 ✓");
    }

    /// <summary>
    /// 向用户提问(UI弹出提问栏), 等待回答。
    /// 无UI订阅时视为用户取消, 返回空字符串。
    /// </summary>
    public async Task<string> AskUserAsync(string question)
    {
        Log($"[提问] {question}");

        if (OnAskUser == null)
        {
            Log("[提问] 无UI订阅提问事件, 视为取消");
            return string.Empty;
        }

        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _askTcs = tcs;
        OnAskUser?.Invoke(question);
        return await tcs.Task;
    }

    /// <summary>
    /// 用户回答提问(由UI调用), 唤醒等待中的 AskUserAsync
    /// </summary>
    public void ProvideUserAnswer(string answer)
    {
        _askTcs?.TrySetResult(answer ?? string.Empty);
        _askTcs = null;
    }

    /// <summary>
    /// 构造澄清问题 —— 指出信息缺口, 引导用户补充操作/对象/产出
    /// </summary>
    private static string BuildClarificationQuestion()
    {
        return "您的输入信息不足, 我无法确定要执行的具体任务。请补充以下信息:\n" +
               "• 操作类型: 您希望我做什么(如: 分析 / 修改 / 搜索 / 生成代码 / 构建...)\n" +
               "• 操作对象: 针对哪个文件、目录或项目\n" +
               "• 期望产出: 最终想要的结果是什么\n" +
               "(输入\"取消\"可终止本次任务)";
    }

    /// <summary>用户回答是否为取消请求</summary>
    private static bool IsCancelRequest(string answer)
    {
        var a = answer.Trim();
        return a.Equals("取消", StringComparison.OrdinalIgnoreCase)
            || a.Equals("cancel", StringComparison.OrdinalIgnoreCase)
            || a.Equals("算了", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<AgentResult?> HandleNodeFailure(WorkflowDef workflow, WorkflowNode node,
        int index, AgentResult result)
    {
        Log($"尝试恢复节点: {node.Label}");

        // 回滚当前阶段
        var agent = _scheduler.GetAgent(node.AgentType);
        if (agent != null)
            await agent.RollbackAsync(node.Id);

        // 重试 (最多2次), 每次重新注入参数
        string? lastError = result.Error;
        for (int retry = 1; retry <= 2; retry++)
        {
            Log($"重试 {retry}/2: {node.Label}");

            // 清除之前的action参数，重新注入
            if (_currentTask != null)
            {
                node.Parameters.Remove("action");
                InjectUserInput(node, _currentTask.OriginalInput, _currentTask.WorkspaceRoot);
            }

            var retryResult = await _scheduler.ExecuteNodeAsync(workflow, node,
                _currentTask?.Id ?? "");
            if (retryResult.Success)
            {
                Log($"重试成功: {node.Label}");
                node.Status = "Completed";
                OnNodeCompleted?.Invoke(node, retryResult);
                return retryResult;
            }

            // 相同错误重试不会改变结果(如参数缺失), 直接放弃后续重试
            if (string.Equals(retryResult.Error, lastError, StringComparison.Ordinal))
            {
                Log($"重试 {retry} 产生相同错误，跳过后续重试: {lastError}");
                return null;
            }
            lastError = retryResult.Error;
        }

        return null;
    }

    /// <summary>
    /// 参数管线: 将上一节点的产出注入当前节点参数。
    /// path = 上一节点修改的第一个文件; previousOutput = 上一节点文本产出;
    /// modifiedFiles = 上一节点修改的全部文件(|分隔, 供验证阶段使用)。
    /// 模板已显式设置的参数不会被覆盖(TryAdd)。
    /// </summary>
    private static void PropagatePreviousOutput(WorkflowNode node, AgentResult? previousResult)
    {
        if (previousResult == null) return;

        var lastFile = previousResult.ModifiedFiles.FirstOrDefault();
        if (!string.IsNullOrEmpty(lastFile))
            node.Parameters.TryAdd("path", lastFile);

        if (previousResult.ModifiedFiles.Count > 0)
            node.Parameters.TryAdd("modifiedFiles", string.Join("|", previousResult.ModifiedFiles));

        if (!string.IsNullOrEmpty(previousResult.Output))
            node.Parameters.TryAdd("previousOutput", previousResult.Output);
    }

    /// <summary>
    /// 将用户输入注入到节点参数,确保Agent能获取到任务内容
    /// 模板已显式设置的参数不会被覆盖(TryAdd)
    /// </summary>
    private static void InjectUserInput(WorkflowNode node, string userInput, string workspaceRoot)
    {
        // 原始输入与目标: 所有Agent都可读取
        node.Parameters.TryAdd("input", userInput);
        node.Parameters.TryAdd("target", userInput);

        // 任务工作区: 从用户输入探测出的路径, 优先于Agent默认工作区
        if (!string.IsNullOrWhiteSpace(workspaceRoot))
            node.Parameters.TryAdd("workspaceRoot", workspaceRoot);

        // 根据Agent类型 + 节点语义设置默认 action
        switch (node.AgentType)
        {
            case "FileAgent":
                // 读/列目录产生分析产出，写操作由模板显式指定
                node.Parameters.TryAdd("action", node.Label switch
                {
                    "Discovery" or "Analysis" => "list",   // 探索阶段列出文件
                    "Format" => "write",                     // 格式化阶段写入
                    _ => "read"                              // 默认读取
                });
                break;
            case "CodeAgent":
                node.Parameters.TryAdd("action", node.Label switch
                {
                    "Coding" or "Writing" or "Implement" or "Refactor" =>
                        IsFixRequest(userInput) ? "fix" : "generate",
                    "Review" => "review",
                    _ => "analyze"
                });
                break;
            case "SearchAgent":
                node.Parameters.TryAdd("query", userInput);
                node.Parameters.TryAdd("action", "search");
                break;
            case "CommandAgent":
                // 测试/验证阶段: 走智能验证(LLM生成命令 + 语法保底); 其余需模板显式提供命令
                if (node.Label is "Testing" or "Verify" or "验证")
                    node.Parameters.TryAdd("action", "verify");
                else
                    node.Parameters.TryAdd("action", "exec");
                break;
        }
    }

    /// <summary>判断输入是否为修复类请求(修复/无法运行/报错等)</summary>
    private static bool IsFixRequest(string userInput)
    {
        var lower = userInput.ToLower();
        return lower.Contains("修复") || lower.Contains("修一下") || lower.Contains("修好")
            || lower.Contains("无法运行") || lower.Contains("运行不了") || lower.Contains("不能运行")
            || lower.Contains("跑不起来") || lower.Contains("报错") || lower.Contains("出错")
            || lower.Contains("bug") || lower.Contains("fix") || lower.Contains("error");
    }

    // === Blackboard管理(仅Supervisor可写Fact/Decision) ===
    public void ConfirmFact(string taskId, string observationId, string? approvedBy = null)
    {
        var board = _blackboard.GetAll(taskId);
        var obs = board.FirstOrDefault(e => e.Id == observationId);
        if (obs != null)
        {
            var fact = _blackboard.AddFact(taskId, obs.Content, approvedBy ?? "Supervisor");
            Log($"确认Fact: {fact.Content}");
            _journal.LogDecision("Supervisor", "FactConfirmed", obs.Content, "");
        }
    }

    public void MakeDecision(string taskId, string topic, string choice, string reason)
    {
        var facts = _blackboard.GetByType<Fact>(taskId).Select(f => f.Id).ToList();
        _blackboard.AddDecision(taskId, topic, choice, reason, facts);
        Log($"决策: [{topic}] → {choice} ({reason})");
        _journal.LogDecision("Supervisor", topic, choice, reason);
    }

    // === 项目初始化 ===
    public void InitProject(string name, string framework, string language, string root)
    {
        _memory.InitProject(name, framework, language, root);
        Log($"项目初始化: {name} ({framework}/{language}) @ {root}");
    }

    private void Log(string msg)
    {
        OnLogMessage?.Invoke(msg);
        _eventBus.Publish("SupervisorLog", msg);
    }

    private void EmitStatus(string status, string detail)
    {
        OnStatusChanged?.Invoke(status, detail);
        _eventBus.Publish("SupervisorStatus", new { Status = status, Detail = detail });
    }

    public UserTask? CurrentTask => _currentTask;
    public IReadOnlyDictionary<string, WorkflowDef> ActiveWorkflows =>
        new ConcurrentDictionary<string, WorkflowDef>(_activeWorkflows);
}
