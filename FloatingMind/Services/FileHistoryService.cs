using Microsoft.Data.Sqlite;

namespace FloatingMind.Services;

/// <summary>
/// 文件历史服务 —— SQLite 记录每次文件修改的 before/after 快照, 支持回溯到修改前
/// 数据库位置: {appDataRoot}/.floatingmind/data/file_history.db
/// </summary>
public class FileHistoryService
{
    private readonly string _dbPath;
    private readonly JournalSystem _journal;

    public FileHistoryService(string appDataRoot, JournalSystem journal)
    {
        _dbPath = Path.Combine(appDataRoot, ".floatingmind", "data", "file_history.db");
        _journal = journal;
        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
        Init();
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        return conn;
    }

    private void Init()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS FileSnapshots (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                TaskId TEXT NOT NULL,
                Agent TEXT NOT NULL,
                FilePath TEXT NOT NULL,
                BeforeContent TEXT,
                AfterContent TEXT,
                Reason TEXT,
                CreatedAt TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_FileSnapshots_FilePath ON FileSnapshots(FilePath);
            CREATE INDEX IF NOT EXISTS IX_FileSnapshots_TaskId ON FileSnapshots(TaskId);
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>记录一次文件写入快照(before=修改前内容, after=修改后内容)</summary>
    public async Task<long> RecordWriteAsync(string taskId, string agent, string filePath,
        string? beforeContent, string afterContent, string reason = "")
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO FileSnapshots (TaskId, Agent, FilePath, BeforeContent, AfterContent, Reason, CreatedAt)
            VALUES ($taskId, $agent, $path, $before, $after, $reason, $created)
            RETURNING Id;
            """;
        cmd.Parameters.AddWithValue("$taskId", taskId ?? "");
        cmd.Parameters.AddWithValue("$agent", agent ?? "");
        cmd.Parameters.AddWithValue("$path", filePath ?? "");
        cmd.Parameters.AddWithValue("$before", beforeContent ?? "");
        cmd.Parameters.AddWithValue("$after", afterContent ?? "");
        cmd.Parameters.AddWithValue("$reason", reason ?? "");
        cmd.Parameters.AddWithValue("$created", DateTime.Now.ToString("O"));
        return (long)(await cmd.ExecuteScalarAsync() ?? 0L);
    }

    /// <summary>查询文件历史(可选按文件过滤, 按时间倒序)</summary>
    public List<FileSnapshot> GetHistory(string? filePath = null)
    {
        var result = new List<FileSnapshot>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = filePath == null
            ? "SELECT Id, TaskId, Agent, FilePath, BeforeContent, AfterContent, Reason, CreatedAt FROM FileSnapshots ORDER BY Id DESC LIMIT 500;"
            : "SELECT Id, TaskId, Agent, FilePath, BeforeContent, AfterContent, Reason, CreatedAt FROM FileSnapshots WHERE FilePath = $path ORDER BY Id DESC LIMIT 500;";
        if (filePath != null) cmd.Parameters.AddWithValue("$path", filePath);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new FileSnapshot
            {
                Id = reader.GetInt64(0),
                TaskId = reader.GetString(1),
                Agent = reader.GetString(2),
                FilePath = reader.GetString(3),
                BeforeContent = reader.IsDBNull(4) ? "" : reader.GetString(4),
                AfterContent = reader.IsDBNull(5) ? "" : reader.GetString(5),
                Reason = reader.IsDBNull(6) ? "" : reader.GetString(6),
                CreatedAt = DateTime.TryParse(reader.GetString(7), out var t) ? t : DateTime.MinValue
            });
        }
        return result;
    }

    /// <summary>按快照ID查询</summary>
    public FileSnapshot? GetSnapshot(long id)
        => GetHistory().FirstOrDefault(s => s.Id == id);

    /// <summary>
    /// 回溯: 将文件恢复为该快照的修改前状态(before 内容)。
    /// 失败返回 false, 并给出原因。
    /// </summary>
    public async Task<(bool Ok, string Reason)> RestoreAsync(long id)
    {
        var snap = GetSnapshot(id);
        if (snap == null)
            return (false, "快照不存在");

        if (string.IsNullOrWhiteSpace(snap.FilePath) || !File.Exists(snap.FilePath))
            return (false, $"目标文件不存在: {snap.FilePath}");

        if (IsSystemProtectedFile(snap.FilePath))
            return (false, $"'{snap.FilePath}' 属于系统保护文件, 不允许恢复");

        try
        {
            await File.WriteAllTextAsync(snap.FilePath, snap.BeforeContent ?? "");
            _journal.LogAgentAction("FileHistory", "Restore",
                $"{snap.FilePath} 恢复到快照#{id} ({snap.CreatedAt:MM-dd HH:mm:ss})");
            return (true, $"已恢复: {snap.FilePath}");
        }
        catch (Exception ex)
        {
            return (false, $"恢复失败: {ex.Message}");
        }
    }

    private static bool IsSystemProtectedFile(string path)
    {
        var fileName = Path.GetFileName(path);
        return SystemProtectedFiles.Contains(fileName);
    }

    private static readonly HashSet<string> SystemProtectedFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "Generated.cs", "MainWindow.xaml.cs", "MainWindow.xaml", "App.xaml.cs",
        "App.xaml", "FloatingMind.csproj", "Program.cs", "AssemblyInfo.cs"
    };
}

/// <summary>一次文件修改快照记录</summary>
public class FileSnapshot
{
    public long Id { get; set; }
    public string TaskId { get; set; } = "";
    public string Agent { get; set; } = "";
    public string FilePath { get; set; } = "";
    public string BeforeContent { get; set; } = "";
    public string AfterContent { get; set; } = "";
    public string Reason { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}
