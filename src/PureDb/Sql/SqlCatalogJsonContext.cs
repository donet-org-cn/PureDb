using System.Text.Json;
using System.Text.Json.Serialization;

namespace PureDb;

// Generate schema metadata at build time instead of reflecting over every schema
// type in each newly opened process. JSON names/options retain the existing format.
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(SqlCatalog))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(double))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(JsonElement))]
internal partial class SqlCatalogJsonContext : JsonSerializerContext;
