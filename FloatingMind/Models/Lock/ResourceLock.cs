namespace FloatingMind.Models.Lock;

/// <summary>
/// 6.3 Resource Lock —— 资源锁(锁属于资源,不属于Agent)
/// </summary>
public class ResourceLock
{
    public string Resource { get; set; } = string.Empty; // "/src/User.cs"
    public string Owner { get; set; } = string.Empty;     // "CodeAgent"
    public int ExpireSeconds { get; set; } = 300;
    public DateTime AcquiredAt { get; set; } = DateTime.Now;

    public bool IsExpired => (DateTime.Now - AcquiredAt).TotalSeconds > ExpireSeconds;
}
