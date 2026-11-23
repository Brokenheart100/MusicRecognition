using System.Text.Json;
using System.Text.Json.Serialization;

namespace MusicRecognition.Application.Serialization;

// 谱面与任务快照的 JSON 读写约定：全项目共用一套序列化设置，保证数据库、接口、脚本格式一致。
// Web 默认命名策略把 C# 的 PascalCase 属性写成 camelCase（如 Width -> width）。
// 枚举用 JsonStringEnumConverter 存成可读字符串（MovableDo 而不是 1），改枚举顺序也不会读错旧数据。
// Options 是只读单例：JsonSerializerOptions 每次新建既慢又容易配置不一致。
public static class ScoreJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    { Converters = { new JsonStringEnumConverter() } };
    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T Read<T>(string value) => JsonSerializer.Deserialize<T>(value, Options)!;
}
