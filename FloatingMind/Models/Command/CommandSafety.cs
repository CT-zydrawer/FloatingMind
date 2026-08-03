namespace FloatingMind.Models.Command;

/// <summary>
/// 10. Command安全系统 —— 风险等级
/// </summary>
public enum CommandRiskLevel
{
    L0_Auto,       // 自动执行 (ls, dotnet build)
    L1_Log,        // 记录后执行
    L2_Confirm,    // 需用户确认 (npm install)
    L3_Forbidden   // 禁止执行 (rm -rf /)
}

public record CommandSafetyResult(
    CommandRiskLevel Level,
    string Reason,
    bool Allowed
);

/// <summary>
/// 命令安全分析结果
/// </summary>
public class CommandAnalysis
{
    public string Command { get; set; } = string.Empty;
    public CommandRiskLevel RiskLevel { get; set; } = CommandRiskLevel.L0_Auto;
    public string RiskReason { get; set; } = string.Empty;
    public bool Allowed => RiskLevel != CommandRiskLevel.L3_Forbidden;

    // 危险模式匹配
    private static readonly List<PatternRule> DangerousPatterns = new()
    {
        // L3 - 绝对禁止
        new("rm\\s+-rf\\s+/", CommandRiskLevel.L3_Forbidden, "递归删除根文件系统"),
        new("rm\\s+-rf\\s+~", CommandRiskLevel.L3_Forbidden, "删除用户主目录"),
        new("dd\\s+if=", CommandRiskLevel.L3_Forbidden, "磁盘直接写入"),
        new("mkfs\\.", CommandRiskLevel.L3_Forbidden, "格式化文件系统"),
        new(">\\s*/dev/sd", CommandRiskLevel.L3_Forbidden, "写入块设备"),
        new("\\.\\.\\/\\.\\.\\/\\*", CommandRiskLevel.L3_Forbidden, "遍历删除上级目录"),

        // L2 - 需确认
        new("npm\\s+install", CommandRiskLevel.L2_Confirm, "安装npm包"),
        new("pip\\s+install", CommandRiskLevel.L2_Confirm, "安装Python包"),
        new("chmod\\s+777", CommandRiskLevel.L2_Confirm, "权限改为777"),
        new("rm\\s+-rf", CommandRiskLevel.L2_Confirm, "递归强制删除"),
        new("docker\\s+rm", CommandRiskLevel.L2_Confirm, "删除Docker容器"),
        new("git\\s+push\\s+--force", CommandRiskLevel.L2_Confirm, "强制推送"),
        new("git\\s+reset\\s+--hard", CommandRiskLevel.L2_Confirm, "硬重置"),

        // L1 - 记录执行
        new("dotnet\\s+build", CommandRiskLevel.L1_Log, "构建项目"),
        new("dotnet\\s+run", CommandRiskLevel.L1_Log, "运行项目"),
        new("dotnet\\s+test", CommandRiskLevel.L1_Log, "运行测试"),
        new("git\\s+commit", CommandRiskLevel.L1_Log, "Git提交"),
        new("git\\s+checkout", CommandRiskLevel.L1_Log, "Git切换分支"),
    };
}

public record PatternRule(string Pattern, CommandRiskLevel Level, string Reason);
