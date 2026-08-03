using FloatingMind.Models.Config;
using Newtonsoft.Json;

namespace FloatingMind.Data;

/// <summary>
/// 数据持久化服务 —— SQLite-based 配置和缓存存储
/// </summary>
public class DatabaseService
{
    private readonly string _dbPath;
    private readonly string _configPath;

    public DatabaseService(string basePath)
    {
        _dbPath = Path.Combine(basePath, ".floatingmind", "data");
        _configPath = Path.Combine(_dbPath, "config.json");
        Directory.CreateDirectory(_dbPath);
    }

    // === Config ===
    public AppConfig LoadConfig()
    {
        if (File.Exists(_configPath))
        {
            var json = File.ReadAllText(_configPath);
            return JsonConvert.DeserializeObject<AppConfig>(json) ?? new AppConfig();
        }
        return new AppConfig();
    }

    public void SaveConfig(AppConfig config)
    {
        var json = JsonConvert.SerializeObject(config, Formatting.Indented);
        File.WriteAllText(_configPath, json);
    }

    // === Search Cache ===
    public string? GetCachedSearch(string queryHash)
    {
        var cacheFile = Path.Combine(_dbPath, $"cache_{queryHash}.json");
        if (!File.Exists(cacheFile)) return null;

        var cache = JsonConvert.DeserializeObject<CacheEntry>(File.ReadAllText(cacheFile));
        if (cache == null || cache.IsExpired) return null;
        return cache.Result;
    }

    public void SetCachedSearch(string queryHash, string result, int ttlMinutes = 60)
    {
        var cacheFile = Path.Combine(_dbPath, $"cache_{queryHash}.json");
        var entry = new CacheEntry
        {
            QueryHash = queryHash,
            Result = result,
            CachedAt = DateTime.Now,
            TtlMinutes = ttlMinutes
        };
        File.WriteAllText(cacheFile, JsonConvert.SerializeObject(entry));
    }

    // === Workflow Templates ===
    public string? GetCachedWorkflow(string intentHash)
    {
        var cacheFile = Path.Combine(_dbPath, $"wf_{intentHash}.json");
        if (!File.Exists(cacheFile)) return null;
        return File.ReadAllText(cacheFile);
    }

    public void CacheWorkflow(string intentHash, string workflowJson)
    {
        var cacheFile = Path.Combine(_dbPath, $"wf_{intentHash}.json");
        File.WriteAllText(cacheFile, workflowJson);
    }
}

internal class CacheEntry
{
    public string QueryHash { get; set; } = string.Empty;
    public string Result { get; set; } = string.Empty;
    public DateTime CachedAt { get; set; }
    public int TtlMinutes { get; set; } = 60;
    public bool IsExpired => (DateTime.Now - CachedAt).TotalMinutes > TtlMinutes;
}
