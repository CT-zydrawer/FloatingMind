using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace FloatingMind.Services
{
    public static class PythonBugChecker
    {
        public static (bool HasBug, string Message) Check(string filePath)
        {
            if (!File.Exists(filePath))
                return (true, $"File not found: {filePath}");

            if (!filePath.EndsWith(".py", StringComparison.OrdinalIgnoreCase))
                return (true, "File is not a Python script.");

            var syntaxResult = CheckSyntaxViaCompiler(filePath);
            if (syntaxResult.HasBug)
                return syntaxResult;

            var styleResult = CheckCommonStyleIssues(filePath);
            if (styleResult.HasBug)
                return styleResult;

            return (false, "No obvious bugs detected.");
        }

        private static (bool HasBug, string Message) CheckSyntaxViaCompiler(string filePath)
        {
            try
            {
                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "python",
                        Arguments = $"-m py_compile \"{filePath}\"",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        StandardOutputEncoding = Encoding.UTF8,
                        StandardErrorEncoding = Encoding.UTF8
                    }
                };
                process.Start();
                string error = process.StandardError.ReadToEnd();
                process.WaitForExit(10000);

                if (process.ExitCode != 0)
                    return (true, $"Syntax error: {error.Trim()}");
                return (false, string.Empty);
            }
            catch (Exception ex)
            {
                return (true, $"Unable to invoke Python compiler: {ex.Message}");
            }
        }

        private static (bool HasBug, string Message) CheckCommonStyleIssues(string filePath)
        {
            var lines = File.ReadAllLines(filePath);
            var issues = new StringBuilder();

            // Check for mixed tabs and spaces
            bool hasTabs = false, hasSpaces = false;
            var indentSizes = new Dictionary<int, int>();
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                int leading = line.Length - line.TrimStart().Length;
                if (leading > 0)
                {
                    if (line[0] == '\t')
                        hasTabs = true;
                    else if (line[0] == ' ')
                        hasSpaces = true;

                    if (!indentSizes.ContainsKey(leading))
                        indentSizes[leading] = 0;
                    indentSizes[leading]++;
                }
            }
            if (hasTabs && hasSpaces)
                issues.AppendLine("Mixed tabs and spaces for indentation.");

            // Check for missing colons after def/if/for/while/class (simple heuristic)
            var controlWords = new[] { "if", "elif", "else", "for", "while", "def", "class", "try", "except", "finally", "with" };
            for (int i = 0; i < lines.Length; i++)
            {
                string trimmed = lines[i].Trim();
                if (trimmed.StartsWith("#"))
                    continue;
                foreach (var word in controlWords)
                {
                    if (Regex.IsMatch(trimmed, $@"\b{word}\b") && !trimmed.EndsWith(":") && !trimmed.Contains("#"))
                    {
                        // Ignore if next line is just a block start with indentation? 
                        // Very rough: if line doesn't end with colon and the word is at the beginning, report.
                        if (Regex.IsMatch(trimmed, $@"^{word}\s") || Regex.IsMatch(trimmed, $@"^{word}\("))
                            issues.AppendLine($"Line {i + 1}: Possible missing colon after '{word}'.");
                    }
                }
            }

            return issues.Length > 0 ? (true, issues.ToString().TrimEnd()) : (false, string.Empty);
        }
    }
}