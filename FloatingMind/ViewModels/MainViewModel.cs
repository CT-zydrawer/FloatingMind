using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using FloatingMind.Helpers;
using FloatingMind.Models.Agent;
using FloatingMind.Models.Blackboard;
using FloatingMind.Models.Command;
using FloatingMind.Models.Config;
using FloatingMind.Models.Journal;
using FloatingMind.Models.Lock;
using FloatingMind.Models.Workflow;
using FloatingMind.Services;

namespace FloatingMind.ViewModels;

public class MainViewModel : ObservableObject
{
    private readonly SupervisorAgent _supervisor;
    private readonly BlackboardSystem _blackboard;
    private readonly JournalSystem _journal;
    private readonly AgentScheduler _scheduler;
    private readonly MemorySystem _memory;
    private readonly CommandSafetyService _cmdSafety;
    private readonly RollbackManager _rollback;
    private readonly Data.DatabaseService _db;
    private AppConfig _config;

    public MainViewModel(SupervisorAgent supervisor, BlackboardSystem blackboard,
        JournalSystem journal, AgentScheduler scheduler, MemorySystem memory,
        CommandSafetyService cmdSafety, RollbackManager rollback, Data.DatabaseService db,
        AppConfig config)
    {
        _supervisor = supervisor;
        _blackboard = blackboard;
        _journal = journal;
        _scheduler = scheduler;
        _memory = memory;
        _cmdSafety = cmdSafety;
        _rollback = rollback;
        _db = db;

        // 与服务共享同一config实例: 设置页修改立即对Agent生效
        _config = config;

        // 连线事件
        _supervisor.OnLogMessage += log => Application.Current?.Dispatcher.Invoke(() => AddLog(log));
        _supervisor.OnStatusChanged += (status, detail) =>
            Application.Current?.Dispatcher.Invoke(() =>
            {
                SystemStatus = $"{status}: {detail}";
                IsRunning = status == "Running";
            });
        _supervisor.OnTaskCreated += task =>
            Application.Current?.Dispatcher.Invoke(() => CurrentTask = task);
        _supervisor.OnWorkflowStarted += wf =>
            Application.Current?.Dispatcher.Invoke(() =>
            {
                ActiveWorkflow = wf;
                RefreshWorkflowNodes();
            });
        _supervisor.OnNodeCompleted += (node, result) =>
            Application.Current?.Dispatcher.Invoke(() =>
            {
                RefreshWorkflowNodes();
                if (!string.IsNullOrEmpty(result.Output))
                    AddLog($"输出: {result.Output[..Math.Min(200, result.Output.Length)]}");
                RefreshBlackboard();
            });

        _blackboard.OnEntryAdded += (taskId, entry) =>
            Application.Current?.Dispatcher.Invoke(() => RefreshBlackboard());
        _blackboard.OnEntryUpdated += (taskId, entry) =>
            Application.Current?.Dispatcher.Invoke(() => RefreshBlackboard());

        _journal.OnEntryAdded += entry =>
            Application.Current?.Dispatcher.Invoke(() => JournalEntries.Insert(0, entry));

        _scheduler.OnAgentStatusChanged += (agent, status) =>
            Application.Current?.Dispatcher.Invoke(() => UpdateAgentStatus(agent, status));

        // 初始化Agent状态
        foreach (var agent in scheduler.RegisteredAgents)
            AgentStatuses.Add(new AgentStatusInfo { Name = agent, Status = "Idle" });

        // Commands
        SendCommand = new AsyncRelayCommand(async () => await ExecuteUserInput());
        ClearCommand = new RelayCommand(() => UserInput = "");
        ConfirmCommand = new AsyncRelayCommand(async () => await ConfirmPending());
        RejectCommand = new AsyncRelayCommand(async () => await RejectPending());
    }

    // ===== Properties =====
    private string _userInput = "";
    public string UserInput { get => _userInput; set => SetProperty(ref _userInput, value); }

    private string _systemStatus = "就绪";
    public string SystemStatus { get => _systemStatus; set => SetProperty(ref _systemStatus, value); }

    private bool _isRunning;
    public bool IsRunning { get => _isRunning; set => SetProperty(ref _isRunning, value); }

    private UserTask? _currentTask;
    public UserTask? CurrentTask { get => _currentTask; set => SetProperty(ref _currentTask, value); }

    private WorkflowDef? _activeWorkflow;
    public WorkflowDef? ActiveWorkflow { get => _activeWorkflow; set => SetProperty(ref _activeWorkflow, value); }

    private string _activeTab = "blackboard";
    public string ActiveTab { get => _activeTab; set => SetProperty(ref _activeTab, value); }

    public void SetActiveTab(string tab) => ActiveTab = tab;

    // === Log ===
    public ObservableCollection<string> LogEntries { get; } = new();

    public void AddLog(string msg)
    {
        LogEntries.Insert(0, $"[{DateTime.Now:HH:mm:ss}] {msg}");
        while (LogEntries.Count > 200) LogEntries.RemoveAt(LogEntries.Count - 1);
    }

    // === Blackboard ===
    public ObservableCollection<BlackboardEntry> BlackboardEntries { get; } = new();

    private void RefreshBlackboard()
    {
        BlackboardEntries.Clear();
        var taskId = _currentTask?.Id ?? "default";
        foreach (var entry in _blackboard.GetAll(taskId))
            BlackboardEntries.Add(entry);
    }

    // === Journal ===
    public ObservableCollection<JournalEntry> JournalEntries { get; } = new();

    // === Workflow Nodes ===
    public ObservableCollection<WorkflowNodeVM> WorkflowNodes { get; } = new();

    public void RefreshWorkflowNodes()
    {
        WorkflowNodes.Clear();
        if (_activeWorkflow == null) return;

        foreach (var node in _activeWorkflow.Nodes)
        {
            var nextNodes = _activeWorkflow.GetNextNodes(node.Id);
            foreach (var next in nextNodes)
            {
                WorkflowNodes.Add(new WorkflowNodeVM
                {
                    SourceLabel = node.Label,
                    TargetLabel = next.Label,
                    SourceStatus = node.Status,
                    TargetStatus = next.Status,
                    SourceAgent = node.AgentType,
                    TargetAgent = next.AgentType,
                    IsCurrent = node.Id == _activeWorkflow.Nodes[_activeWorkflow.CurrentNodeIndex].Id
                });
            }
        }
    }

    // === Agent Status ===
    public ObservableCollection<AgentStatusInfo> AgentStatuses { get; } = new();

    private void UpdateAgentStatus(string agent, string status)
    {
        var existing = AgentStatuses.FirstOrDefault(a => a.Name == agent);
        if (existing != null) existing.Status = status;
        else AgentStatuses.Add(new AgentStatusInfo { Name = agent, Status = status });
    }

    // === Locks ===
    public ObservableCollection<ResourceLock> ActiveLocks { get; } = new();

    // === Pending confirmations ===
    private bool _hasPendingConfirm;
    public bool HasPendingConfirm { get => _hasPendingConfirm; set => SetProperty(ref _hasPendingConfirm, value); }

    private string _pendingCommand = "";
    public string PendingCommand { get => _pendingCommand; set => SetProperty(ref _pendingCommand, value); }

    // === Commands ===
    public ICommand SendCommand { get; }
    public ICommand ClearCommand { get; }
    public ICommand ConfirmCommand { get; }
    public ICommand RejectCommand { get; }

    private async Task ExecuteUserInput()
    {
        if (string.IsNullOrWhiteSpace(UserInput) || IsRunning) return;

        var input = UserInput;
        UserInput = "";
        AddLog($"> {input}");

        // 整个调度管线放到后台线程执行,避免 Agent 的同步文件I/O阻塞UI线程。
        // ViewModel 各事件处理器内部已通过 Dispatcher.Invoke 回到UI线程刷新界面。
        IsRunning = true;
        try
        {
            await Task.Run(() => _supervisor.HandleUserRequestAsync(input));
        }
        catch (Exception ex)
        {
            // 管线异常不能静默吞掉: 记录日志并确保状态可见
            AddLog($"系统错误: {ex.Message}");
            SystemStatus = $"Failed: {ex.Message}";
        }
        finally
        {
            // 无论成功/失败/异常都复位运行状态,防止发送按钮永久禁用
            IsRunning = false;
        }
    }

    private async Task ConfirmPending()
    {
        HasPendingConfirm = false;
        PendingCommand = "";
        await Task.CompletedTask;
    }

    private async Task RejectPending()
    {
        HasPendingConfirm = false;
        PendingCommand = "";
        await Task.CompletedTask;
    }

    // === Config ===
    public AppConfig Config => _config;

    public void SaveConfig()
    {
        _db.SaveConfig(_config);
    }
}

public class WorkflowNodeVM
{
    public string SourceLabel { get; set; } = "";
    public string TargetLabel { get; set; } = "";
    public string SourceStatus { get; set; } = "Pending";
    public string TargetStatus { get; set; } = "Pending";
    public string SourceAgent { get; set; } = "";
    public string TargetAgent { get; set; } = "";
    public bool IsCurrent { get; set; }
}

public class AgentStatusInfo
{
    public string Name { get; set; } = "";
    public string Status { get; set; } = "Idle";
}

public class AsyncRelayCommand : ICommand
{
    private readonly Func<Task> _execute;
    private readonly Func<bool>? _canExecute;

    public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;
    public async void Execute(object? parameter)
    {
        try { await _execute(); }
        catch (Exception ex)
        {
            // 不再静默吞异常: 至少写入调试输出,便于定位
            System.Diagnostics.Debug.WriteLine($"[AsyncRelayCommand] {ex}");
        }
    }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }
}
