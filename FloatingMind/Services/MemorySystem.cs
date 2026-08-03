using FloatingMind.Models.Memory;
using Newtonsoft.Json;

namespace FloatingMind.Services;

/// <summary>
/// 4. Memory系统 —— 三层记忆管理
/// </summary>
public class MemorySystem
{
    private readonly string _basePath;
    private ProjectMemory? _projectMemory;
    private readonly List<ArchiveMemory> _archiveMemories = new();
    private readonly Dictionary<string, WorkingMemory> _sessionMemories = new();

    public event Action<string>? OnMemoryChanged;

    public MemorySystem(string basePath)
    {
        _basePath = Path.Combine(basePath, ".floatingmind", "memory");
        Directory.CreateDirectory(_basePath);
        LoadProjectMemory();
    }

    // --- 4.1 Project Memory ---
    public ProjectMemory GetProjectMemory() => _projectMemory ??= new ProjectMemory();

    public void UpdateProjectMemory(Action<ProjectMemory> update)
    {
        var pm = GetProjectMemory();
        update(pm);
        pm.LastUpdated = DateTime.Now;
        SaveProjectMemory();
        OnMemoryChanged?.Invoke("ProjectMemory updated");
    }

    public void InitProject(string name, string framework, string language, string root)
    {
        _projectMemory = new ProjectMemory
        {
            Project = name,
            Framework = framework,
            Language = language,
            ProjectRoot = root
        };
        SaveProjectMemory();
    }

    private void SaveProjectMemory()
    {
        var path = Path.Combine(_basePath, "project.json");
        File.WriteAllText(path, JsonConvert.SerializeObject(_projectMemory, Formatting.Indented));
    }

    private void LoadProjectMemory()
    {
        var path = Path.Combine(_basePath, "project.json");
        if (File.Exists(path))
            _projectMemory = JsonConvert.DeserializeObject<ProjectMemory>(File.ReadAllText(path));
    }

    // --- 4.2 Working Memory ---
    public WorkingMemory GetWorkingMemory(string taskId)
    {
        if (!_sessionMemories.ContainsKey(taskId))
            _sessionMemories[taskId] = new WorkingMemory { TaskId = taskId };
        return _sessionMemories[taskId];
    }

    public void SetWorkingContext(string taskId, string description, List<string> files, string stage)
    {
        var wm = GetWorkingMemory(taskId);
        wm.TaskDescription = description;
        wm.RelatedFiles = files;
        wm.CurrentStage = stage;
        OnMemoryChanged?.Invoke($"WorkingMemory: {stage}");
    }

    public void ClearWorkingMemory(string taskId)
    {
        if (_sessionMemories.TryGetValue(taskId, out var wm))
            wm.Clear();
    }

    // --- 4.3 Archive Memory ---
    public void Archive(WorkingMemory wm, string summary, string designRationale, List<string> issues)
    {
        var am = new ArchiveMemory
        {
            TaskId = wm.TaskId,
            TaskSummary = summary,
            WhyThisDesign = designRationale,
            IssuesEncountered = issues,
            CompletedAt = DateTime.Now
        };
        _archiveMemories.Add(am);

        var path = Path.Combine(_basePath, "archive.json");
        File.WriteAllText(path, JsonConvert.SerializeObject(_archiveMemories, Formatting.Indented));

        OnMemoryChanged?.Invoke("ArchiveMemory saved");
    }

    public IReadOnlyList<ArchiveMemory> GetArchiveMemories() => _archiveMemories.AsReadOnly();

    public void LoadArchiveMemories()
    {
        var path = Path.Combine(_basePath, "archive.json");
        if (File.Exists(path))
        {
            var list = JsonConvert.DeserializeObject<List<ArchiveMemory>>(File.ReadAllText(path));
            if (list != null) _archiveMemories.AddRange(list);
        }
    }
}
