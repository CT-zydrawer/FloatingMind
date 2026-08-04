using FloatingMind.Models.Config;
using FloatingMind.Models.Workflow;
using FloatingMind.Services.LLM;

namespace FloatingMind.Services;

/// <summary>
/// 3.1 Intent Router —— 理解用户目标, 判断任务类型, 区分明确任务与信息不足的输入
/// 
/// 双路径分析:
///   - 快速路径: 关键词+规则匹配 (毫秒级, 零成本)
///   - LLM路径: DeepSeek深度理解 (高准确率, 有成本)
///   默认快速路径, 高明确度时自动升级到LLM路径
/// </summary>
public class IntentRouter
{
    private readonly DeepSeekService? _llm;
    private readonly PromptBuilder? _promptBuilder;
    // === 动作动词池: 检测到这些词说明用户很可能在请求执行操作 ===
    private static readonly HashSet<string> ActionVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "帮我", "请", "执行", "运行", "启动", "停止", "重启",
        "列出", "查找", "搜索", "查询", "找", "定位",
        "写", "生成", "创建", "新建", "添加", "插入",
        "修改", "更改", "更新", "编辑", "调整", "替换", "覆盖",
        "删除", "移除", "清理", "清空", "卸载",
        "整理", "分类", "排序", "合并", "拆分", "转换",
        "分析", "检查", "审查", "审计", "扫描", "检测",
        "修复", "解决", "处理", "调试", "排错",
        "实现", "完成", "搞定", "重构", "优化", "改进", "完善",
        "编译", "构建", "打包", "部署", "发布", "安装", "配置",
        "设置", "设定", "定义", "声明", "指定", "设定",
        "读取", "加载", "打开", "关闭", "保存", "导出", "导入",
        "复制", "粘贴", "移动", "重命名", "备份", "恢复",
        "对比", "比较", "统计", "计算", "汇总", "验证", "确认",
        "测试", "试用", "演练", "演示", "展示", "显示",
        "说明", "解释", "介绍", "描述", "总结", "归纳",
        "建议", "推荐", "给出", "提供", "准备", "制作",
    };

    // === 非任务信号: 检测到这些模式说明输入偏讨论/质疑, 明确度下降 ===
    private static readonly string[] NonTaskPatterns = new[]
    {
        "但是", "可是", "然而", "不过", "问题是", "问题是",
        "为什么", "怎么会", "难道", "是不是", "能不能", "可不可以",
        "你是", "你能否", "你会不会", "你能不能", "你可不可以",
        "我觉得", "我认为", "我想", "我猜", "我怀疑",
        "如果", "假设", "假如", "要是",
        "据说", "听说", "有人说",
        "对吗", "是吧", "对吧", "是不是", "不是吗",
    };

    // === 强任务信号: 即使包含非任务词, 有这些开头也判定为任务 ===
    private static readonly string[] StrongTaskPrefixes = new[]
    {
        "帮我", "请帮我", "给我", "给我把", "帮我把",
        "执行一下", "运行一下", "做", "做一下", "弄一下",
    };

    // === 领域关键词映射 ===
    private static readonly Dictionary<string, (string intent, int defaultComplexity, string? workflow)> KeywordMap = new()
    {
        // 写作类
        ["论文"] = ("Writing", 6, "EssayCreation"),
        ["文章"] = ("Writing", 4, "EssayCreation"),
        ["报告"] = ("Writing", 5, "EssayCreation"),

        // 开发类
        ["代码"] = ("Development", 7, "CodeDevelopment"),
        ["开发"] = ("Development", 6, "CodeDevelopment"),
        ["功能"] = ("Development", 5, "CodeDevelopment"),
        ["bug"] = ("Development", 4, "CodeDevelopment"),
        ["修复"] = ("Development", 5, "CodeDevelopment"),
        ["实现"] = ("Development", 6, "CodeDevelopment"),
        ["重构"] = ("Development", 7, "FileRefactor"),

        // 分析类 (检查/审查/审计 → 动态Analysis流程: 发现→分析→审查报告)
        ["分析"] = ("Analysis", 5, null),
        ["检查"] = ("Analysis", 4, null),
        ["审查"] = ("Analysis", 4, null),
        ["审计"] = ("Analysis", 4, null),
        ["review"] = ("Analysis", 4, null),

        // 搜索类
        ["搜索"] = ("Search", 2, "QuickQuery"),
        ["查找"] = ("Search", 2, "QuickQuery"),
        ["查询"] = ("Search", 2, "QuickQuery"),

        // 文件操作
        ["整理"] = ("FileOps", 3, "FileRefactor"),
    };

    public IntentRouter(DeepSeekService? llm = null, PromptBuilder? promptBuilder = null)
    {
        _llm = llm;
        _promptBuilder = promptBuilder;
    }

    public async Task<IntentResult> AnalyzeAsync(string userInput)
    {
        var input = userInput.Trim();
        if (string.IsNullOrWhiteSpace(input))
            return CreateGeneralResult("输入为空", clarity: 1);

        // === Step 1: 判定请求明确度 ===
        var clarity = CalculateClarity(input);

        // === Step 2: 明确度 ≥ 5 且 LLM 可用 → 让 DeepSeek 理解意图 ===
        if (clarity >= 5 && _llm != null && _promptBuilder != null && _llm.IsConfigured)
        {
            try
            {
                var llmResult = await AnalyzeWithLLM(input);
                if (llmResult != null) return llmResult;
            }
            catch { /* LLM失败静默回退到规则引擎 */ }
        }

        // === Step 3: 明确度低于阈值 → 判定为信息不足, Supervisor将向用户提问澄清 ===
        if (clarity < 5)
        {
            return CreateGeneralResult(
                $"意图: General (明确度 {clarity}/10, 输入信息不足, 无法确定为明确任务请求)",
                clarity: clarity);
        }

        // === Step 3: 明确度足够 → 进一步判定意图类型 ===
        var (intent, complexity, workflow) = DetectIntent(input);

        // 根据输入长度和关键词微调复杂度
        if (input.Length > 100) complexity = Math.Min(10, complexity + 2);
        if (input.Contains("复杂") || input.Contains("系统")) complexity = Math.Min(10, complexity + 3);
        if (input.Contains("简单")) complexity = Math.Max(1, complexity - 2);

        return new IntentResult
        {
            Intent = intent,
            Complexity = complexity,
            Clarity = clarity,
            NeedsWorkflow = workflow != null || complexity >= 4,
            WorkflowName = workflow ?? (intent == "Development" && complexity >= 4 ? "CodeDevelopment" : string.Empty),
            Summary = $"意图: {intent}, 复杂度: {complexity}/10, 明确度: {clarity}/10"
        };
    }

    public IntentResult Analyze(string userInput) => AnalyzeAsync(userInput).GetAwaiter().GetResult();

    /// <summary>
    /// LLM深度意图分析: 用 DeepSeek 理解用户意图并返回结构化结果
    /// </summary>
    private async Task<IntentResult?> AnalyzeWithLLM(string userInput)
    {
        if (_llm == null || _promptBuilder == null) return null;
        var (systemPrompt, userMessage) = _promptBuilder.BuildIntent(userInput);
        var response = await _llm.ChatAsync(userMessage, systemPrompt, temperature: 0.2);
        if (string.IsNullOrWhiteSpace(response)) return null;
        try
        {
            var json = response.Trim();
            var start = json.IndexOf('{'); var end = json.LastIndexOf('}');
            if (start >= 0 && end > start) json = json[start..(end + 1)];
            var obj = Newtonsoft.Json.Linq.JObject.Parse(json);
            return new IntentResult
            {
                Intent = obj["intent"]?.ToString() ?? "General",
                Complexity = Math.Clamp((int?)obj["complexity"] ?? 3, 1, 10),
                Clarity = 8,
                NeedsWorkflow = ((bool?)obj["needsWorkflow"] ?? false) || ((int?)obj["complexity"] ?? 3) >= 4,
                WorkflowName = obj["workflowName"]?.ToString() ?? "",
                Summary = obj["summary"]?.ToString() ?? "LLM分析"
            };
        }
        catch { return null; }
    }

    /// <summary>
    /// 计算请求明确度 (1-10):
    /// - 包含强任务前缀 → +5
    /// - 包含动作动词 → 每个+1 (最多+4)
    /// - 包含非任务信号 → 每个-2 (最多-4)
    /// - 纯陈述/疑问句式(无动作词) → 基础分1-2
    /// </summary>
    private int CalculateClarity(string input)
    {
        int score = 3; // 基础分

        // 强任务前缀 → 明确意图
        foreach (var prefix in StrongTaskPrefixes)
        {
            if (input.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                score += 5;
                break;
            }
        }

        // 动作动词 → 请求操作
        int actionCount = 0;
        foreach (var verb in ActionVerbs)
        {
            if (input.Contains(verb, StringComparison.OrdinalIgnoreCase))
                actionCount++;
        }
        score += Math.Min(actionCount, 4); // 最多+4

        // 非任务信号 → 降低明确度
        int nonTaskCount = 0;
        foreach (var pattern in NonTaskPatterns)
        {
            if (input.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                nonTaskCount++;
        }
        score -= Math.Min(nonTaskCount * 2, 4); // 最多-4

        // 如果没有任何动作动词且以问号结尾(纯疑问) → 大幅降低
        if (actionCount == 0 && input.EndsWith("?"))
            score -= 2;

        // 如果没有任何动作动词且是陈述句(以句号/无标点结尾) → 大幅降低
        if (actionCount == 0 && (input.EndsWith("。") || (!input.EndsWith("?") && !input.EndsWith("!"))))
            score -= 1;

        return Math.Clamp(score, 1, 10);
    }

    /// <summary>
    /// 基于关键词匹配检测意图类型
    /// </summary>
    private (string intent, int complexity, string? workflow) DetectIntent(string input)
    {
        var lowered = input.ToLower();
        int maxMatch = 0;
        string bestIntent = "General";
        int complexity = 1;
        string? workflow = null;

        foreach (var kv in KeywordMap)
        {
            if (lowered.Contains(kv.Key.ToLower()) && kv.Key.Length > maxMatch)
            {
                maxMatch = kv.Key.Length;
                bestIntent = kv.Value.intent;
                complexity = kv.Value.defaultComplexity;
                workflow = kv.Value.workflow;
            }
        }

        // 没有任何领域关键词匹配, 但明确度足够 → 判定为 General(但仍允许简单执行)
        if (maxMatch == 0 && complexity <= 1)
            complexity = 2;

        return (bestIntent, complexity, workflow);
    }

    private static IntentResult CreateGeneralResult(string summary, int clarity)
    {
        return new IntentResult
        {
            Intent = "General",
            Complexity = 1,
            Clarity = clarity,
            NeedsWorkflow = false,
            WorkflowName = string.Empty,
            Summary = summary
        };
    }
}
