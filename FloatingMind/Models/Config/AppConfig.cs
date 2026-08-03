namespace FloatingMind.Models.Config;

/// <summary>
/// 应用配置
/// </summary>
public class AppConfig
{
    // LLM配置
    public string DeepSeekApiKey { get; set; } = string.Empty;
    public string DeepSeekApiUrl { get; set; } = "https://api.deepseek.com/v1";
    public string LowCostModel { get; set; } = "deepseek-chat";
    public string HighPerformanceModel { get; set; } = "deepseek-reasoner";

    // 项目配置
    public string ProjectRoot { get; set; } = string.Empty;
    public string WorkspacePath { get; set; } = string.Empty;

    // 代理配置
    public int MaxConcurrentAgents { get; set; } = 2;
    public int ResourceLockTimeoutSeconds { get; set; } = 300;

    // 主题
    public string Theme { get; set; } = "Dark";
    public bool AutoScrollJournal { get; set; } = true;
}

/// <summary>
/// 3.1 Intent Router 分析结果
/// </summary>
public class IntentResult
{
    public string Intent { get; set; } = string.Empty;     // Writing/Development/Analysis/General/Chat...
    public int Complexity { get; set; } = 1;                // 1-10
    public int Clarity { get; set; } = 5;                   // 1-10, 请求明确度(越高越确定用户想要执行操作)
    public bool NeedsWorkflow { get; set; }
    public string WorkflowName { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
}

/// <summary>
/// Task信息
/// </summary>
public class UserTask
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string OriginalInput { get; set; } = string.Empty;
    public IntentResult? Intent { get; set; }
    public string? WorkflowId { get; set; }
    public string Status { get; set; } = "Created";
    /// <summary>本次任务的工作区根目录(从用户输入探测出的路径, 空=使用默认工作区)</summary>
    public string WorkspaceRoot { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}
