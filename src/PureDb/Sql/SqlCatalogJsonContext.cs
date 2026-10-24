using System.Text.Json;
using System.Text.Json.Serialization;

namespace PureDb;
// 构建时生成模式 JSON 元数据，避免新进程打开数据库时对每种模式类型反射。
// 序列化名称和选项保持原文件格式。
/// <summary>构建时生成模式 JSON 序列化元数据，避免运行时反射及启动扫描。</summary>
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(SqlCatalog))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(double))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(JsonElement))]
internal partial class SqlCatalogJsonContext : JsonSerializerContext;
