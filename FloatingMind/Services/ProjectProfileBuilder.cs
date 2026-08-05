using FloatingMind.Models.Blackboard;

namespace FloatingMind.Services;

/// <summary>
/// 项目画像构建器 —— Analyzer(CodeAgent analyze)使用的规则提取逻辑。
/// 全部本地规则、零API成本(设计文档: 成本可控)。
/// 提取: 工程文件(*.csproj/*.sln) / 测试工程 / 语言 / 框架(TargetFramework/requires-python) / 验证命令。
/// </summary>
public static class ProjectProfileBuilder
{
    /// <summary>基于工作区目录规则提取项目结构化画像</summary>
    public static ProjectProfile Build(string workspaceRoot, ProjectLanguage language)
    {
        var profile = new ProjectProfile
        {
            Project = new DirectoryInfo(workspaceRoot).Name,
            Language = language switch
            {
                ProjectLanguage.CSharp => "CSharp",
                ProjectLanguage.Python => "Python",
                ProjectLanguage.JavaScript => "JavaScript",
                ProjectLanguage.TypeScript => "TypeScript",
                ProjectLanguage.Java => "Java",
                ProjectLanguage.Go => "Go",
                _ => "Unknown"
            }
        };

        // 1. 工程文件 + 测试工程
        var projectFiles = EnumerateProjectFiles(workspaceRoot).ToList();
        foreach (var pf in projectFiles)
        {
            var rel = Path.GetRelativePath(workspaceRoot, pf).Replace('\\', '/');
            var name = Path.GetFileNameWithoutExtension(pf);
            if (IsTestProjectName(name) || rel.Contains("/test", StringComparison.OrdinalIgnoreCase)
                || rel.Contains("/tests/", StringComparison.OrdinalIgnoreCase))
            {
                profile.TestProjects.Add(rel);
            }
            else
            {
                profile.Projects.Add(rel);
            }
        }

        // 2. 框架
        profile.Framework = DetectFramework(workspaceRoot, language, projectFiles);

        // 3. 验证命令
        profile.Validation = BuildValidationCommands(workspaceRoot, language, profile);

        return profile;
    }

    public static IEnumerable<string> EnumerateProjectFiles(string root)
    {
        if (!Directory.Exists(root)) return Array.Empty<string>();
        var patterns = new[] { "*.csproj", "*.sln", "*.fsproj", "*.vbproj" };
        return patterns
            .SelectMany(p => Directory.EnumerateFiles(root, p, SearchOption.AllDirectories))
            .Where(f => !IsBuildArtifactPath(root, f))
            .ToList();
    }

    private static bool IsBuildArtifactPath(string root, string fullPath)
    {
        try
        {
            return Path.GetRelativePath(root, fullPath)
                .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(seg => seg is "bin" or "obj" or ".vs" or ".git" or "packages" or "node_modules");
        }
        catch { return true; }
    }

    private static bool IsTestProjectName(string name) =>
        name.Contains("Test", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Tests", StringComparison.OrdinalIgnoreCase);

    private static string DetectFramework(string root, ProjectLanguage language,
        IEnumerable<string> projectFiles)
    {
        // .NET: 读第一个 csproj 的 TargetFramework
        var csproj = projectFiles.FirstOrDefault(f => f.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase));
        if (csproj != null)
        {
            try
            {
                var text = File.ReadAllText(csproj);
                var m = System.Text.RegularExpressions.Regex.Match(text,
                    @"<TargetFramework[s]?>([^<]+)</TargetFramework[s]?>",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (m.Success)
                {
                    var tf = m.Groups[1].Value.Trim();
                    return tf switch
                    {
                        "net10.0" or "net10.0-windows" => ".NET 10",
                        "net9.0" or "net9.0-windows" => ".NET 9",
                        "net8.0" or "net8.0-windows" => ".NET 8",
                        "net7.0" => ".NET 7",
                        "net6.0" => ".NET 6",
                        "net48" or "net472" => $".NET Framework {tf[3..]}",
                        _ => $"C# (TargetFramework={tf})"
                    };
                }
            }
            catch { }
            return "C#";
        }

        switch (language)
        {
            case ProjectLanguage.Python:
                // pyproject.toml 的 requires-python, 或直接 Python
                try
                {
                    var py = Path.Combine(root, "pyproject.toml");
                    if (File.Exists(py))
                    {
                        var m = System.Text.RegularExpressions.Regex.Match(File.ReadAllText(py),
                            @"requires-python\s*=\s*""([^""]+)""");
                        if (m.Success) return $"Python {m.Groups[1].Value}";
                    }
                }
                catch { }
                return "Python";
            case ProjectLanguage.JavaScript:
                return "Node.js";
            case ProjectLanguage.TypeScript:
                return "TypeScript";
            case ProjectLanguage.Java:
                if (File.Exists(Path.Combine(root, "pom.xml"))) return "Java (Maven)";
                if (File.Exists(Path.Combine(root, "build.gradle"))) return "Java (Gradle)";
                return "Java";
            case ProjectLanguage.Go:
                return "Go";
            default:
                return string.Empty;
        }
    }

    private static List<ValidationCommand> BuildValidationCommands(string root,
        ProjectLanguage language, ProjectProfile profile)
    {
        var result = new List<ValidationCommand>();

        switch (language)
        {
            case ProjectLanguage.CSharp:
                // 优先解决方案, 其次主工程
                var sln = profile.Projects.FirstOrDefault(p => p.EndsWith(".sln", StringComparison.OrdinalIgnoreCase));
                var mainProject = profile.Projects.FirstOrDefault(p => p.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase));
                if (sln != null)
                    result.Add(new ValidationCommand { Type = "build", Command = $"dotnet build {sln}" });
                else if (mainProject != null)
                    result.Add(new ValidationCommand { Type = "build", Command = $"dotnet build {mainProject}" });
                else
                    result.Add(new ValidationCommand { Type = "build", Command = "dotnet build" });
                foreach (var test in profile.TestProjects.Take(1))
                    result.Add(new ValidationCommand { Type = "test", Command = $"dotnet test {test}" });
                break;

            case ProjectLanguage.Python:
                result.Add(new ValidationCommand { Type = "compile", Command = "python -m compileall ." });
                break;

            case ProjectLanguage.TypeScript:
                result.Add(new ValidationCommand { Type = "compile", Command = "npx tsc --noEmit" });
                break;

            case ProjectLanguage.JavaScript:
                result.Add(new ValidationCommand { Type = "test", Command = "npm test" });
                break;

            case ProjectLanguage.Java:
                var gradle = File.Exists(Path.Combine(root, "build.gradle"));
                result.Add(new ValidationCommand
                {
                    Type = "test",
                    Command = gradle ? "gradle test" : "mvn test"
                });
                break;

            case ProjectLanguage.Go:
                result.Add(new ValidationCommand { Type = "build", Command = "go build ./..." });
                break;
        }

        return result;
    }
}
