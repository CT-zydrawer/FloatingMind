using System.Collections.Concurrent;
using FloatingMind.Models.Blackboard;
using Newtonsoft.Json;

namespace FloatingMind.Services;

/// <summary>
/// 5. Blackboard系统 —— 当前任务协作状态
/// 不保存长期知识, 只负责当前任务。
/// 五类数据: Observation/Fact/Hypothesis/Conflict/Decision
/// </summary>
public class BlackboardSystem
{
    private readonly ConcurrentDictionary<string, List<BlackboardEntry>> _taskBoards = new();
    private readonly object _lock = new();

    public event Action<string, BlackboardEntry>? OnEntryAdded;
    public event Action<string, BlackboardEntry>? OnEntryUpdated;
    public event Action? OnCleared;

    // ===== 获取/初始化 =====
    private List<BlackboardEntry> GetBoard(string taskId)
    {
        return _taskBoards.GetOrAdd(taskId, _ => new List<BlackboardEntry>());
    }

    public IReadOnlyList<BlackboardEntry> GetAll(string taskId) => GetBoard(taskId).AsReadOnly();

    public IReadOnlyList<T> GetByType<T>(string taskId) where T : BlackboardEntry
        => GetBoard(taskId).OfType<T>().ToList().AsReadOnly();

    // ===== Observation (Agent可写) =====
    public Observation AddObservation(string taskId, string content, string author, string source = "")
    {
        var obs = new Observation
        {
            Content = content,
            Author = author,
            Source = string.IsNullOrEmpty(source) ? author : source
        };
        GetBoard(taskId).Add(obs);
        OnEntryAdded?.Invoke(taskId, obs);
        return obs;
    }

    // ===== Fact (仅Supervisor/Validator可写) =====
    public Fact AddFact(string taskId, string content, string approvedBy, List<string>? evidence = null)
    {
        var fact = new Fact
        {
            Content = content,
            Author = approvedBy,
            ApprovedBy = approvedBy,
            Evidence = evidence ?? new()
        };
        GetBoard(taskId).Add(fact);
        OnEntryAdded?.Invoke(taskId, fact);
        return fact;
    }

    public Fact? PromoteHypothesis(string taskId, Hypothesis hypothesis, string approvedBy)
    {
        var fact = new Fact
        {
            Content = hypothesis.Content,
            Author = approvedBy,
            ApprovedBy = approvedBy,
            Evidence = new List<string> { hypothesis.Id }
        };
        GetBoard(taskId).Add(fact);
        hypothesis.PromotedToFactId = fact.Id;
        OnEntryAdded?.Invoke(taskId, fact);
        return fact;
    }

    // ===== Hypothesis (Agent可提交) =====
    public Hypothesis AddHypothesis(string taskId, string content, string author, double confidence = 0.5)
    {
        var hyp = new Hypothesis
        {
            Content = content,
            Author = author,
            Confidence = Math.Clamp(confidence, 0, 1)
        };
        GetBoard(taskId).Add(hyp);
        OnEntryAdded?.Invoke(taskId, hyp);
        return hyp;
    }

    // ===== Conflict =====
    public ConflictEntry AddConflict(string taskId, string topic, List<string> items)
    {
        var conflict = new ConflictEntry
        {
            Topic = topic,
            Items = items,
            Status = "Open"
        };
        GetBoard(taskId).Add(conflict);
        OnEntryAdded?.Invoke(taskId, conflict);
        return conflict;
    }

    public void ResolveConflict(string taskId, string conflictId, string resolution)
    {
        var board = GetBoard(taskId);
        var conflict = board.OfType<ConflictEntry>().FirstOrDefault(c => c.Id == conflictId);
        if (conflict != null)
        {
            conflict.Status = "Resolved";
            conflict.Resolution = resolution;
            OnEntryUpdated?.Invoke(taskId, conflict);
        }
    }

    // ===== Decision =====
    public Decision AddDecision(string taskId, string topic, string choice, string reason, List<string> basedOn)
    {
        var decision = new Decision
        {
            Topic = topic,
            Choice = choice,
            Reason = reason,
            BasedOn = basedOn,
            Author = "Supervisor"
        };
        GetBoard(taskId).Add(decision);
        OnEntryAdded?.Invoke(taskId, decision);
        return decision;
    }

    // ===== 项目画像 (Analyzer提交, 其他Agent解读) =====
    /// <summary>
    /// Analyzer 将项目结构化画像提交到黑板(Observation + JsonPayload)。
    /// 其他Agent通过 GetProjectProfile 解读, 如 CommandAgent 消费 validation 命令。
    /// </summary>
    public Observation AddProjectProfile(string taskId, ProjectProfile profile, string author)
    {
        var obs = new Observation
        {
            Content = $"项目画像: {profile.Project} ({profile.Language}/{profile.Framework}) " +
                      $"工程{profile.Projects.Count}个 测试工程{profile.TestProjects.Count}个",
            Author = author,
            Source = author,
            JsonPayload = profile.ToJson()
        };
        GetBoard(taskId).Add(obs);
        OnEntryAdded?.Invoke(taskId, obs);
        return obs;
    }

    /// <summary>读取任务黑板上最近一次提交的项目画像, 供其他Agent解读; 无则返回null</summary>
    public ProjectProfile? GetProjectProfile(string taskId)
    {
        var board = GetBoard(taskId);
        foreach (var entry in board.AsEnumerable().Reverse())
        {
            if (entry is Observation obs && !string.IsNullOrEmpty(obs.JsonPayload))
            {
                try
                {
                    var profile = JsonConvert.DeserializeObject<ProjectProfile>(obs.JsonPayload);
                    if (profile != null) return profile;
                }
                catch { /* 非画像条目, 忽略 */ }
            }
        }
        return null;
    }

    // ===== 清理 =====
    public void Clear(string taskId)
    {
        if (_taskBoards.TryRemove(taskId, out _))
            OnCleared?.Invoke();
    }

    // ===== 快照(上下文最小化) =====
    public string GetSummary(string taskId)
    {
        var board = GetBoard(taskId);
        var facts = board.OfType<Fact>().Select(f => f.Content);
        var decisions = board.OfType<Decision>().Select(d => $"[{d.Topic}] → {d.Choice}");
        var conflicts = board.OfType<ConflictEntry>().Where(c => c.Status == "Open")
            .Select(c => $"⚠ {c.Topic}: {string.Join(" vs ", c.Items)}");

        var profile = GetProjectProfile(taskId);
        var profileLine = profile != null
            ? $"◆ 项目画像: {profile.Project} ({profile.Language}/{profile.Framework}) 工程[{string.Join(", ", profile.Projects)}]"
            : string.Empty;

        return string.Join("\n",
            (profileLine.Length > 0 ? new[] { profileLine } : Array.Empty<string>()).Concat(
            facts.Select(f => $"✓ Fact: {f}")).Concat(
            decisions.Select(d => $"● Decision: {d}")).Concat(
            conflicts.Select(c => $"⚠ Conflict: {c}")));
    }
}
