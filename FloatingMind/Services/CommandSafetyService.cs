using System.Text.RegularExpressions;
using FloatingMind.Models.Command;

namespace FloatingMind.Services;

/// <summary>
/// 10. Command安全系统 —— 命令执行前风险分析
/// </summary>
public class CommandSafetyService
{
    private readonly List<(Regex Pattern, CommandRiskLevel Level, string Reason)> _rules = new();
    private readonly Action<string, CommandRiskLevel>? _onConfirm;

    public CommandSafetyService(Action<string, CommandRiskLevel>? onConfirm = null)
    {
        _onConfirm = onConfirm;
        InitRules();
    }

    private void InitRules()
    {
        // L3 - 禁止
        Add("rm\\s+-rf\\s+/", CommandRiskLevel.L3_Forbidden, "递归删除根文件系统");
        Add("rm\\s+-rf\\s+~", CommandRiskLevel.L3_Forbidden, "删除用户主目录");
        Add("dd\\s+if=", CommandRiskLevel.L3_Forbidden, "磁盘直接写入");
        Add("mkfs\\.", CommandRiskLevel.L3_Forbidden, "格式化文件系统");
        Add(">\\s*/dev/sd", CommandRiskLevel.L3_Forbidden, "写入块设备");
        Add("rm\\s+-rf\\s+\\*", CommandRiskLevel.L3_Forbidden, "删除全部文件");
        Add(":\\s*\\(\\)\\s*\\{", CommandRiskLevel.L3_Forbidden, "Shell叉炸弹");
        Add("chmod\\s+-R\\s+777\\s+/", CommandRiskLevel.L3_Forbidden, "递归修改根目录权限");
        Add(">\\s*/etc/", CommandRiskLevel.L3_Forbidden, "写入系统配置");

        // L2 - 确认
        Add("npm\\s+install", CommandRiskLevel.L2_Confirm, "安装npm包");
        Add("npm\\s+uninstall", CommandRiskLevel.L2_Confirm, "卸载npm包");
        Add("pip\\s+install", CommandRiskLevel.L2_Confirm, "安装Python包");
        Add("chmod\\s+777", CommandRiskLevel.L2_Confirm, "权限设为777");
        Add("rm\\s+-rf", CommandRiskLevel.L2_Confirm, "递归强制删除");
        Add("docker\\s+rm", CommandRiskLevel.L2_Confirm, "删除Docker容器");
        Add("docker\\s+rmi", CommandRiskLevel.L2_Confirm, "删除Docker镜像");
        Add("git\\s+push\\s+--force", CommandRiskLevel.L2_Confirm, "强制推送");
        Add("git\\s+reset\\s+--hard", CommandRiskLevel.L2_Confirm, "硬重置");
        Add("rmdir\\s+/s", CommandRiskLevel.L2_Confirm, "递归删除目录");
        Add("del\\s+/f\\s+/s", CommandRiskLevel.L2_Confirm, "强制递归删除");
        Add("net\\s+user", CommandRiskLevel.L2_Confirm, "用户管理操作");

        // L1 - 记录
        Add("dotnet\\s+build", CommandRiskLevel.L1_Log, "项目构建");
        Add("dotnet\\s+run", CommandRiskLevel.L1_Log, "项目运行");
        Add("dotnet\\s+test", CommandRiskLevel.L1_Log, "运行测试");
        Add("dotnet\\s+publish", CommandRiskLevel.L1_Log, "项目发布");
        Add("git\\s+commit", CommandRiskLevel.L1_Log, "Git提交");
        Add("git\\s+checkout", CommandRiskLevel.L1_Log, "Git分支切换");
        Add("git\\s+merge", CommandRiskLevel.L1_Log, "Git合并");
        Add("git\\s+rebase", CommandRiskLevel.L1_Log, "Git变基");
        Add("mkdir", CommandRiskLevel.L1_Log, "创建目录");
    }

    private void Add(string pattern, CommandRiskLevel level, string reason)
    {
        _rules.Add((new Regex(pattern, RegexOptions.IgnoreCase), level, reason));
    }

    public CommandSafetyResult Analyze(string command)
    {
        foreach (var (pattern, level, reason) in _rules)
        {
            if (pattern.IsMatch(command))
            {
                return new CommandSafetyResult(level, reason, level != CommandRiskLevel.L3_Forbidden);
            }
        }

        // 默认 L0
        return new CommandSafetyResult(CommandRiskLevel.L0_Auto, "安全命令", true);
    }

    public bool CanAutoExecute(string command) => Analyze(command).Level == CommandRiskLevel.L0_Auto;

    public bool IsForbidden(string command) => Analyze(command).Level == CommandRiskLevel.L3_Forbidden;
}
