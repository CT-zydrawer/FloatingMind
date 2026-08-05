using System.Text.RegularExpressions;

namespace FloatingMind.Services;

/// <summary>
/// 路径映射器 —— 统一解决 Agent 输出文件落位与目标定位问题(设计文档 §4.1 Project Memory 文件位置)
///
/// 解决三个已知缺陷:
/// 1. 输出文件落位: LLM提出的相对路径 / 用户输入中的(可能尚不存在的)绝对路径 → 映射到任务工作区内的绝对路径
/// 2. 目标定位:    "检查 login.py" 这类输入 → 在工作区内找到真实文件(修复"检查Agent无法定位路径")
/// 3. 安全边界:    输出禁止越出工作区、禁止覆盖系统保护文件、自动创建父目录
/// </summary>
public static class PathMapper
{
    // 系统保护文件 —— 与 FileAgent/CodeAgent 的保护清单保持一致, 集中管理
    private static readonly HashSet<string> SystemProtectedFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "Generated.cs", "MainWindow.xaml.cs", "MainWindow.xaml", "App.xaml.cs",
        "App.xaml", "FloatingMind.csproj", "FloatingMind.sln", "Program.cs", "AssemblyInfo.cs"
    };

    /// <summary>是否为系统保护文件(禁止 Agent 修改 Floating Mind 自身关键文件)</summary>
    public static bool IsSystemProtected(string path)
    {
        try { return SystemProtectedFiles.Contains(Path.GetFileName(path)); }
        catch { return false; }
    }

    /// <summary>
    /// 定位已存在的文件/目录(返回绝对路径, 找不到返回空串):
    /// 1. 本身是已存在的绝对路径
    /// 2. 从自由文本中提取路径片段(引号路径 / 盘符绝对路径)
    /// 3. 工作区相对路径(workspaceRoot + 输入)
    /// 4. 工作区内按文件名搜索(如 "检查 login.py" → workspaceRoot/login.py)
    /// </summary>
    public static string LocateExisting(string? target, string workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(target)) return string.Empty;
        var text = target.Trim().Trim('"', '\'');

        // 1. 本身是已存在的绝对/相对路径
        if (IsExisting(text)) return text;

        // 2. 从自由文本中提取路径片段(引号内路径、盘符绝对路径)
        foreach (var cand in ExtractPathTokens(text))
            if (IsExisting(cand)) return cand;

        // 3. 工作区相对路径
        if (!string.IsNullOrWhiteSpace(workspaceRoot))
        {
            var rel = Path.Combine(workspaceRoot, text);
            if (IsExisting(rel)) return rel;

            // 4. 按文件名在工作区内搜索(排除 bin/obj 等目录)
            var fileName = GetFileNamePart(text);
            if (!string.IsNullOrEmpty(fileName))
            {
                try
                {
                    var hit = Directory.EnumerateFiles(workspaceRoot, fileName, SearchOption.AllDirectories)
                        .FirstOrDefault(f => !IsExcluded(f, workspaceRoot));
                    if (hit != null) return hit;
                }
                catch { /* 目录不可读时返回空 */ }
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// 从自由文本中提取所有类路径片段(引号内路径 + 盘符绝对路径 + 裸文件名), 不要求路径存在。
    /// 用于: 定位已存在文件 / 探测用户指定的输出位置(文件可能尚未创建)。
    /// </summary>
    public static List<string> ExtractPathTokens(string? input)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(input)) return result;

        // 1. 引号内路径: "D:\foo\bar.py" 或 'D:\foo\bar.py' (用户显式引用, 原样保留)
        foreach (var quote in new[] { '"', '\'' })
        {
            int start = input.IndexOf(quote);
            while (start >= 0)
            {
                int end = input.IndexOf(quote, start + 1);
                if (end > start + 1)
                {
                    var candidate = input[(start + 1)..end].Trim();
                    if (LooksLikePath(candidate)) result.Add(candidate);
                }
                start = input.IndexOf(quote, start + 1);
            }
        }

        // 2. 盘符绝对路径: D:\foo\bar.py / D:/foo/bar.py
        foreach (Match m in Regex.Matches(input, @"[A-Za-z]:[\\/][^\s""'，。；、]+"))
        {
            var candidate = m.Value.TrimEnd('\\', '/', '.', ',', ';', ')', ']', '}');
            // 中文目录名合法, 但"路径后紧跟中文说明"(如 D:\a\bug.py里的错误)需要剥离;
            // 仅在候选带文件扩展名时剥离尾部中文, 避免误伤纯中文目录名(D:\新项目)
            if (Path.HasExtension(candidate))
                candidate = Regex.Replace(candidate, @"[\u4e00-\u9fff]+$", "");
            if (LooksLikePath(candidate)) result.Add(candidate);
        }

        // 3. 裸文件名(自由文本中的 "main.py" 之类, 供输出定位与文件名搜索)
        foreach (Match m in Regex.Matches(input,
                     @"\b[\w\u4e00-\u9fff-]+\.(py|cs|js|ts|md|txt|json|java|go|xaml|html|css|sql|xml|yaml|yml|toml|ini|cfg|bat|sh|ps1|csproj|sln|db)\b",
                     RegexOptions.IgnoreCase))
        {
            result.Add(m.Value);
        }

        // 4. 去重 + 去掉被更长候选包含的前缀片段
        //    如引号路径 "D:\my docs\report.md" 会被盘符正则额外提取出 "D:\my"(空格截断),
        //    前者包含后者且后续字符为分隔符/空格 → 丢弃短片段
        var distinct = result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return distinct
            .Where(c => !distinct.Any(o =>
                o.Length > c.Length
                && o.StartsWith(c, StringComparison.OrdinalIgnoreCase)
                && o[c.Length] is ' ' or '/' or '\\'))
            .ToList();
    }

    /// <summary>
    /// 解析输出目标路径(优先级从高到低):
    /// 1. 显式 output 参数(节点/模板指定)
    /// 2. 用户输入中的路径候选(文件可尚不存在, 如 "生成 D:\proj\newfile.py")
    /// 3. 用户输入中的已存在目录 + 默认文件名(如 "放到 D:\docs")
    /// 4. 工作区根目录 + 默认文件名
    /// 用户/模板显式给出的位置允许在工作区外(用户意图优先), 默认落点必须留在工作区内。
    /// 失败返回 null 并给出 error。
    /// </summary>
    public static string? ResolveOutputPath(string? explicitOutput, string userInput,
        string workspaceRoot, string defaultFileName, out string? error)
    {
        error = null;
        // (路径, 是否用户/模板显式指定): 显式候选优先于工作区组合候选
        var candidates = new List<(string Path, bool UserExplicit)>();

        // 1. 显式 output 参数(规范为绝对路径)
        if (!string.IsNullOrWhiteSpace(explicitOutput))
        {
            var eo = explicitOutput.Trim().Trim('"', '\'');
            var full = Path.IsPathRooted(eo)
                ? Path.GetFullPath(eo)
                : Path.Combine(workspaceRoot, eo);
            candidates.Add((full, true));
        }

        // 2. 用户输入中的路径候选(保持出现顺序)
        foreach (var tok in ExtractPathTokens(userInput))
        {
            if (Path.IsPathRooted(tok))
            {
                // 绝对路径: 已存在目录→目录+默认名; 带扩展名→文件; 无扩展名→按目录处理("放到 D:\outdir")
                if (Directory.Exists(tok))
                    candidates.Add((Path.Combine(Path.GetFullPath(tok), SanitizeFileName(defaultFileName)), true));
                else if (Path.HasExtension(tok))
                    candidates.Add((Path.GetFullPath(tok), true));
                else
                    candidates.Add((Path.Combine(Path.GetFullPath(tok), SanitizeFileName(defaultFileName)), true));
            }
            else if (LooksLikeFileRef(tok))
            {
                candidates.Add((Path.Combine(workspaceRoot, tok), false));
            }
        }

        string resolved;
        if (candidates.Count > 0)
        {
            // 显式候选优先; 多个时取最后一个(通常最后的提及才是输出目标,
            // 如 "根据 user.py 生成 main.py" → main.py)
            resolved = candidates
                    .Where(c => c.UserExplicit)
                    .LastOrDefault(c => !Directory.Exists(c.Path) && LooksLikeFileRef(c.Path)).Path
                ?? candidates.LastOrDefault(c => !Directory.Exists(c.Path) && LooksLikeFileRef(c.Path)).Path
                ?? candidates[0].Path;

            // 候选解析后仍是目录(如输入只给了目录) → 追加默认文件名
            if (!string.IsNullOrEmpty(resolved) && Directory.Exists(resolved))
                resolved = Path.Combine(resolved, SanitizeFileName(defaultFileName));
        }
        else
        {
            resolved = Path.Combine(workspaceRoot, SanitizeFileName(defaultFileName));
        }

        // 用户/模板显式指定的位置不受工作区边界限制, 但仍禁止系统保护文件与目录穿越
        return EnsureSafeOutput(ref resolved, workspaceRoot,
            enforceWorkspace: !candidates.Any(c => c.UserExplicit), out error)
            ? resolved : null;
    }

    /// <summary>
    /// 将 LLM 提出的文件路径(相对或绝对)映射为工作区内的安全绝对路径。
    /// 相对路径基于 workspaceRoot 解析; 自动创建父目录; 越界/保护文件返回 null。
    /// LLM 提出的路径必须留在工作区内(设计文档: Agent 只能操作工作区内的文件)。
    /// </summary>
    public static string? MapToWorkspace(string? proposedPath, string workspaceRoot, out string? error)
    {
        error = null;
        var p = proposedPath?.Trim().Trim('"', '\'', '`');
        if (string.IsNullOrWhiteSpace(p)) { error = "路径为空"; return null; }

        string full;
        try
        {
            full = Path.IsPathRooted(p)
                ? Path.GetFullPath(p)
                : Path.GetFullPath(Path.Combine(workspaceRoot, p));
        }
        catch (Exception ex) { error = $"路径无效: {ex.Message}"; return null; }

        return EnsureSafeOutput(ref full, workspaceRoot, enforceWorkspace: true, out error) ? full : null;
    }

    /// <summary>
    /// 输出安全校验:
    /// - 禁止写入系统保护文件
    /// - enforceWorkspace=true 时禁止越出任务工作区
    /// - 自动创建父目录
    /// </summary>
    public static bool EnsureSafeOutput(ref string path, string workspaceRoot,
        bool enforceWorkspace, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(path)) { error = "路径为空"; return false; }

        try
        {
            var full = Path.GetFullPath(path);

            if (SystemProtectedFiles.Contains(Path.GetFileName(full)))
            {
                error = $"'{Path.GetFileName(full)}' 是系统保护文件, 禁止写入";
                return false;
            }

            if (enforceWorkspace && !string.IsNullOrWhiteSpace(workspaceRoot))
            {
                var rootFull = Path.GetFullPath(workspaceRoot);
                var rel = Path.GetRelativePath(rootFull, full);
                // 越出工作区: 相对路径以 .. 开头, 或跨盘符(GetRelativePath 返回绝对路径)
                if (rel == ".." || rel.StartsWith("..\\") || rel.StartsWith("../")
                    || Path.IsPathRooted(rel))
                {
                    error = $"输出路径越出工作区: {full} (工作区: {rootFull})";
                    return false;
                }
            }

            var dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            path = full;
            return true;
        }
        catch (Exception ex)
        {
            error = $"路径无效: {ex.Message}";
            return false;
        }
    }

    // ===== 内部辅助 =====

    private static bool IsExisting(string path)
    {
        try { return File.Exists(path) || Directory.Exists(path); }
        catch { return false; }
    }

    /// <summary>判断字符串是否像路径: 含分隔符 / 盘符 / 带文件扩展名</summary>
    private static bool LooksLikePath(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        return s.Contains(Path.DirectorySeparatorChar)
            || s.Contains(Path.AltDirectorySeparatorChar)
            || (s.Length >= 2 && char.IsLetter(s[0]) && s[1] == ':')
            || Path.HasExtension(s.TrimEnd('.'));
    }

    /// <summary>判断是否像文件引用(带扩展名 或 含分隔符), 用于输出候选筛选</summary>
    private static bool LooksLikeFileRef(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        return s.Contains(Path.DirectorySeparatorChar)
            || s.Contains(Path.AltDirectorySeparatorChar)
            || Path.HasExtension(s.TrimEnd('.'));
    }

    /// <summary>从任意文本中取"文件名"部分(分隔符后的最后一段)</summary>
    private static string GetFileNamePart(string text)
    {
        var normalized = text.Replace('/', Path.DirectorySeparatorChar).TrimEnd(Path.DirectorySeparatorChar);
        var name = Path.GetFileName(normalized);
        return string.IsNullOrWhiteSpace(name) ? string.Empty : name;
    }

    /// <summary>文件名净化: 去掉路径分隔符与非法字符, 防止默认文件名逃逸目录</summary>
    private static string SanitizeFileName(string name)
    {
        var cleaned = new string(name.Where(c =>
            !Path.GetInvalidFileNameChars().Contains(c)
            && c != Path.DirectorySeparatorChar && c != Path.AltDirectorySeparatorChar).ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? "output.txt" : cleaned;
    }

    /// <summary>排除 bin/obj/.git 等构建与元数据目录(与各 Agent 保持一致)</summary>
    private static bool IsExcluded(string fullPath, string root)
    {
        try
        {
            var rel = Path.GetRelativePath(root, fullPath);
            return rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(seg => ExcludedDirs.Contains(seg));
        }
        catch { return true; }
    }

    private static readonly HashSet<string> ExcludedDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", ".vs", ".git", ".floatingmind", ".agents", ".deepcode",
        "node_modules", "packages", "__pycache__", "venv", ".venv",
        "Debug", "Release", "dist", "build"
    };
}
