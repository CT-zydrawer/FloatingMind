using FloatingMind.Models.Journal;
using Newtonsoft.Json;

namespace FloatingMind.Services;

/// <summary>
/// 8. Journal系统 —— 所有不可逆事件记录
/// </summary>
public class JournalSystem
{
    private readonly string _basePath;
    private readonly List<JournalEntry> _entries = new();
    private readonly object _lock = new();

    public event Action<JournalEntry>? OnEntryAdded;

    public JournalSystem(string basePath)
    {
        _basePath = Path.Combine(basePath, ".floatingmind", "journal");
        Directory.CreateDirectory(_basePath);
        Load();
    }

    public IReadOnlyList<JournalEntry> Entries => _entries.AsReadOnly();

    public JournalEntry Log(JournalEntry entry)
    {
        lock (_lock)
        {
            _entries.Add(entry);
            OnEntryAdded?.Invoke(entry);
            Persist();
            return entry;
        }
    }

    // 便捷方法
    public JournalEntry LogFileWrite(string agent, string filePath, string before, string after)
        => Log(JournalEntry.FileWrite(agent, filePath, before, after));

    public JournalEntry LogCommand(string agent, string command, string result)
        => Log(JournalEntry.CommandExec(agent, command, result));

    public JournalEntry LogDecision(string agent, string topic, string choice, string reason)
        => Log(JournalEntry.DecisionMade(agent, topic, choice, reason));

    public JournalEntry LogAgentAction(string agent, string action, string detail = "")
        => Log(JournalEntry.AgentAction(agent, action, detail));

    public JournalEntry LogValidation(string validator, string result, string detail = "")
        => Log(JournalEntry.ValidationResult(validator, result, detail));

    public JournalEntry LogLock(string agent, string resource, bool acquired)
        => Log(acquired
            ? JournalEntry.LockAcquire(agent, resource)
            : JournalEntry.LockRelease(agent, resource));

    // 按时间范围查询
    public List<JournalEntry> Query(DateTime from, DateTime to, string? agent = null, string? eventType = null)
    {
        lock (_lock)
        {
            var query = _entries.Where(e => e.Timestamp >= from && e.Timestamp <= to);
            if (agent != null) query = query.Where(e => e.Agent == agent);
            if (eventType != null) query = query.Where(e => e.Event == eventType);
            return query.OrderByDescending(e => e.Timestamp).ToList();
        }
    }

    private void Persist()
    {
        var file = Path.Combine(_basePath, $"journal_{DateTime.Now:yyyyMMdd}.json");
        File.WriteAllText(file, JsonConvert.SerializeObject(_entries, Formatting.Indented));
    }

    private void Load()
    {
        var today = Path.Combine(_basePath, $"journal_{DateTime.Now:yyyyMMdd}.json");
        if (File.Exists(today))
        {
            var list = JsonConvert.DeserializeObject<List<JournalEntry>>(File.ReadAllText(today));
            if (list != null) _entries.AddRange(list);
        }
    }
}
