using FloatingMind.Models.Config;

namespace FloatingMind.Services;

/// <summary>
/// 3.4 Model Router —— 根据任务复杂度选择模型
/// 评分: 推理深度30% | 上下文长度20% | 代码需求20% | 专业知识20% | 格式要求10%
/// 0-5: chat | 6-10: reasoner
/// </summary>
public class ModelRouter
{
    private readonly AppConfig _config;

    public ModelRouter(AppConfig config)
    {
        _config = config;
    }

    /// <summary>
    /// 综合评分决定模型
    /// </summary>
    public ModelSelection SelectModel(IntentResult intent, string? stageDescription = null)
    {
        // 因素权重
        double reasoningScore = intent.Complexity * 0.3;       // 推理深度
        double contextScore = Math.Min(5, intent.Summary.Length / 20.0) * 0.2; // 上下文
        double codeScore = (intent.Intent == "Development" ? 5 : 0) * 0.2;    // 代码
        double expertScore = (intent.Complexity > 5 ? 4 : 1) * 0.2;           // 专业知识
        double formatScore = 1 * 0.1;                                          // 格式

        double totalScore = reasoningScore + contextScore + codeScore + expertScore + formatScore;

        string modelName = totalScore <= 5 ? _config.LowCostModel : _config.HighPerformanceModel;

        return new ModelSelection
        {
            ModelName = modelName,
            TotalScore = totalScore,
            Breakdown = new Dictionary<string, double>
            {
                ["推理深度"] = reasoningScore,
                ["上下文长度"] = contextScore,
                ["代码需求"] = codeScore,
                ["专业知识"] = expertScore,
                ["格式要求"] = formatScore
            }
        };
    }

    public ModelSelection SelectCheapest() => new()
    {
        ModelName = _config.LowCostModel,
        TotalScore = 0,
        Breakdown = new()
    };
}

public class ModelSelection
{
    public string ModelName { get; set; } = string.Empty;
    public double TotalScore { get; set; }
    public Dictionary<string, double> Breakdown { get; set; } = new();
    public bool IsHighPerformance => ModelName.Contains("reasoner");
}
