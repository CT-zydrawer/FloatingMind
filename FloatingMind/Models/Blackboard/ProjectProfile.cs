using Newtonsoft.Json;

namespace FloatingMind.Models.Blackboard;

/// <summary>
/// 项目结构化画像 —— Analyzer(CodeAgent analyze)规则提取生成, 以 JsonPayload 提交Blackboard, 供其他Agent解读。
/// 消费方: CommandAgent(validation 命令)、CodeAgent(生成上下文)、Review(验证依据)。
/// 示例:
/// {
///   "project": "SkyLauncher",
///   "language": "CSharp",
///   "framework": ".NET 8",
///   "projects": ["SkyLauncher.csproj"],
///   "testProjects": [],
///   "validation": [ { "type": "build", "command": "dotnet build SkyLauncher.csproj" } ]
/// }
/// </summary>
public class ProjectProfile
{
    /// <summary>项目名(目录名或主工程名)</summary>
    public string Project { get; set; } = string.Empty;

    /// <summary>主语言: CSharp/Python/JavaScript/TypeScript/Java/Go</summary>
    public string Language { get; set; } = string.Empty;

    /// <summary>框架描述: ".NET 8" / "Python 3.12" 等</summary>
    public string Framework { get; set; } = string.Empty;

    /// <summary>工程文件相对路径(*.csproj/*.sln/*.fsproj/*.vbproj)</summary>
    public List<string> Projects { get; set; } = new();

    /// <summary>测试工程相对路径(名称含 Test/Tests)</summary>
    public List<string> TestProjects { get; set; } = new();

    /// <summary>推荐的验证命令(按语言/工程规则生成)</summary>
    public List<ValidationCommand> Validation { get; set; } = new();

    /// <summary>序列化为camelCase JSON(与设计文档样例一致: project/language/framework/...)</summary>
    public string ToJson() => JsonConvert.SerializeObject(this, Formatting.Indented,
        new JsonSerializerSettings
        {
            ContractResolver = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver()
        });
}

/// <summary>一条验证命令(供 Workflow 的 Verify 节点消费)</summary>
public class ValidationCommand
{
    /// <summary>类型: build / test / compile / lint</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>具体命令, 在任务工作区根目录执行</summary>
    public string Command { get; set; } = string.Empty;
}
