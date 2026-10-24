# 中文注释与代码格式

主库及可选兼容库的 C# 源码使用 UTF-8。类型、方法、构造函数、属性、索引器和公开字段使用中文 XML 文档；参数、类型参数和非 void 返回值分别写明 `<param>`、`<typeparam>`、`<returns>`。位置记录的构造参数也需要说明，以便关联生成属性。

注释应解释行为、单位、默认值、NULL、所有权和失败边界。涉及文件写入顺序、锁、事务恢复、缓存版本及 SIMD 的代码，应说明算法成立的前提。复用 .NET 运行时代码的版权和许可证声明保留原文。

公开接口的文档随 NuGet 包发布为 `PureDb.xml` 和 `PureDb.SqliteCompatibility.xml`，IDE 可显示中文提示。文档文件不参与数据库的运行时查询。

```powershell
dotnet build PureDb.slnx -c Release
dotnet run --project tools/PureDb.SourceCheck -c Release --no-build
dotnet format whitespace src/PureDb/PureDb.csproj --no-restore
dotnet format whitespace src/PureDb.SqliteCompatibility/PureDb.SqliteCompatibility.csproj --no-restore
```

`PureDb.SourceCheck` 使用 SDK 自带的 Roslyn 分析实际源码，检查中文摘要、参数对应关系、类型参数、返回值和 XML 格式。构建同时启用文档生成，公开成员漏写文档会导致编译失败。检查工具属于开发工具，不进入数据库包，不增加数据库的运行时依赖。

格式以根目录 [`.editorconfig`](../.editorconfig) 为准：4 空格缩进、统一运算符与逗号间距、展开单行代码块。CI 与预览发布流水线验证源文件格式；修改后运行上述命令。
