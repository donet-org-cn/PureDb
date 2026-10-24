using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;

[assembly: InternalsVisibleTo("PureDb.CompatibilityTests")]
namespace PureDb.SqliteCompatibility;
/// <summary>纯托管 SQLite 文件存储，负责连接级独占锁、模式适配及回滚日志提交。</summary>
internal sealed class SqliteStorage : ISqlStorage, ISqlSnapshot
{
    private const long Pending = 0x40000000;
    private readonly string _path;
    private readonly SqliteCompatibilityOptions _options;
    private readonly object _sync = new();
    private readonly FileStream _file = null!;
    private static int _linuxOwner;
    private bool _linuxLease;
    private SqliteFileReader _reader = null!;
    private SqlCatalog _catalog = new();
    private byte[] _catalogBytes = [];
    private readonly Dictionary<int, long> _roots = [];
    private readonly Dictionary<string, uint> _indexRoots = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, int> _counts = [];
    private long _version, _lastId = long.MinValue;
    private byte[]? _lastValue;
    private bool _pending, _exclusive, _reserved, _closed, _draining;
    private Exception? _fault;
    internal static Action<string>? CommitStage;
    /// <summary>打开兼容文件，取得独占锁并恢复热日志，然后装载受支持模式。</summary>
    /// <param name="path">当前数据库或索引文件路径。</param>
    /// <param name="options">后端配置；为 null 时使用默认值。</param>
    internal SqliteStorage(string path, SqliteCompatibilityOptions options)
    {
        if (!OperatingSystem.IsWindows() && !(OperatingSystem.IsLinux() && options.LinuxExclusiveProcess))
            throw new PlatformNotSupportedException("On Linux, explicitly enable LinuxExclusiveProcess and use a dedicated managed process without other handles to this database. Other platforms are not supported.");
        _path = System.IO.Path.GetFullPath(path);
        _options = options;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
        try
        {
            // fcntl 锁属于进程；关闭同一 inode 的任意描述符都会释放锁。
            // 在打开文件句柄前拒绝第二个托管连接，包含别名和硬链接情况。
            // 同进程的外部或原生句柄由 LinuxExclusiveProcess 显式使用约定限制。
            if (OperatingSystem.IsLinux())
            {
                var timer = Stopwatch.StartNew();
                while (Interlocked.CompareExchange(ref _linuxOwner, 1, 0) != 0)
                {
                    if (timer.Elapsed >= options.LockTimeout)
                        throw new IOException("Linux compatibility already has an open connection in this process.");
                    Thread.Sleep(5);
                }

                _linuxLease = true;
            }

            _file = new(_path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite, 65536, FileOptions.RandomAccess);
            Acquire(Pending, 1);
            _pending = true;
            Acquire(Pending + 2, 510);
            _exclusive = true;
            // 热回滚日志恢复必须持有 PENDING 和 EXCLUSIVE，且不能持有 RESERVED。
            SqliteJournal.Recover(_file, _path);
            Acquire(Pending + 1, 1);
            _reserved = true;
            if (File.Exists(_path + "-wal") && new FileInfo(_path + "-wal").Length != 0)
                throw new NotSupportedException("WAL must be checkpointed by SQLite before phase 1 compatibility access.");
            if (_file.Length == 0)
            {
                string initial = Temp();
                try
                {
                    using (var w = new SqliteFileWriter(initial))
                        w.Finish([]);
                    using var input = File.OpenRead(initial);
                    input.CopyTo(_file);
                    _file.Flush(true);
                }
                finally
                {
                    File.Delete(initial);
                }
            }

            Reload();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>在超时范围内取得 SQLite 指定字节区间的排他锁。</summary>
    /// <param name="position">需要加锁的 SQLite 字节区间起点。</param>
    /// <param name="length">需要取得排他锁的字节区间长度。</param>
    private void Acquire(long position, long length)
    {
        if (OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException();
        var timer = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                _file.Lock(position, length);
                return;
            }
            catch (IOException) when (timer.Elapsed < _options.LockTimeout)
            {
                Thread.Sleep(5);
            }
        }
    }

    /// <summary>创建与主文件同目录的唯一临时文件路径。</summary>
    /// <returns>按当前方法语义生成、解析或规范化后的文本。</returns>
    private string Temp() => _path + ".puredb-" + Guid.NewGuid().ToString("N") + ".tmp";
    /// <summary>重新读取 SQLite 模式和行映射，清除基于旧文件版本的缓存。</summary>
    private void Reload()
    {
        _reader?.Dispose();
        _reader = new(_file, true, _options.PageCacheBytes, rollbackOnly: true);
        var schema = _reader.Table(1).Select(r => r.Values).ToArray();
        if (schema.Any(r => r.Length != 5 || r[0] is not string || r[1] is not string || r[2] is not string || r[3] is not long))
            throw new InvalidDataException("Invalid sqlite_schema.");
        if (schema.Any(r => (string)r[0]! is not ("table" or "index")))
            throw new NotSupportedException("Views and triggers are not supported in phase 1.");
        var catalog = new SqlCatalog();
        _roots.Clear();
        _indexRoots.Clear();
        _counts.Clear();
        foreach (var item in schema.Where(r => (string)r[0]! == "table"))
        {
            string name = (string)item[1]!;
            if (name.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException("AUTOINCREMENT and SQLite statistics/internal tables are not supported in phase 1.");
            if (item[4] is not string sql || new SqlParser(sql, sqliteFile: true).Parse() is not [CreateTable create])
                throw new NotSupportedException("Unsupported SQLite table definition.");
            if (!name.Equals(create.Name, StringComparison.Ordinal) || !name.Equals((string)item[2]!, StringComparison.Ordinal))
                throw new InvalidDataException("SQLite table name mismatch.");
            var table = new SqlTable
            {
                Id = catalog.NextId++,
                Name = name,
                Columns = create.Columns
            };
            catalog.Tables.Add(table);
            _roots.Add(table.Id, (long)item[3]!);
        }

        foreach (var item in schema.Where(r => (string)r[0]! == "index"))
        {
            string name = (string)item[1]!;
            if (name.StartsWith("sqlite_autoindex_", StringComparison.Ordinal) && item[4] is null)
                continue;
            if (name.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase) || item[4] is not string sql || new SqlParser(sql, sqliteFile: true).Parse() is not [CreateIndex create])
                throw new NotSupportedException("Unsupported SQLite index definition.");
            var table = catalog.Table(create.Table);
            if (!name.Equals(create.Name, StringComparison.Ordinal) || !table.Name.Equals((string)item[2]!, StringComparison.Ordinal))
                throw new InvalidDataException("SQLite index name mismatch.");
            string column = create.Expression.Op == "column" ? (string)create.Expression.Value! : "";
            if (column.Length > 0)
                table.Column(column);
            table.Indexes.Add(new(name, column, create.Unique, column.Length == 0 ? create.Expression : null));
            _indexRoots.Add(name, checked((uint)(long)item[3]!));
        }

        _catalog = catalog;
        _catalogBytes = JsonSerializer.SerializeToUtf8Bytes(catalog);
        _lastValue = null;
        _lastId = long.MinValue;
        _version++;
    }

    /// <summary>检查兼容存储尚未释放。</summary>
    private void Check()
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        if (_fault is not null)
            throw new IOException("SQLite compatibility write failed; close and recover the file before continuing.", _fault);
    }

    /// <summary>在存储同步保护期间运行快照回调。</summary>
    /// <param name="action">只在读取保护期内执行的快照回调。</param>
    /// <typeparam name="T">回调返回值或请求转换的结果类型。</typeparam>
    /// <returns>回调或类型转换得到的结果。</returns>
    public T Read<T>(Func<ISqlSnapshot, T> action)
    {
        lock (_sync)
        {
            Check();
            return action(this);
        }
    }

    /// <summary>校验并暂存 SQL 变更，选择受支持的增量页路径或完整镜像回退并原子提交。</summary>
    /// <param name="transform">在存储保护期内从一致快照生成原子变更的回调。</param>
    /// <param name="token">取消请求；原子提交开始后需完成提交或回滚。</param>
    /// <returns>完成时提供直接受影响行数的任务。</returns>
    public ValueTask<int> TransformAsync(Func<ISqlSnapshot, (Mutation[] Mutations, int Affected)> transform, CancellationToken token)
    {
        lock (_sync)
        {
            Check();
            ObjectDisposedException.ThrowIf(_draining, this);
            token.ThrowIfCancellationRequested();
            var result = transform(this);
            if (result.Mutations.Length == 0)
                return ValueTask.FromResult(result.Affected);
            var changes = result.Mutations.ToDictionary(m => m.Id);
            var catalog = changes.TryGetValue(-1, out var c) ? SqlAst.Load(c.Value.ToArray()) : _catalog;
            if (catalog.Tables.Any(t => t.Columns.Any(column => column.Type == "JSONB")))
                throw new NotSupportedException("Native SQLite JSONB is not implemented in phase 1; use JSON text columns.");
            string temp = Temp();
            try
            {
                if (_options.EnableIncrementalUpdates && TryIncremental(changes, token, out var pages))
                {
                    token.ThrowIfCancellationRequested();
                    SqliteJournal.CommitIncremental(_file, _path, pages, CommitStage);
                }
                else
                {
                    var overlay = new Overlay(this, changes);
                    SqlDatabase.WriteSqliteSnapshot(overlay, catalog, temp, token, unchecked(_reader.ChangeCounter + 1), unchecked(_reader.SchemaCookie + 1), _reader.UserVersion, _reader.ApplicationId);
                    token.ThrowIfCancellationRequested();
                    // 取消只影响准入边界；开始持久化后必须完成事务或完成回滚。
                    SqliteJournal.Commit(_file, _path, temp, CommitStage);
                }

                Reload();
            }
            catch
            {
                try
                {
                    SqliteJournal.Recover(_file, _path);
                    Reload();
                }
                catch (Exception recovery)
                {
                    _fault = recovery;
                }

                throw;
            }
            finally
            {
                File.Delete(temp);
            }

            return ValueTask.FromResult(result.Affected);
        }
    }

    /// <summary>验证变更是否处于已实现的增量子集，并在私有页中构造所有修改。</summary>
    /// <param name="changes">本事务按记录 ID 组织的最终变更。</param>
    /// <param name="token">取消请求；原子提交开始后需完成提交或回滚。</param>
    /// <param name="pages">适用时返回构造完成的事务私有脏页编辑器。</param>
    /// <returns>匹配、校验或操作成功时为 true，否则为 false；具体条件见方法说明。</returns>
    private bool TryIncremental(Dictionary<long, Mutation> changes, CancellationToken token, out Dictionary<uint, byte[]> pages)
    {
        var editor = new SqlitePageEditor(_file, Math.Max(4 * 1024 * 1024, _options.PageCacheBytes));
        pages = editor.Dirty;
        if (changes.ContainsKey(-1))
            return false;
        foreach (var mutation in changes.Values)
        {
            token.ThrowIfCancellationRequested();
            if (mutation.IsDelete || mutation.Id < 0)
                return false;
            var table = _catalog.Tables.Single(t => t.Id == mutation.IndexKey);
            var existing = ValueMemory(mutation.Id);
            if (existing.IsEmpty)
                return false;
            var before = SqlCodec.Decode(existing.Span, table.Columns.Count);
            var after = SqlCodec.Decode(mutation.Value.Span, table.Columns.Count);
            for (int i = 0; i < table.Columns.Count; i++)
                if (table.Columns[i].Unique && !SqlValueComparer.Instance.Equals(before[i], after[i]))
                    return false;
            foreach (var index in table.Indexes)
            {
                var binding = new SqlBinding();
                binding.Add(table, table.Name);
                var get = binding.Bind(index.Expression ?? new Expr("column", index.Column), new Dictionary<string, object?>());
                object? oldKey = get(before), newKey = get(after);
                if (!SqlValueComparer.Instance.Equals(oldKey, newKey))
                {
                    if (index.Unique || index.Expression is not null || oldKey is not long oldInteger || newKey is not long newInteger || !editor.TryReplaceIntegerIndex(_indexRoots[index.Name], oldInteger, newInteger, mutation.Id & ((1L << 40) - 1)))
                        return false;
                }
            }

            after[table.Primary] = null;
            if (!editor.TryReplace(checked((uint)_roots[table.Id]), mutation.Id & ((1L << 40) - 1), SqliteRecord.Encode(after)))
                return false;
        }

        editor.IncrementChangeCounter();
        return true;
    }

    /// <summary>将 SQL 行转换为 SQLite 记录载荷，整数主键由 rowid 表示。</summary>
    /// <param name="table">当前操作的不可变表模式。</param>
    /// <param name="row">按模式列序排列的当前行值。</param>
    /// <returns>本次编码或复制得到的字节数组。</returns>
    private byte[] Encode(SqlTable table, SqliteFileReader.Row row)
    {
        if (row.Id < 0 || row.Id >= 1L << 40 || row.Values.Length != table.Columns.Count)
            throw new NotSupportedException("Phase 1 requires nonnegative INTEGER PRIMARY KEY below 2^40 and no omitted/default columns.");
        var values = row.Values;
        if (values[table.Primary] is not null && !Equals(values[table.Primary], row.Id))
            throw new NotSupportedException("INTEGER PRIMARY KEY must alias rowid.");
        values[table.Primary] = row.Id;
        for (int i = 0; i < values.Length; i++)
        {
            var column = table.Columns[i];
            object? value = values[i];
            if (value is null)
            {
                if (column.NotNull || column.Primary)
                    throw new InvalidDataException("NULL in a required column.");
                continue;
            }

            bool supported = column.Type switch
            {
                "INTEGER" => value is long,
                "REAL" => value is long or double,
                "TEXT" => value is string,
                "BLOB" => value is byte[],
                "JSON" => value is string,
                _ => false
            };
            if (!supported)
                throw new NotSupportedException("SQLite dynamic value type is outside the phase 1 typed subset: " + table.Name + "." + column.Name);
            if (column.Type == "REAL" && value is long n)
                values[i] = (double)n;
            if (column.Type == "JSON")
            {
                using var json = JsonDocument.Parse((string)value);
            }
        }

        return SqlCodec.Encode(values);
    }

    /// <summary>是否支持原生持久化索引；兼容或叠加快照可关闭专用路径。</summary>
    public bool HasPersistentIndexes => false;
    /// <summary>当前数据文件的规范路径。</summary>
    public string Path => _path;
    /// <summary>用于识别文件替换的存储代次。</summary>
    public int Generation => unchecked((int)_reader.ChangeCounter);
    /// <summary>当前快照可见的数据文件提交长度。</summary>
    public long Length => _file.Length;
    /// <summary>当前数据文件的修改时间戳。</summary>
    public long SourceTime => File.GetLastWriteTimeUtc(_path).Ticks;
    /// <summary>当前视图对应的已提交版本。</summary>
    public long Version => _version;

    /// <summary>获取指定表或键空间的变化版本，用于缓存失效。</summary>
    /// <param name="key">用于查询或统计的内部表键或索引键。</param>
    /// <returns>按方法规定计算的数量、位置、版本或比较结果。</returns>
    public long KeyVersion(long key) => _version;
    /// <summary>统计指定表或键空间中的有效记录数。</summary>
    /// <param name="key">用于查询或统计的内部表键或索引键。</param>
    /// <returns>按方法规定计算的数量、位置、版本或比较结果。</returns>
    public int CountKey(long key)
    {
        if (key == -1)
            return 1;
        if (key <= 0 || key > int.MaxValue || !_roots.TryGetValue((int)key, out long root))
            return 0;
        if (!_counts.TryGetValue((int)key, out int count))
            _counts[(int)key] = count = checked((int)_reader.Count(root));
        return count;
    }

    /// <summary>当前快照中的有效记录总数。</summary>
    public long Count => 1 + _roots.Keys.Sum(k => (long)CountKey(k));

    /// <summary>枚举指定表或键空间内的有效记录 ID。</summary>
    /// <param name="key">用于查询或统计的内部表键或索引键。</param>
    /// <returns>按方法规定的顺序和可见性组织的结果集合。</returns>
    public IEnumerable<long> Ids(long key)
    {
        if (key == -1)
        {
            yield return -1;
            yield break;
        }

        if (key <= 0 || key > int.MaxValue || !_roots.TryGetValue((int)key, out long root))
            yield break;
        var table = _catalog.Tables.Single(t => t.Id == key);
        foreach (var row in _reader.Table(root))
        {
            _lastId = (key << 40) | row.Id;
            _lastValue = Encode(table, row);
            yield return _lastId;
        }
    }

    /// <summary>取得记录的只读内存；不得修改或越过所属读取保护期持有。</summary>
    /// <param name="id">原生记录 ID 或受支持的整数主键。</param>
    /// <returns>只读字节内存；不得修改，且必须遵守所属存储的生命周期。</returns>
    public ReadOnlyMemory<byte> ValueMemory(long id)
    {
        if (id == -1)
            return _catalogBytes;
        if (_lastId == id && _lastValue is not null)
            return _lastValue;
        long key = id >> 40;
        if (key <= 0 || key > int.MaxValue || !_roots.TryGetValue((int)key, out long root))
            return default;
        var row = _reader.Seek(root, id & ((1L << 40) - 1));
        if (row is null)
            return default;
        _lastId = id;
        _lastValue = Encode(_catalog.Tables.Single(t => t.Id == key), row);
        return _lastValue;
    }

    /// <summary>取得记录的只读字节片段；仅在读取保护期间有效，不得修改。</summary>
    /// <param name="id">原生记录 ID 或受支持的整数主键。</param>
    /// <returns>受当前读取保护期约束的只读字节片段。</returns>
    public ReadOnlySpan<byte> Value(long id) => ValueMemory(id).Span;
    /// <summary>按记录 ID 读取独立记录；不存在时返回 null。</summary>
    /// <param name="id">原生记录 ID 或受支持的整数主键。</param>
    /// <returns>计算或查找得到的结果；未找到或 SQL NULL 情况按方法说明返回 null。</returns>
    public DbRecord? Get(long id)
    {
        var value = ValueMemory(id);
        return value.IsEmpty ? null : new(id, id == -1 ? -1 : id >> 40, value.ToArray());
    }

    /// <summary>按索引键闭区间枚举当前视图中的有效记录。</summary>
    /// <param name="min">查询闭区间的下界，包含该值。</param>
    /// <param name="max">查询闭区间的上界，包含该值。</param>
    /// <returns>按方法规定的顺序和可见性组织的结果集合。</returns>
    public IEnumerable<DbRecord> Range(long min, long max)
    {
        foreach (int key in _roots.Keys.Order())
            if (key >= min && key <= max)
                foreach (long id in Ids(key))
                    yield return new(id, key, ValueMemory(id).ToArray());
    }

    /// <summary>此兼容或重建叠加快照不提供该项原生快照操作；调用明确失败。</summary>
    /// <param name="id">原生记录 ID 或受支持的整数主键。</param>
    /// <param name="key">用于查询或统计的内部表键或索引键。</param>
    /// <returns>此实现不返回结果，调用始终抛出异常。</returns>
    /// <exception cref="System.NotSupportedException">此快照未实现该项操作。</exception>
    public DiskIndex.Item IndexItem(long id, long key) => throw new NotSupportedException();
    /// <summary>此兼容或重建叠加快照不提供该项原生快照操作；调用明确失败。</summary>
    /// <param name="entry">已定位的日志索引项，包含偏移、长度及校验和。</param>
    /// <returns>此实现不返回结果，调用始终抛出异常。</returns>
    /// <exception cref="System.NotSupportedException">此快照未实现该项操作。</exception>
    public ReadOnlySpan<byte> IndexValue(DiskIndex.Item entry) => throw new NotSupportedException();
    /// <summary>此兼容或重建叠加快照不提供该项原生快照操作；调用明确失败。</summary>
    /// <param name="entry">已定位的日志索引项，包含偏移、长度及校验和。</param>
    /// <returns>此实现不返回结果，调用始终抛出异常。</returns>
    /// <exception cref="System.NotSupportedException">此快照未实现该项操作。</exception>
    public ReadOnlyMemory<byte> IndexMemory(DiskIndex.Item entry) => throw new NotSupportedException();
    /// <summary>此兼容或重建叠加快照不提供该项原生快照操作；调用明确失败。</summary>
    /// <param name="tableId">表在模式目录中的内部 ID。</param>
    /// <returns>此实现不返回结果，调用始终抛出异常。</returns>
    /// <exception cref="System.NotSupportedException">此快照未实现该项操作。</exception>
    public IEnumerable<DiskIndex.Item> TableEntries(int tableId) => throw new NotSupportedException();
    /// <summary>兼容后端不提供 VACUUM 或压缩接口；调用明确失败。</summary>
    /// <param name="token">取消请求；原子提交开始后需完成提交或回滚。</param>
    /// <returns>此实现不返回结果，调用始终抛出异常。</returns>
    /// <exception cref="System.NotSupportedException">此快照未实现该项操作。</exception>
    public ValueTask CompactAsync(CancellationToken token = default) => throw new NotSupportedException("Phase 1 transactions already rebuild the SQLite image; VACUUM is not exposed.");
    /// <summary>同步兼容后端主文件；该后端没有原生格式的旁路索引检查点。</summary>
    /// <param name="token">取消请求；原子提交开始后需完成提交或回滚。</param>
    /// <returns>表示操作最终完成的任务。</returns>
    public ValueTask CheckpointAsync(CancellationToken token = default)
    {
        lock (_sync)
        {
            Check();
            token.ThrowIfCancellationRequested();
            _file.Flush(true);
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>等待本后端关闭前的写入收尾；同步提交路径没有独立写入队列。</summary>
    /// <returns>表示操作最终完成的任务。</returns>
    public ValueTask DrainForCloseAsync()
    {
        lock (_sync)
        {
            _draining = true;
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>释放兼容后端持有的页、文件及独占锁。</summary>
    /// <returns>表示操作最终完成的任务。</returns>
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>关闭文件和锁资源，并撤销当前进程的兼容文件所有权登记。</summary>
    public void Dispose()
    {
        lock (_sync)
        {
            if (_closed)
                return;
            _closed = true;
            _reader?.Dispose();
            try
            {
                if (!OperatingSystem.IsMacOS())
                {
                    if (_reserved)
                        _file.Unlock(Pending + 1, 1);
                    if (_exclusive)
                        _file.Unlock(Pending + 2, 510);
                    if (_pending)
                        _file.Unlock(Pending, 1);
                }
            }
            finally
            {
                _file?.Dispose();
                if (_linuxLease)
                {
                    _linuxLease = false;
                    Interlocked.Exchange(ref _linuxOwner, 0);
                }
            }
        }
    }

    /// <summary>在原始 SQLite 快照上叠加本次事务变更，供整库重建回退路径读取。</summary>
    /// <param name="source">被叠加的原始 SQLite 存储快照。</param>
    /// <param name="changes">本事务按记录 ID 保存的最终变更。</param>
    private sealed class Overlay(SqliteStorage source, Dictionary<long, Mutation> changes) : ISqlSnapshot
    {
        /// <summary>是否支持原生持久化索引；兼容或叠加快照可关闭专用路径。</summary>
        public bool HasPersistentIndexes => false;
        /// <summary>当前数据文件的规范路径。</summary>
        public string Path => source.Path;
        /// <summary>用于识别文件替换的存储代次。</summary>
        public int Generation => source.Generation;
        /// <summary>当前快照可见的数据文件提交长度。</summary>
        public long Length => source.Length;
        /// <summary>当前数据文件的修改时间戳。</summary>
        public long SourceTime => source.SourceTime;
        /// <summary>当前视图对应的已提交版本。</summary>
        public long Version => source.Version;

        /// <summary>获取指定表或键空间的变化版本，用于缓存失效。</summary>
        /// <param name="key">用于查询或统计的内部表键或索引键。</param>
        /// <returns>按方法规定计算的数量、位置、版本或比较结果。</returns>
        public long KeyVersion(long key) => source.KeyVersion(key);
        /// <summary>枚举指定表或键空间内的有效记录 ID。</summary>
        /// <param name="key">用于查询或统计的内部表键或索引键。</param>
        /// <returns>按方法规定的顺序和可见性组织的结果集合。</returns>
        public IEnumerable<long> Ids(long key) => source.Ids(key).Where(id => !changes.ContainsKey(id)).Concat(changes.Values.Where(m => !m.IsDelete && m.IndexKey == key).Select(m => m.Id)).Order();
        /// <summary>取得记录的只读内存；不得修改或越过所属读取保护期持有。</summary>
        /// <param name="id">原生记录 ID 或受支持的整数主键。</param>
        /// <returns>只读字节内存；不得修改，且必须遵守所属存储的生命周期。</returns>
        public ReadOnlyMemory<byte> ValueMemory(long id) => changes.TryGetValue(id, out var m) ? m.IsDelete ? default : m.Value : source.ValueMemory(id);
        /// <summary>取得记录的只读字节片段；仅在读取保护期间有效，不得修改。</summary>
        /// <param name="id">原生记录 ID 或受支持的整数主键。</param>
        /// <returns>受当前读取保护期约束的只读字节片段。</returns>
        public ReadOnlySpan<byte> Value(long id) => ValueMemory(id).Span;
        /// <summary>此兼容或重建叠加快照不提供该项原生快照操作；调用明确失败。</summary>
        /// <param name="id">原生记录 ID 或受支持的整数主键。</param>
        /// <returns>此实现不返回结果，调用始终抛出异常。</returns>
        /// <exception cref="System.NotSupportedException">此快照未实现该项操作。</exception>
        public DbRecord? Get(long id) => throw new NotSupportedException();
        /// <summary>此兼容或重建叠加快照不提供该项原生快照操作；调用明确失败。</summary>
        /// <param name="key">用于查询或统计的内部表键或索引键。</param>
        /// <returns>此实现不返回结果，调用始终抛出异常。</returns>
        /// <exception cref="System.NotSupportedException">此快照未实现该项操作。</exception>
        public int CountKey(long key) => throw new NotSupportedException();
        /// <summary>此兼容或重建叠加快照不提供该项原生快照操作；调用明确失败。</summary>
        /// <exception cref="System.NotSupportedException">此快照未实现该项操作。</exception>
        public long Count => throw new NotSupportedException();

        /// <summary>此兼容或重建叠加快照不提供该项原生快照操作；调用明确失败。</summary>
        /// <param name="min">查询闭区间的下界，包含该值。</param>
        /// <param name="max">查询闭区间的上界，包含该值。</param>
        /// <returns>此实现不返回结果，调用始终抛出异常。</returns>
        /// <exception cref="System.NotSupportedException">此快照未实现该项操作。</exception>
        public IEnumerable<DbRecord> Range(long min, long max) => throw new NotSupportedException();
        /// <summary>此兼容或重建叠加快照不提供该项原生快照操作；调用明确失败。</summary>
        /// <param name="id">原生记录 ID 或受支持的整数主键。</param>
        /// <param name="key">用于查询或统计的内部表键或索引键。</param>
        /// <returns>此实现不返回结果，调用始终抛出异常。</returns>
        /// <exception cref="System.NotSupportedException">此快照未实现该项操作。</exception>
        public DiskIndex.Item IndexItem(long id, long key) => throw new NotSupportedException();
        /// <summary>此兼容或重建叠加快照不提供该项原生快照操作；调用明确失败。</summary>
        /// <param name="entry">已定位的日志索引项，包含偏移、长度及校验和。</param>
        /// <returns>此实现不返回结果，调用始终抛出异常。</returns>
        /// <exception cref="System.NotSupportedException">此快照未实现该项操作。</exception>
        public ReadOnlySpan<byte> IndexValue(DiskIndex.Item entry) => throw new NotSupportedException();
        /// <summary>此兼容或重建叠加快照不提供该项原生快照操作；调用明确失败。</summary>
        /// <param name="entry">已定位的日志索引项，包含偏移、长度及校验和。</param>
        /// <returns>此实现不返回结果，调用始终抛出异常。</returns>
        /// <exception cref="System.NotSupportedException">此快照未实现该项操作。</exception>
        public ReadOnlyMemory<byte> IndexMemory(DiskIndex.Item entry) => throw new NotSupportedException();
        /// <summary>此兼容或重建叠加快照不提供该项原生快照操作；调用明确失败。</summary>
        /// <param name="tableId">表在模式目录中的内部 ID。</param>
        /// <returns>此实现不返回结果，调用始终抛出异常。</returns>
        /// <exception cref="System.NotSupportedException">此快照未实现该项操作。</exception>
        public IEnumerable<DiskIndex.Item> TableEntries(int tableId) => throw new NotSupportedException();
    }
}
