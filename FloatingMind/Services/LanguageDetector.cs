namespace FloatingMind.Services;

/// <summary>项目主语言</summary>
public enum ProjectLanguage
{
    Unknown,
    CSharp,
    Python,
    JavaScript,
    TypeScript,
    Java,
    Go,
    FSharp,
    VisualBasic
}

/// <summary>
/// 语言检测器 —— 扫描目录文件扩展名统计, 推断项目主语言。
/// 用于: CodeAgent 按语言枚举文件/调整提示词/选择输出扩展名。
/// </summary>
public static class LanguageDetector
{
    private static readonly string[] SourceExts =
    {
        ".py", ".cs", ".js", ".ts", ".java", ".go",".fs"
    };

    private static readonly HashSet<string> ExcludedDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", ".vs", ".git", ".floatingmind", ".agents", ".deepcode",
        "node_modules", "packages", "__pycache__", "venv", ".venv", "Debug", "Release",
        "dist", "build", ".idea", ".vscode", "model", "models", "output", "logs", "log"
    };

    /// <summary>扫描目录, 按源码扩展名出现次数推断主语言</summary>
    public static ProjectLanguage Detect(string root)
    {
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            return ProjectLanguage.Unknown;

        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var f in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories))
            {
                if (IsExcludedPath(root, f)) continue;
                var ext = Path.GetExtension(f);
                if (SourceExts.Contains(ext, StringComparer.OrdinalIgnoreCase))
                    counts[ext] = counts.GetValueOrDefault(ext) + 1;
            }
        }
        catch { /* 目录不可读时返回 Unknown */ }

        var top = counts.OrderByDescending(kv => kv.Value).FirstOrDefault();
        return top.Key?.ToLowerInvariant() switch
        {
            ".py" => ProjectLanguage.Python,
            ".cs" => ProjectLanguage.CSharp,
            ".js" => ProjectLanguage.JavaScript,
            ".ts" => ProjectLanguage.TypeScript,
            ".java" => ProjectLanguage.Java,
            ".go" => ProjectLanguage.Go,
            ".fs" => ProjectLanguage.FSharp,
            ".vb" =>ProjectLanguage.VisualBasic,
            _ => ProjectLanguage.Unknown
        };
    }

    /// <summary>语言对应的源码文件通配符(供枚举)</summary>
    public static string GetFilePattern(ProjectLanguage lang) => lang switch
    {
        ProjectLanguage.Python => "*.py",
        ProjectLanguage.CSharp => "*.cs",
        ProjectLanguage.JavaScript => "*.js",
        ProjectLanguage.TypeScript => "*.ts",
        ProjectLanguage.Java => "*.java",
        ProjectLanguage.Go => "*.go",
        ProjectLanguage.FSharp =>"*.fs",
        ProjectLanguage.VisualBasic =>"*.vb",
        _ => "*.*"
    };

    /// <summary>语言对应的文件扩展名(供输出文件)</summary>
    public static string GetFileExtension(ProjectLanguage lang) => lang switch
    {
        ProjectLanguage.Python => ".py",
        ProjectLanguage.CSharp => ".cs",
        ProjectLanguage.JavaScript => ".js",
        ProjectLanguage.TypeScript => ".ts",
        ProjectLanguage.Java => ".java",
        ProjectLanguage.Go => ".go",
        ProjectLanguage.FSharp => ".fs",
        ProjectLanguage.VisualBasic =>".vb",
        _ => ".txt"
    };

    /// <summary>语言对应的语法检查命令(保底验证)</summary>
    public static string GetSyntaxCheckCommand(ProjectLanguage lang) => lang switch
    {
        ProjectLanguage.Python => "python -m py_compile ",
        ProjectLanguage.CSharp => "dotnet build",
        ProjectLanguage.FSharp => "dotnet build",
        ProjectLanguage.VisualBasic => "dotnet build",
        _ => string.Empty
    };

    private static bool IsExcludedPath(string root, string fullPath)
    {
        var rel = Path.GetRelativePath(root, fullPath);
        return rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(seg => ExcludedDirs.Contains(seg));
    }
}
