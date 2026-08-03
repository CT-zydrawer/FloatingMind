using FloatingMind.Models.Journal;
using Newtonsoft.Json;

namespace FloatingMind.Services;

/// <summary>
/// 9. 回滚系统 —— 文件备份恢复、操作反向
/// </summary>
public class RollbackManager
{
    private readonly string _backupPath;
    private readonly string _recycleBinPath;
    private readonly Dictionary<string, string> _fileBackups = new(); // filePath -> backupPath
    private readonly List<RollbackRecord> _records = new();

    public RollbackManager(string basePath)
    {
        _backupPath = Path.Combine(basePath, ".floatingmind", "backups");
        _recycleBinPath = Path.Combine(basePath, ".floatingmind", "recyclebin");
        Directory.CreateDirectory(_backupPath);
        Directory.CreateDirectory(_recycleBinPath);
    }

    // === 文件备份 ===
    public string Backup(string filePath)
    {
        if (!File.Exists(filePath)) return string.Empty;

        var backupName = $"{Path.GetFileName(filePath)}.{DateTime.Now:yyyyMMddHHmmss}.bak";
        var backupPath = Path.Combine(_backupPath, backupName);
        File.Copy(filePath, backupPath, true);
        _fileBackups[filePath] = backupPath;

        _records.Add(new RollbackRecord
        {
            Type = "Backup",
            Target = filePath,
            BackupPath = backupPath,
            Timestamp = DateTime.Now
        });

        return backupPath;
    }

    // === 文件恢复 ===
    public bool Restore(string filePath)
    {
        if (!_fileBackups.TryGetValue(filePath, out var backupPath))
            return false;

        if (!File.Exists(backupPath)) return false;

        File.Copy(backupPath, filePath, true);
        _records.Add(new RollbackRecord
        {
            Type = "Restore",
            Target = filePath,
            BackupPath = backupPath,
            Timestamp = DateTime.Now
        });

        return true;
    }

    // === 删除移到回收站(Pending) ===
    public string MoveToRecycleBin(string filePath)
    {
        if (!File.Exists(filePath)) return string.Empty;

        var recycleName = $"{Path.GetFileName(filePath)}.{DateTime.Now:yyyyMMddHHmmss}.del";
        var recyclePath = Path.Combine(_recycleBinPath, recycleName);
        File.Move(filePath, recyclePath);

        _records.Add(new RollbackRecord
        {
            Type = "RecycleBin",
            Target = filePath,
            BackupPath = recyclePath,
            Timestamp = DateTime.Now
        });

        return recyclePath;
    }

    // === 根据Journal反向操作 ===
    public bool RollbackByJournal(List<JournalEntry> journalEntries)
    {
        bool success = true;
        // 反向遍历Journal
        foreach (var entry in journalEntries.OrderByDescending(j => j.Timestamp))
        {
            if (entry.RolledBack) continue;
            if (!entry.IsReversible) continue;

            if (entry.Event == "FileWrite" && !string.IsNullOrEmpty(entry.BeforeState))
            {
                try
                {
                    File.WriteAllText(entry.Target, entry.BeforeState);
                    entry.RolledBack = true;
                }
                catch { success = false; }
            }
        }
        return success;
    }

    public IReadOnlyList<RollbackRecord> Records => _records.AsReadOnly();
}

public class RollbackRecord
{
    public string Type { get; set; } = string.Empty; // Backup/Restore/RecycleBin
    public string Target { get; set; } = string.Empty;
    public string BackupPath { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
}
