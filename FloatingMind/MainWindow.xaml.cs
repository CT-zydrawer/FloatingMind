using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FloatingMind.Agents;
using FloatingMind.Data;
using FloatingMind.Models.Config;
using FloatingMind.Services;
using FloatingMind.ViewModels;
using FloatingMind.Services.LLM;

namespace FloatingMind;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly SupervisorAgent _supervisor;
    private readonly CommandAgent _commandAgent;
    private FileHistoryService _fileHistory = null!;

    public MainWindow()
    {
        InitializeComponent();

        // ===== 构建服务容器 =====
        // 数据根目录: 自探测用户主目录下 .floatingmind, 与编译输出目录彻底解耦
        var appDataRoot = WorkspaceResolver.ResolveAppDataRoot();

        // 旧版本数据落在编译输出目录(bin/.../.floatingmind), 首次切换时迁移配置, 避免丢失API Key
        MigrateLegacyConfig(appDataRoot);

        var eventBus = new EventBus();
        var journal = new JournalSystem(appDataRoot);
        var blackboard = new BlackboardSystem();
        var memory = new MemorySystem(appDataRoot);
        var db = new DatabaseService(appDataRoot);

        // 统一使用同一个config实例: 服务与ViewModel/设置页共享,
        // 设置页保存后Agent立即读到新API Key,无需重启
        var config = db.LoadConfig();

        // 工作区: 自探测(设置页指定) > 自建默认工作区, 绝不指向 Floating Mind 自身源码
        var workspaceRoot = WorkspaceResolver.ResolveWorkspaceRoot(config);
        config.WorkspacePath = workspaceRoot;
        db.SaveConfig(config); // 记住解析结果, 下次启动沿用同一工作区

        var modelRouter = new ModelRouter(config);
        var llm = new DeepSeekService(config);
        var promptBuilder = new PromptBuilder(memory, blackboard);
        var cmdSafety = new CommandSafetyService(OnCommandNeedsConfirm);
        var rollback = new RollbackManager(appDataRoot);
        _fileHistory = new FileHistoryService(appDataRoot, journal);

        var intentRouter = new IntentRouter(llm, promptBuilder);
        var workflowPlanner = new WorkflowPlanner();
        var validator = new ValidatorChain(eventBus, journal);

        // Agent注册 (workspaceRoot 指向用户项目源码,而非编译输出目录)
        var scheduler = new AgentScheduler(blackboard, journal, eventBus,
            config.MaxConcurrentAgents);

        scheduler.Register(new FileAgent(blackboard, journal, cmdSafety, workspaceRoot, _fileHistory));
        scheduler.Register(new CodeAgent(blackboard, journal, modelRouter, config, workspaceRoot, llm, _fileHistory));
        scheduler.Register(new SearchAgent(blackboard, journal, config, llm, workspaceRoot));

        _commandAgent = new CommandAgent(blackboard, journal, cmdSafety, llm, config, OnCommandNeedsConfirm);
        scheduler.Register(_commandAgent);

        // Supervisor (信息不足时通过 OnAskUser 向用户提问澄清, 不再闲聊)
        _supervisor = new SupervisorAgent(intentRouter, workflowPlanner, modelRouter,
            scheduler, blackboard, memory, journal, eventBus, validator, rollback);

        // ViewModel (共享同一config实例)
        _viewModel = new MainViewModel(_supervisor, blackboard, journal, scheduler, memory,
            cmdSafety, rollback, db, config);

        DataContext = _viewModel;

        // 监听 Running 状态更新 UI 指示器
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.IsRunning))
                RunningBadge.Visibility = _viewModel.IsRunning ? Visibility.Visible : Visibility.Collapsed;
        };

        // 信息不足时 Supervisor 向用户提问: 显示提问栏并聚焦输入框
        _supervisor.OnAskUser += question => Dispatcher.Invoke(() =>
        {
            _viewModel.PendingQuestion = question;
            _viewModel.HasPendingQuestion = true;
            TxtQuestionAnswer.Clear();
            TxtQuestionAnswer.Focus();
        });

        // 初始化
        _viewModel.AddLog("Floating Mind 已启动 — 多Agent智能调度系统就绪");
    }

    // === 命令确认回调 ===
    private void OnCommandNeedsConfirm(string command, Models.Command.CommandRiskLevel level)
    {
        Dispatcher.Invoke(() =>
        {
            _viewModel.PendingCommand = command;
            _viewModel.HasPendingConfirm = true;
        });
    }

    // === 输入框 Enter 发送 ===
    private void UserInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !_viewModel.IsRunning)
        {
            _viewModel.SendCommand.Execute(null);
            e.Handled = true;
        }
    }

    // === 提问栏 Enter 提交 ===
    private void QuestionAnswer_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _viewModel.HasPendingQuestion)
        {
            _viewModel.SubmitAnswerCommand.Execute(null);
            e.Handled = true;
        }
    }

    // === 导航按钮 ===
    //private void NavBlackboard_Click(object sender, RoutedEventArgs e)
    //{
        //_viewModel.SetActiveTab("blackboard");
    //}

    private void NavChat_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.SetActiveTab("chat");
        LogScroller.ScrollToBottom();
    }

    private void NavWorkflow_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.SetActiveTab("workflow");
        ShowWorkflowDialog();
    }

    private void NavJournal_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.SetActiveTab("journal");
        ShowJournalDialog();
    }

    private void NavHistory_Click(object sender, RoutedEventArgs e)
    {
        ShowHistoryDialog();
    }

    private void NavSettings_Click(object sender, RoutedEventArgs e)
    {
        ShowSettingsDialog();
    }

    // === 对话框 ===
    private void ShowSettingsDialog()
    {
        var dialog = new SettingsDialog(_viewModel.Config, () => _viewModel.SaveConfig());
        dialog.Owner = this;
        dialog.ShowDialog();
    }

    private void ShowWorkflowDialog()
    {
        if (_viewModel.ActiveWorkflow == null)
        {
            MessageBox.Show("当前没有活跃的Workflow。请先输入一个任务。",
                "Workflow", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new WorkflowDialog(_viewModel.ActiveWorkflow);
        dialog.Owner = this;
        dialog.ShowDialog();
    }

    private void ShowJournalDialog()
    {
        var dialog = new JournalDialog(_viewModel.JournalEntries);
        dialog.Owner = this;
        dialog.ShowDialog();
    }

    private void ShowHistoryDialog()
    {
        var dialog = new HistoryDialog(_fileHistory);
        dialog.Owner = this;
        dialog.ShowDialog();
    }

    /// <summary>
    /// 迁移旧版本配置: 旧版数据在编译输出目录(bin/.../.floatingmind)下,
    /// 数据目录改为用户主目录后, 首次启动时复制旧配置, 保留API Key等设置。
    /// </summary>
    private static void MigrateLegacyConfig(string appDataRoot)
    {
        var newConfig = Path.Combine(appDataRoot, ".floatingmind", "data", "config.json");
        if (File.Exists(newConfig)) return;

        var legacy = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
            ".floatingmind", "data", "config.json");
        if (!File.Exists(legacy)) return;

        Directory.CreateDirectory(Path.GetDirectoryName(newConfig)!);
        File.Copy(legacy, newConfig);
    }
}
