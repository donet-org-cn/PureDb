# SQL 语句支持清单

适用版本：`1.0.0-preview.2` NuGet 包与源码（2026-10-24）。本文以解析器、执行器和自动测试为依据；✅ 表示已实现，⚠️ 表示有限制，❌ 表示尚未实现。PureDb 是具有明确支持范围的 SQL 方言，不能视为完整 SQLite 替代品。

## 一眼查看

| 语句或能力 | PureDb 原版文件 | 关键边界 |
| --- | --- | --- |
| CREATE TABLE / DROP TABLE | ✅ | 每表必须有一个 INTEGER PRIMARY KEY |
| NOT NULL / UNIQUE / DEFAULT / CHECK | ⚠️ | 复合 UNIQUE、常量 DEFAULT、列级及表级 CHECK |
| CREATE INDEX / CREATE UNIQUE INDEX / DROP INDEX | ✅ | 单列、复合列、受支持的 JSON 路径表达式；支持 ASC / DESC |
| FOREIGN KEY / REFERENCES | ⚠️ | 主键或完整唯一键、五种引用动作、立即及延迟检查 |
| CREATE VIEW / DROP VIEW | ✅ | 普通视图；写入需要 INSTEAD OF 触发器 |
| CREATE TRIGGER / DROP TRIGGER | ⚠️ | BEFORE / AFTER / INSTEAD OF 行触发器；RAISE 仅 ABORT |
| INSERT VALUES / INSERT SELECT / DEFAULT VALUES | ✅ | 支持一次插入多行、指定列和自动分配整数主键 |
| INSERT ON CONFLICT | ⚠️ | 单分支 DO NOTHING / DO UPDATE；更新需显式主键或唯一列目标 |
| UPDATE / DELETE | ✅ | 支持表达式、WHERE、子查询和 WITH 前缀 |
| SELECT / DISTINCT / GROUP BY / HAVING | ✅ | 支持的标量表达式和五种聚合函数 |
| INNER / LEFT / RIGHT / FULL / CROSS JOIN | ✅ | 普通表支持；相关 JSON 表函数支持 CROSS / INNER / LEFT |
| ORDER BY / LIMIT / OFFSET | ✅ | 多列、升降序、输出别名和序号；负 LIMIT 表示不限制 |
| UNION / UNION ALL / INTERSECT / EXCEPT | ✅ | 分支列数相同；最终排序和分页作用于整个集合 |
| WITH / WITH RECURSIVE | ⚠️ | 普通 CTE 和受控递归 CTE，可用于 SELECT / INSERT / UPDATE / DELETE |
| 标量 / IN / EXISTS / FROM 子查询 | ✅ | 支持相关表达式子查询；普通 FROM 子查询不支持 LATERAL |
| 窗口函数 / WINDOW / OVER | ⚠️ | 排名、偏移、取值、聚合；ROWS / RANGE / GROUPS 和 EXCLUDE |
| CASE WHEN / CASE value WHEN | ✅ | 分支惰性求值，支持聚合结果中的 CASE |
| JSON / JSONB / JSON5 | ⚠️ | JSON5 输入及文本输出；JSONB 为 PureDb 自有 PJB1 格式 |
| json_each / json_tree | ⚠️ | 八个可见列、两个隐藏列；节点 ID 与 SQLite 不相同 |
| BEGIN / COMMIT / END / ROLLBACK | ⚠️ | 乐观 Serializable；Shared 模式只接受 BEGIN DEFERRED |
| SAVEPOINT / ROLLBACK TO / RELEASE | ✅ | 支持嵌套、同名保存点、DDL 回滚及 ADO.NET 保存点 |
| C# 用户函数 | ⚠️ | 连接级标量函数；不支持自定义聚合、窗口及表值回调 |
| ALTER TABLE / RETURNING / INSERT OR ... | ❌ | 尚未实现，不能依赖这些语法 |
| PRAGMA / ATTACH / VACUUM / FTS / RTree | ❌ | SQL 入口尚未实现；维护操作使用对应 C# API |

**文件格式范围：**上表主要针对 `new SqlDatabase(...)` 使用的 PureDb 原版文件。可选的 `PureDb.SqliteCompatibility` 使用相同查询执行器，可在其已有表上执行受支持的 CTE、子查询、集合、窗口和 JSON 查询；但本轮新增的复合索引、外键、默认值、CHECK、视图和触发器尚未接入 SQLite 页格式写入及导入导出，兼容包明确拒绝这些持久化模式，避免丢失定义或生成不正确的文件。该后端仍不支持 SQLite WAL 和任意模式；详见 [SQLite 文件兼容](SQLITE-COMPATIBILITY.md)。

## 建表、约束与索引

```sql
CREATE TABLE IF NOT EXISTS departments(
    id INTEGER PRIMARY KEY,
    code TEXT NOT NULL UNIQUE
);
CREATE TABLE employees(
    id INTEGER PRIMARY KEY,
    department_id INTEGER REFERENCES departments(id)
        ON UPDATE CASCADE ON DELETE SET NULL,
    name TEXT NOT NULL,
    level INTEGER DEFAULT 0,
    profile JSON,
    UNIQUE(department_id,name)
);
CREATE INDEX employees_lookup ON employees(department_id,level DESC,name);
CREATE INDEX employees_age ON employees(json_extract(profile,'$.age'));
DROP INDEX IF EXISTS employees_age;
```

- 类型为 `INTEGER`、`REAL`、`TEXT`、`BLOB`、`JSON`、`JSONB`。使用严格类型校验；没有实现 SQLite 全部类型亲和性、任意类型名、STRICT 表语法。
- 主键可在列内或表级声明，但必须且只能是单列 INTEGER，值范围 `[0, 2^40)`。省略或传入 NULL 时，从当前最大值加一分配，空表从 1 开始；不提供 AUTOINCREMENT 的永不复用保证。复合主键、隐式 rowid 表、WITHOUT ROWID 均不支持。
- 支持列级及表级 UNIQUE，复合唯一键中任一分量为 NULL 时不产生唯一冲突。支持带名称的 `CONSTRAINT` 语法，名称不作为独立可操作对象。
- DEFAULT 支持数字、文本、NULL 及受支持的常量表达式；不支持 BLOB 字面量默认值、业务函数、列引用或子查询默认值。没有 DEFAULT 的省略列为 NULL，再执行非空校验。
- CHECK 支持列内、表级及带名称的约束，插入与更新统一执行；数值结果为 0 时失败，非零或 NULL 通过。沿用严格谓词类型，文本或 BLOB 结果报错，不执行 SQLite 的宽松数值转换。表达式不支持参数、子查询、聚合或窗口。可调用已注册的 C# 标量函数；函数不持久化，重开后须重新注册，替换注册后后续语句重新绑定。语句、显式事务和保存点均保留约束与原子撤销。
- 索引项支持列和受支持的 `JSON_EXTRACT` 路径表达式，可组合 ASC / DESC。支持左前缀等值、下一项范围、匹配排序和反向扫描；不支持任意函数索引、部分索引 WHERE、COLLATE 或 INCLUDE。
- 复合索引**定义持久化**，排序键与行引用按数据版本在内存中惰性构建、二分定位并复用。第一次访问可能扫描并排序整表；增删改后需要重建。它还不是分页持久化的增量复合 B-tree，不应把热缓存查询表现外推到首次查询或超内存场景。
- 支持 `IF NOT EXISTS` / `IF EXISTS` 的对应建删语句。不支持 `CREATE TABLE AS SELECT`、TEMP 表、生成列、CHECK、ALTER TABLE。

外键支持列级 REFERENCES 和表级多列 FOREIGN KEY，引用父表 INTEGER PRIMARY KEY 或完整 UNIQUE 键。采用 MATCH SIMPLE：子键含 NULL 即不要求父键存在。支持 `NO ACTION`、`RESTRICT`、`CASCADE`、`SET NULL`、`SET DEFAULT`，同时适用于 ON DELETE 和 ON UPDATE。SET DEFAULT 的结果仍须满足外键。

```sql
CREATE TABLE details(
    id INTEGER PRIMARY KEY,
    department_id INTEGER,
    FOREIGN KEY(department_id) REFERENCES departments(id)
        DEFERRABLE INITIALLY DEFERRED
);
BEGIN;
INSERT INTO details VALUES(1,100);
INSERT INTO departments VALUES(100,'new');
COMMIT;
```

外键默认且始终启用；不提供 `PRAGMA foreign_keys` 或 `PRAGMA defer_foreign_keys` 开关。默认立即约束在语句结束校验，`DEFERRABLE INITIALLY DEFERRED` 在提交校验；RESTRICT 立即拒绝受引用的父键变化。级联深度上限 64，超过报错并回滚语句。父表尚不存在时允许保存声明，实际写入时检查父模式和唯一键；不支持事后修改约束或 SET CONSTRAINTS。

## 增删改、查询及表达式

```sql
INSERT INTO employees(id,department_id,name) VALUES(1,100,'张三'),(2,100,'李四');
INSERT INTO employees(department_id,name) SELECT id,'负责人' FROM departments;
UPDATE employees SET level=level+1 WHERE department_id IN (SELECT id FROM departments);
DELETE FROM employees WHERE NOT EXISTS(
    SELECT 1 FROM departments d WHERE d.id=employees.department_id
);
SELECT department_id,COUNT(*) AS total,AVG(level) AS average_level
FROM employees GROUP BY department_id HAVING COUNT(*)>0
ORDER BY total DESC,department_id LIMIT 20 OFFSET 0;
```

支持投影列、表达式、`*`、`alias.*`、别名、参数、无 FROM 常量查询、多表及自连接、ON 条件、DISTINCT、GROUP BY、HAVING。普通连接包括 INNER、LEFT、RIGHT、FULL OUTER、CROSS 及逗号连接；暂不支持 NATURAL、USING 和普通派生表 LATERAL。

支持 `+ - * / %`、比较、`AND OR NOT`、`BETWEEN`、`IN` / `NOT IN`、`IS NULL` / `IS NOT NULL`、LIKE、`||`、`CASE`。LIKE 的 ESCAPE 子句、GLOB、REGEXP、位运算、CAST、通用 IS 比较、行值元组比较未实现。NULL 使用 SQL 三值逻辑；整数算术溢出报错，表中 REAL 必须为有限 double。文本排序使用 .NET ordinal，LIKE 和大小写转换使用 invariant Unicode，与 SQLite 的部分规则不同。

支持 `COUNT(*)`、`COUNT(expr)`、`SUM`、`AVG`、单参数 `MIN` / `MAX`；标量函数包括 `LENGTH`、`LOWER`、`UPPER`、`COALESCE`。未实现聚合 DISTINCT、FILTER、GROUP_CONCAT、TOTAL、STRING_AGG、日期时间、数学及完整 SQLite 内置函数集合。

LIMIT 支持 `LIMIT count OFFSET skip` 和 `LIMIT skip,count`；负 count 表示不限制，负 skip 按 0 处理。ORDER BY 支持表达式、输出别名、从 1 开始的结果序号、多列 ASC / DESC。未提供 COLLATE、NULLS FIRST / LAST。相同排序键未指定唯一补充键时，不保证与 SQLite 返回相同的并列行顺序。

DML 还不支持 `INSERT OR ...`、`REPLACE`、`ON CONFLICT DO ...`、`RETURNING`、`UPDATE FROM`、UPDATE / DELETE 的 ORDER BY / LIMIT。

## CTE、子查询和集合运算

```sql
WITH active AS (SELECT id,name FROM employees WHERE level>0)
SELECT name FROM active
UNION ALL
SELECT code FROM departments
ORDER BY name;

WITH RECURSIVE seq(n) AS (
    SELECT 1 UNION ALL SELECT n+1 FROM seq WHERE n<10
)
SELECT n FROM seq ORDER BY n;

SELECT e.id,(SELECT d.code FROM departments d WHERE d.id=e.department_id)
FROM employees e
WHERE EXISTS(SELECT 1 FROM departments d WHERE d.id=e.department_id);
```

- 普通 CTE 支持多个定义、列名列表、引用前面已定义的 CTE、WITH 前缀的 SELECT / INSERT / UPDATE / DELETE。接受 MATERIALIZED / NOT MATERIALIZED 提示，但当前均按语句物化；易变用户函数的调用次数可能与 SQLite 不同。未使用的 CTE 也可能求值。前向引用和互递归未实现。
- 递归 CTE 使用工作队列，支持 UNION / UNION ALL、多锚点、多递归分支、递归 ORDER BY、LIMIT / OFFSET；RECURSIVE 关键字可以省略。所有递归分支必须使用一致的 UNION 类型，每个递归分支只能有一次直接递归表引用，不能含聚合、GROUP BY 或窗口函数。复杂嵌套自引用不支持。
- 查询嵌套深度上限 64；递归 CTE 最多生成 1,000,000 行，超过报错。简单直接读取的外层 LIMIT 可安全下推；存在排序、过滤、连接等场景时不能靠外层 LIMIT 保证终止，应在递归定义中设置终止条件或 LIMIT。
- 标量子查询必须单列；无行返回 NULL，多行取第一行。IN 子查询遵循 NULL / 空集语义，EXISTS 检查是否有结果；支持外层列关联。相关子查询目前按外层行执行，尚未做一般去相关、半连接重写或结果缓存。
- FROM 派生表须有别名，当前物化后查询；普通 FROM 派生表不支持相关 LATERAL。大型中间结果会占用额外内存。
- UNION 去重，UNION ALL 保留重复，INTERSECT 求交，EXCEPT 求差；同层从左至右结合，NULL 在集合去重中视为相等。分支列数必须相同，列名来自最左侧查询，混合结果类型的 ADO.NET 元数据为 Object。
- 集合结果最终的 ORDER BY 必须引用输出列名、序号或匹配的输出表达式；不支持括号直接改变集合结合顺序，可通过 FROM 派生查询分层。独立 VALUES 查询和 INTERSECT ALL / EXCEPT ALL 未实现。

## UPSERT 冲突写入

```sql
CREATE TABLE stock(id INTEGER PRIMARY KEY,code TEXT UNIQUE,qty INTEGER CHECK(qty>=0));
INSERT INTO stock VALUES(1,'A',10)
ON CONFLICT(code) DO UPDATE SET qty=stock.qty+excluded.qty
WHERE excluded.qty>0;
INSERT INTO stock VALUES(2,'A',5) ON CONFLICT DO NOTHING;
```

支持 VALUES 多行、INSERT SELECT / WITH 输入上的单个 ON CONFLICT 分支。DO UPDATE 必须给出显式列目标，匹配整数主键、列 UNIQUE 或完整复合唯一索引；不能使用普通非唯一索引、表达式或部分索引目标。DO NOTHING 可省略目标，捕获任一唯一冲突；NULL 唯一键不产生冲突。`excluded.column` 为待插入值，无前缀列为旧行值；WHERE 不成立时不更新。未冲突插入或实际更新影响数为 1，跳过为 0。

复用会话内的唯一键映射；冲突更新进入标准 UPDATE 流程，执行 CHECK、外键和更新触发器，保留 BEFORE INSERT 行为。NOT NULL、CHECK、外键或触发器异常不能被 DO NOTHING 吞掉；更新产生其他唯一冲突也报错并撤销语句。兼容包可在其已有受支持表上执行 UPSERT。尚不支持多分支、无目标 DO UPDATE、INSERT OR / REPLACE、RETURNING 和视图 UPSERT。

## 窗口函数

```sql
SELECT id,department_id,level,
       ROW_NUMBER() OVER (PARTITION BY department_id ORDER BY level DESC,id) AS position,
       SUM(level) OVER (PARTITION BY department_id ORDER BY id
           ROWS BETWEEN 2 PRECEDING AND CURRENT ROW) AS recent_total
FROM employees ORDER BY id;

SELECT department_id,COUNT(*) AS total,SUM(COUNT(*)) OVER () AS all_total
FROM employees GROUP BY department_id ORDER BY department_id;
```

| 类别 | 已实现函数 |
| --- | --- |
| 排名与分布 | ROW_NUMBER、RANK、DENSE_RANK、PERCENT_RANK、CUME_DIST、NTILE |
| 偏移与取值 | LAG、LEAD、FIRST_VALUE、LAST_VALUE、NTH_VALUE |
| 聚合窗口 | COUNT、SUM、AVG、MIN、MAX |

支持 `PARTITION BY`、多项 ORDER BY、命名 WINDOW 和基本窗口继承；支持 ROWS / RANGE / GROUPS 的当前行、无界边界、PRECEDING / FOLLOWING、BETWEEN，以及 EXCLUDE NO OTHERS / CURRENT ROW / GROUP / TIES。省略帧时，带排序默认累计到当前同值组，无排序默认整个分区。带数值偏移的 RANGE 必须恰好一个排序表达式；ROWS / GROUPS 偏移必须为非负整数。

窗口可出现在结果表达式和最终 ORDER BY；GROUP BY / HAVING 先计算，窗口后计算。分组窗口中的相关子查询尚不支持，应先写为 FROM 派生查询。窗口不能用于 WHERE、JOIN ON、GROUP BY 或 HAVING；不能嵌套窗口。FILTER、窗口 DISTINCT、自定义窗口函数以及完整 SQLite 窗口继承限制尚未覆盖。

性能实现：分区排序后识别同值组，SUM / COUNT / AVG 使用前缀统计，MIN / MAX 使用区间树，避免为每行重新扫描整个窗口；偏移 RANGE 使用有序边界。执行器仍会物化分区，多个窗口也会创建各自的辅助结构，未实现磁盘溢出或所有同窗函数共享排序。

## 视图与触发器

```sql
CREATE VIEW senior_employees(employee_id,employee_name)
AS SELECT id,name FROM employees WHERE level>=3;
CREATE TABLE audit(id INTEGER PRIMARY KEY,employee_id INTEGER,old_level INTEGER,new_level INTEGER);
CREATE TRIGGER employee_level_changed AFTER UPDATE OF level ON employees
WHEN NEW.level!=OLD.level
BEGIN
    INSERT INTO audit(employee_id,old_level,new_level) VALUES(NEW.id,OLD.level,NEW.level);
END;
CREATE TRIGGER reject_negative_level BEFORE INSERT ON employees
WHEN NEW.level<0
BEGIN
    SELECT RAISE(ABORT,'level must be nonnegative');
END;
```

视图定义、触发器定义与关联模式持久化，并随事务、保存点和重开恢复。视图支持列名列表，定义中不能带参数；视图当前按查询物化，尚未实现展开、谓词下推和物化视图。无 INSTEAD OF 触发器的视图不能写入；视图循环引用会报错。

表触发器支持 BEFORE / AFTER 的 INSERT / UPDATE / DELETE，视图支持 INSTEAD OF；可选 FOR EACH ROW、UPDATE OF、WHEN、OLD / NEW 引用。触发器体支持 INSERT、UPDATE、DELETE、SELECT，每条须分号结尾；触发器动作和主语句一起原子回滚。直接受影响行数不包含触发器、级联写入；视图 INSTEAD OF 写入的直接影响数为 0。

仅支持 `RAISE(ABORT,message)`；FAIL / IGNORE / ROLLBACK 动作、TEMP 触发器、触发器体顶层 WITH、语句级触发器未实现。相同触发器在当前调用链中不会递归重入，不提供 `recursive_triggers` 开关；调用深度上限 64。不要依赖多个触发器的执行次序或 BEFORE 触发器修改本次目标行后的细节，这些行为未提供兼容保证。

## JSON、JSON5 与表值函数

标量支持 `json` / `jsonb`、`json_valid`、`json_extract`、`json_type`、`json_array_length`、`json_set`、`json_insert`、`json_replace`、`json_remove`、`json_object`、`json_array`、`json_quote`、`json_patch`、`json_array_insert`，及执行器支持的 jsonb 对应操作；支持 `->`、`->>`。不包含 json_error_position、json_pretty、JSON 聚合函数、jsonb_each / jsonb_tree。

JSON5 输入支持注释、尾逗号、单引号字符串、未加引号的键、十六进制、正号、首尾小数点及转义续行等；NaN / QNaN / SNaN 转为 JSON null，Inf / Infinity 解析为无穷 JSON 数值。JSON 文本输出是规范化标准 JSON，格式、转义和数字字面量不保证逐字节等于 SQLite；转为表列 REAL 或 PJB1 JSONB 时仍执行有限数值限制。解析深度上限 128；未宣称覆盖所有 SQLite 宽松输入扩展、重复对象键或格式保留行为。

`json_valid(value)` / 标志 1 只检查标准 JSON 文本，标志 2 允许 JSON5；标志可按位组合。标志 4 / 8 对应 **PureDb PJB1** 的头部 / 严格验证，不代表识别 SQLite JSONB。`json_valid(NULL)` 为 SQL NULL。

```sql
SELECT json_extract('{name:''张三'',age:0x20,}', '$.age');
SELECT key,value,type FROM json_each('[1,null,{"name":"张三"}]');
SELECT key,value,type,atom,parent,fullkey,path FROM json_tree('{"items":[1,2]}');
SELECT e.id,j.value FROM employees e
LEFT JOIN json_each(e.profile,'$.tags') AS j ON j.type='text'
ORDER BY e.id,j.key;
```

`json_each(value[,root])` 展开直接子项，`json_tree(value[,root])` 递归遍历。八个可见列为 key、value、type、atom、id、parent、fullkey、path，隐藏列 json、root 可显式查询；`SELECT *` 不包含隐藏列。SQL NULL 产生空表，标量产生一行。ID 在本次遍历内唯一，parent 引用本次 ID；**这些 ID 不与 SQLite 的内部编号相等，也不能作为持久业务主键**。

支持字段、引号字段、数组正向和倒数下标，编辑支持末尾追加；不支持通配 JSONPath。JSON 表函数可作为独立数据源，或通过逗号 / CROSS / INNER / LEFT 与左侧行相关调用；不支持与相关 JSON 表函数做 RIGHT / FULL 连接。对象或数组的 value 返回 JSON 文本。

## 事务与保存点

```sql
BEGIN DEFERRED TRANSACTION;
INSERT INTO employees(id,name) VALUES(10,'待确认');
SAVEPOINT step_one;
UPDATE employees SET level=8 WHERE id=10;
ROLLBACK TRANSACTION TO SAVEPOINT step_one;
RELEASE SAVEPOINT step_one;
COMMIT TRANSACTION;
```

支持 BEGIN [DEFERRED / IMMEDIATE / EXCLUSIVE] [TRANSACTION]、COMMIT / END [TRANSACTION]、ROLLBACK [TRANSACTION]、SAVEPOINT name、ROLLBACK [TRANSACTION] TO [SAVEPOINT] name、RELEASE [SAVEPOINT] name。

- 默认 DEFERRED，为跨调用的乐观 Serializable 事务；提交前验证全局版本。Shared 模式不接受 IMMEDIATE / EXCLUSIVE；只有已按 Exclusive 方式独占数据库的连接可使用这两个模式。没有实现 SQLite 的 RESERVED / PENDING / WAL 锁转换，不能将语法支持等同于完整 SQLite 事务协议。
- 事务内可读自己的暂存修改，其他连接不能读到。普通约束或触发器失败回滚该条语句，事务仍可修正后继续；延迟外键导致的 COMMIT 失败也可修正重试。版本冲突抛出 DBConcurrencyException，必须整体回滚并重做。
- BEGIN 不允许嵌套，SAVEPOINT 可以嵌套、同名；名称按不区分大小写的最近一次匹配处理。ROLLBACK TO 保留目标保存点并删除更内层保存点，RELEASE 删除目标及更内层保存点。释放最外层、由 SAVEPOINT 开启的事务会真正提交，含延迟约束校验。
- DDL、索引定义、视图和触发器也参加保存点回滚。关闭连接或释放数据库对象回滚未提交事务。
- 没有显式事务控制语句时，单次 `ExecuteAsync` / `ExecuteTransactionAsync` 中的普通 DDL / DML 整批原子提交；含事务控制语句时按显式 SQL 边界执行，成功提交的前段不会因后段错误撤销。此 API 批处理约定与 sqlite3_exec 的默认逐句提交不同。
- SQL 显式事务和 ADO.NET DbTransaction 使用同一会话模型。ADO.NET 支持 SupportsSavepoints、Save / SaveAsync、Rollback(name) / RollbackAsync(name)、Release / ReleaseAsync。通过 BeginTransaction 创建的事务应通过对象 Commit / Rollback 结束；该事务内部的 SQL BEGIN / COMMIT / 完整 ROLLBACK 明确拒绝，SQL SAVEPOINT / ROLLBACK TO / RELEASE 可用。连接和显式事务不可由调用方并发操作。
- I/O 失败的提交结果仍可能不确定；语句回滚能力不能替代存储故障恢复或业务幂等处理。事务暂存、保存点撤销及中间结果占用内存，没有通用磁盘溢出机制。

## C# 函数注入

```csharp
db.CreateFunction<string?, bool>("RELIGIBLE", user => user is "Alice" or "张三");
var result = db.Query("SELECT * FROM Awards WHERE RELIGIBLE(User)");
```

支持连接级、按名称和参数数量解析的标量函数，固定数量重载、可变参数入口、替换及移除；SqlDatabase 和 ADO.NET 连接均可注册。函数仅保存在连接内，不写入数据库文件，重开后需重新注册。SQL 列值按标量传入；业务对象需由函数从 JSON 等值自行转换。不支持自定义聚合、窗口、表值或跨进程共享 C# 委托。NULL、类型、BLOB 所有权、异常和易变函数限制详见 [用户函数](USER-FUNCTIONS.md)。

## 尚未实现的主要 SQLite 能力

| 分类 | 不支持的语句或能力 |
| --- | --- |
| 表结构 | ALTER TABLE、生成列、TEMP、WITHOUT ROWID、AUTOINCREMENT、复合主键、CREATE TABLE AS SELECT |
| 写入扩展 | 多分支 UPSERT、无目标 DO UPDATE、INSERT OR ...、REPLACE、RETURNING、UPDATE FROM、UPDATE / DELETE ORDER BY LIMIT |
| 表达式及查询 | CAST、COLLATE、自定义排序规则、USING / NATURAL JOIN、普通 LATERAL、独立 VALUES、聚合 DISTINCT / FILTER |
| 内置函数 | SQLite 完整的数学、字符串、日期、JSON 聚合及格式诊断函数集合 |
| 模式及维护 SQL | PRAGMA、ATTACH / DETACH、VACUUM、REINDEX、ANALYZE、EXPLAIN SQL、sqlite_schema 系统表查询 |
| 扩展机制 | 虚拟表 API、FTS、RTree、加载原生扩展、自定义聚合 / 窗口 / 表值回调 |
| 完整协议 | SQLite WAL、任意 SQLite 页和编码、全部事务锁语义、全部 SQL 类型亲和性和错误码 |

本清单不是 SQLite 全部语法的逐项认证。清单之外的语法应先作为不支持处理，不依赖未测试的偶然接受行为。新增持久化模式由新版读取；旧预览版不了解外键和触发器等语义，不支持降级打开并写入使用了这些特性的文件。

## 验证与代码入口

新增测试 [PureDb.SqlCompatibilityTests](../tests/PureDb.SqlCompatibilityTests/Program.cs) 与原生 SQLite 3.54.1 逐行逐值比较，覆盖 CHECK、UPSERT、集合、普通和递归 CTE、相关子查询、JSON5 和 JSON 表函数、窗口帧、随机复合索引、外键级联及保存点；另检查约束失败、重开恢复、ADO.NET 保存点、Shared 冲突和拒绝不完整 SQLite 导出。它已接入 [自动测试脚本](../scripts/test.py)，与原有存储、SQL、ADO.NET、兼容和排序测试共同执行。平台和实际数量见 [开发验证](DEVELOPMENT-VALIDATION.md)。

```bash
dotnet build PureDb.slnx -c Release
python scripts/prepare-sqlite.py
python scripts/test.py
dotnet run --project tools/PureDb.SourceCheck -c Release --no-build
```

主要实现位于 [解析器](../src/PureDb/Sql/SqlParser.Query.cs)、[扩展查询](../src/PureDb/Sql/SqlQuery.Extended.cs)、[窗口执行](../src/PureDb/Sql/SqlQuery.Windows.cs)、[复合索引](../src/PureDb/Sql/SqlCompositeIndex.cs)、[外键](../src/PureDb/Sql/SqlSession.ForeignKeys.cs)、[触发器与视图](../src/PureDb/Sql/SqlSession.Schema.cs)、[事务控制](../src/PureDb/Sql/SqlTransactions.cs)、[JSON5](../src/PureDb/Sql/SqlJson5.cs)。所有扩展保留纯 .NET 实现，原生 SQLite 仅用于验证。

语义对照参考 SQLite 官方 [WITH](https://www.sqlite.org/lang_with.html)、[窗口函数](https://www.sqlite.org/windowfunctions.html)、[JSON](https://www.sqlite.org/json1.html) 和 [保存点](https://www.sqlite.org/lang_savepoint.html)、[UPSERT](https://www.sqlite.org/lang_upsert.html) 和 [CHECK](https://www.sqlite.org/lang_createtable.html#check_constraints)；这些链接描述 SQLite，PureDb 的实际支持范围以本文为准。
