using System.Text.RegularExpressions;

namespace FloatingMind.Services;

/// <summary>
/// 路径提取器 —— 从用户输入中提取显式存在的文件/目录路径
/// 用途: 用户输入 "修复 D:\minimind-plus 的python代码" 时, 自动探测出任务工作区
/// </summary>
public static class PathExtractor
{
    /// <summary>
    /// 从用户输入中提取第一个存在的路径(文件或目录)。
    /// 支持: 引号路径("D:\foo\bar")、绝对路径(D:\foo\bar / D:/foo/bar)。
    /// 找不到返回空字符串。
    /// </summary>
    public static string ExtractExistingPath(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;

        // 1. 引号内路径: "D:\foo\bar" 或 'D:\foo\bar'
        foreach (var quote in new[] { '"', '\'' })
        {
            int start = input.IndexOf(quote);
            while (start >= 0)
            {
                int end = input.IndexOf(quote, start + 1);
                if (end > start + 1)
                {
                    var candidate = input[(start + 1)..end].Trim();
                    if (IsExisting(candidate)) return candidate;
                }
                start = input.IndexOf(quote, start + 1);
            }
        }

        // 2. 绝对路径: 盘符开头, 如 D:\minimind-plus
        foreach (Match m in Regex.Matches(input, @"[A-Za-z]:[\\/][^\s""'，。；、]+"))
        {
            var candidate = m.Value.TrimEnd('\\', '/', '.', ',', ';', ')', ']', '}');
            if (IsExisting(candidate)) return candidate;
        }

        return string.Empty;
    }

    private static bool IsExisting(string path)
    {
        try
        {
            return File.Exists(path) || Directory.Exists(path);
        }
        catch
        {
            return false;
        }
    }
}
