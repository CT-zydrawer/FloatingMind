using FloatingMind.Models.Config;

namespace FloatingMind.Services;

/// <summary>
/// 工作区解析器 —— 应用自探测工作目录并自建数据工作区
///
/// 解决旧实现的两个问题:
/// 1. 数据目录(.floatingmind)错误地落在编译输出目录 bin/Debug/... 下, 会被清理/混入构建产物
/// 2. 工作区从编译输出目录向上探测 .csproj, 结果误指向 Floating Mind 自身源码
///
/// 新方案:
/// - 数据根目录: %USERPROFILE%\.floatingmind (配置/日志/记忆/备份, 与编译输出彻底解耦)
/// - 工作区根目录: 设置页显式指定 > 自建默认工作区 (Documents/FloatingMindWorkspaces/Default)
///
/// 注意: JournalSystem/MemorySystem/DatabaseService/RollbackManager 均约定在传入的
/// 根目录下创建 .floatingmind/<子目录>, 因此这里返回用户主目录作为"宿主根"。
/// </summary>
public static class WorkspaceResolver
{
    /// <summary>
    /// 应用数据宿主根目录 —— 用户主目录, 各服务在其下创建 .floatingmind/。
    /// 绝不使用编译输出目录。
    /// </summary>
    public static string ResolveAppDataRoot()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return EnsureDirectory(root);
    }

    /// <summary>
    /// 工作区根目录解析:
    /// 1. 设置页显式指定的 WorkspacePath(最高优先级, 同时作为跨启动记忆)
    /// 2. 否则自建默认工作区: Documents/FloatingMindWorkspaces/Default
    /// 绝不返回 Floating Mind 自身源码目录
    /// </summary>
    public static string ResolveWorkspaceRoot(AppConfig config)
    {
        // 1. 配置的工作区(用户显式指定或上次解析结果)
        if (!string.IsNullOrWhiteSpace(config.WorkspacePath))
            return EnsureDirectory(config.WorkspacePath);

        // 2. 自建默认工作区
        var fallback = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "FloatingMindWorkspaces", "Default");
        return EnsureDirectory(fallback);
    }

    private static string EnsureDirectory(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }
}
