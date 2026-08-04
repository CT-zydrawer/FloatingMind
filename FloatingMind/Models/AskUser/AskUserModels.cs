namespace FloatingMind.Models.AskUser;

/// <summary>
/// 单个选项 —— 对应 TS schema 中的 option: { label, description }
/// </summary>
public class QuestionOption
{
    public string Label { get; set; } = "";
    public string Description { get; set; } = "";
}

/// <summary>
/// 单个问题 —— 对应 TS schema 中的 question: { question, multiSelect, options }
/// </summary>
public class QuestionItem
{
    public string Question { get; set; } = "";
    public bool MultiSelect { get; set; }
    public List<QuestionOption> Options { get; set; } = new();
}

/// <summary>
/// 提问请求 —— 对应 TS schema 中的顶层 { questions: [...] }
/// </summary>
public class AskUserRequest
{
    public List<QuestionItem> Questions { get; set; } = new();
}

/// <summary>
/// 单个问题的回答 —— 用户选中的选项标签列表
/// </summary>
public class QuestionAnswer
{
    public string Question { get; set; } = "";
    public List<string> SelectedLabels { get; set; } = new();
}

/// <summary>
/// 提问响应 —— 包含所有问题的回答, 以及是否取消
/// </summary>
public class AskUserResponse
{
    public List<QuestionAnswer> Answers { get; set; } = new();
    public bool IsCancelled { get; set; }

    /// <summary>
    /// 将响应转换为可读文本, 供 SupervisorAgent 拼接到用户输入中
    /// </summary>
    public string ToText()
    {
        if (IsCancelled || Answers.Count == 0) return "";

        var parts = new List<string>();
        foreach (var ans in Answers)
        {
            if (ans.SelectedLabels.Count > 0)
                parts.Add($"{ans.Question} → {string.Join(", ", ans.SelectedLabels)}");
        }
        return string.Join("\n", parts);
    }
}
