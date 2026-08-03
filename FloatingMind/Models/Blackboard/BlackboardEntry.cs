using Newtonsoft.Json;

namespace FloatingMind.Models.Blackboard;

/// <summary>
/// Blackboard条目基类 —— 任务协作状态数据
/// </summary>
public abstract class BlackboardEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string Type { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.Now;

    public override string ToString() => $"[{Type}] {Content}";
}

/// <summary>
/// 5.1 Observation —— Agent发现的信息(Agent可写)
/// </summary>
public class Observation : BlackboardEntry
{
    public Observation()
    {
        Type = "Observation";
    }
    public string Source { get; set; } = string.Empty; // 来源Agent名
    public string Scope { get; set; } = string.Empty;  // 相关文件/模块
}

/// <summary>
/// 5.2 Fact —— 已确认事实(仅Supervisor/Validator可写)
/// </summary>
public class Fact : BlackboardEntry
{
    public Fact()
    {
        Type = "Fact";
    }
    public string ApprovedBy { get; set; } = "Validator";
    public List<string> Evidence { get; set; } = new(); // 支撑证据ID
}

/// <summary>
/// 5.3 Hypothesis —— 未确认推测(Agent可提交)
/// </summary>
public class Hypothesis : BlackboardEntry
{
    public Hypothesis()
    {
        Type = "Hypothesis";
    }
    public double Confidence { get; set; } // 0.0~1.0
    public string? PromotedToFactId { get; set; } // 提升为Fact后的ID
}

/// <summary>
/// 5.4 Conflict —— 冲突记录
/// </summary>
public class ConflictEntry : BlackboardEntry
{
    public ConflictEntry()
    {
        Type = "Conflict";
    }
    public string Topic { get; set; } = string.Empty;
    public List<string> Items { get; set; } = new();
    public string Status { get; set; } = "Open"; // Open/Resolved
    public string? Resolution { get; set; }
}

/// <summary>
/// 5.5 Decision —— 最终决策
/// </summary>
public class Decision : BlackboardEntry
{
    public Decision()
    {
        Type = "Decision";
    }
    public string Topic { get; set; } = string.Empty;
    public string Choice { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public List<string> BasedOn { get; set; } = new(); // 支撑Fact ID列表
}
