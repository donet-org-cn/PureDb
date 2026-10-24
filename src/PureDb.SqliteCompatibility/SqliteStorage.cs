using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;

[assembly: InternalsVisibleTo("PureDb.CompatibilityTests")]
namespace PureDb.SqliteCompatibility;

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

    internal SqliteStorage(string path, SqliteCompatibilityOptions options)
    {
        if (!OperatingSystem.IsWindows() && !(OperatingSystem.IsLinux() && options.LinuxExclusiveProcess))
            throw new PlatformNotSupportedException("On Linux, explicitly enable LinuxExclusiveProcess and use a dedicated managed process without other handles to this database. Other platforms are not supported.");
        _path = System.IO.Path.GetFullPath(path); _options = options;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
        try
        {
            // fcntl locks belong to the process, and closing ANY descriptor for the
            // inode releases them. Refuse a second managed open before touching a file
            // handle, including aliases/hard links. External/native handles in this same
            // process are outside the explicit LinuxExclusiveProcess contract.
            if (OperatingSystem.IsLinux())
            {
                var timer = Stopwatch.StartNew();
                while (Interlocked.CompareExchange(ref _linuxOwner, 1, 0) != 0)
                {
                    if (timer.Elapsed >= options.LockTimeout) throw new IOException("Linux compatibility already has an open connection in this process.");
                    Thread.Sleep(5);
                }
                _linuxLease = true;
            }
            _file = new(_path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite, 65536, FileOptions.RandomAccess);
            Acquire(Pending, 1); _pending = true;
            Acquire(Pending + 2, 510); _exclusive = true;
            // Hot-journal recovery must hold PENDING + EXCLUSIVE without RESERVED.
            SqliteJournal.Recover(_file, _path);
            Acquire(Pending + 1, 1); _reserved = true;
            if (File.Exists(_path + "-wal") && new FileInfo(_path + "-wal").Length != 0) throw new NotSupportedException("WAL must be checkpointed by SQLite before phase 1 compatibility access.");
            if (_file.Length == 0)
            {
                string initial = Temp();
                try { using (var w = new SqliteFileWriter(initial)) w.Finish([]); using var input = File.OpenRead(initial); input.CopyTo(_file); _file.Flush(true); }
                finally { File.Delete(initial); }
            }
            Reload();
        }
        catch { Dispose(); throw; }
    }
    private void Acquire(long position, long length)
    {
        if (OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
        var timer = Stopwatch.StartNew();
        while (true)
        {
            try { _file.Lock(position, length); return; }
            catch (IOException) when (timer.Elapsed < _options.LockTimeout) { Thread.Sleep(5); }
        }
    }
    private string Temp() => _path + ".puredb-" + Guid.NewGuid().ToString("N") + ".tmp";
    private void Reload()
    {
        _reader?.Dispose(); _reader = new(_file, true, _options.PageCacheBytes, rollbackOnly: true);
        var schema = _reader.Table(1).Select(r => r.Values).ToArray();
        if (schema.Any(r => r.Length != 5 || r[0] is not string || r[1] is not string || r[2] is not string || r[3] is not long)) throw new InvalidDataException("Invalid sqlite_schema.");
        if (schema.Any(r => (string)r[0]! is not ("table" or "index"))) throw new NotSupportedException("Views and triggers are not supported in phase 1.");
        var catalog = new SqlCatalog(); _roots.Clear(); _indexRoots.Clear(); _counts.Clear();
        foreach (var item in schema.Where(r => (string)r[0]! == "table"))
        {
            string name = (string)item[1]!;
            if (name.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase)) throw new NotSupportedException("AUTOINCREMENT and SQLite statistics/internal tables are not supported in phase 1.");
            if (item[4] is not string sql || new SqlParser(sql, sqliteFile: true).Parse() is not [CreateTable create]) throw new NotSupportedException("Unsupported SQLite table definition.");
            if (!name.Equals(create.Name, StringComparison.Ordinal) || !name.Equals((string)item[2]!, StringComparison.Ordinal)) throw new InvalidDataException("SQLite table name mismatch.");
            var table = new SqlTable { Id = catalog.NextId++, Name = name, Columns = create.Columns };
            catalog.Tables.Add(table); _roots.Add(table.Id, (long)item[3]!);
        }
        foreach (var item in schema.Where(r => (string)r[0]! == "index"))
        {
            string name = (string)item[1]!;
            if (name.StartsWith("sqlite_autoindex_", StringComparison.Ordinal) && item[4] is null) continue;
            if (name.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase) || item[4] is not string sql || new SqlParser(sql, sqliteFile: true).Parse() is not [CreateIndex create]) throw new NotSupportedException("Unsupported SQLite index definition.");
            var table = catalog.Table(create.Table);
            if (!name.Equals(create.Name, StringComparison.Ordinal) || !table.Name.Equals((string)item[2]!, StringComparison.Ordinal)) throw new InvalidDataException("SQLite index name mismatch.");
            string column = create.Expression.Op == "column" ? (string)create.Expression.Value! : "";
            if (column.Length > 0) table.Column(column);
            table.Indexes.Add(new(name, column, create.Unique, column.Length == 0 ? create.Expression : null));
            _indexRoots.Add(name, checked((uint)(long)item[3]!));
        }
        _catalog = catalog; _catalogBytes = JsonSerializer.SerializeToUtf8Bytes(catalog); _lastValue = null; _lastId = long.MinValue; _version++;
    }
    private void Check() { ObjectDisposedException.ThrowIf(_closed, this); if (_fault is not null) throw new IOException("SQLite compatibility write failed; close and recover the file before continuing.", _fault); }
    public T Read<T>(Func<ISqlSnapshot, T> action) { lock (_sync) { Check(); return action(this); } }
    public ValueTask<int> TransformAsync(Func<ISqlSnapshot, (Mutation[] Mutations, int Affected)> transform, CancellationToken token)
    {
        lock (_sync)
        {
            Check(); ObjectDisposedException.ThrowIf(_draining, this); token.ThrowIfCancellationRequested();
            var result = transform(this);
            if (result.Mutations.Length == 0) return ValueTask.FromResult(result.Affected);
            var changes = result.Mutations.ToDictionary(m => m.Id);
            var catalog = changes.TryGetValue(-1, out var c) ? SqlAst.Load(c.Value.ToArray()) : _catalog;
            if (catalog.Tables.Any(t => t.Columns.Any(column => column.Type == "JSONB"))) throw new NotSupportedException("Native SQLite JSONB is not implemented in phase 1; use JSON text columns.");
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
                    // Cancellation is an admission boundary: a durable transaction must finish or roll back.
                    SqliteJournal.Commit(_file, _path, temp, CommitStage);
                }
                Reload();
            }
            catch
            {
                try { SqliteJournal.Recover(_file, _path); Reload(); }
                catch (Exception recovery) { _fault = recovery; }
                throw;
            }
            finally { File.Delete(temp); }
            return ValueTask.FromResult(result.Affected);
        }
    }
    private bool TryIncremental(Dictionary<long, Mutation> changes, CancellationToken token, out Dictionary<uint, byte[]> pages)
    {
        var editor = new SqlitePageEditor(_file, Math.Max(4 * 1024 * 1024, _options.PageCacheBytes)); pages = editor.Dirty;
        if (changes.ContainsKey(-1)) return false;
        foreach (var mutation in changes.Values)
        {
            token.ThrowIfCancellationRequested();
            if (mutation.IsDelete || mutation.Id < 0) return false;
            var table = _catalog.Tables.Single(t => t.Id == mutation.IndexKey);
            var existing = ValueMemory(mutation.Id); if (existing.IsEmpty) return false;
            var before = SqlCodec.Decode(existing.Span, table.Columns.Count);
            var after = SqlCodec.Decode(mutation.Value.Span, table.Columns.Count);
            for (int i = 0; i < table.Columns.Count; i++)
                if (table.Columns[i].Unique && !SqlValueComparer.Instance.Equals(before[i], after[i])) return false;
            foreach (var index in table.Indexes)
            {
                var binding = new SqlBinding(); binding.Add(table, table.Name);
                var get = binding.Bind(index.Expression ?? new Expr("column", index.Column), new Dictionary<string, object?>());
                object? oldKey = get(before), newKey = get(after);
                if (!SqlValueComparer.Instance.Equals(oldKey, newKey))
                {
                    if (index.Unique || index.Expression is not null || oldKey is not long oldInteger || newKey is not long newInteger ||
                        !editor.TryReplaceIntegerIndex(_indexRoots[index.Name], oldInteger, newInteger, mutation.Id & ((1L << 40) - 1))) return false;
                }
            }
            after[table.Primary] = null;
            if (!editor.TryReplace(checked((uint)_roots[table.Id]), mutation.Id & ((1L << 40) - 1), SqliteRecord.Encode(after))) return false;
        }
        editor.IncrementChangeCounter(); return true;
    }
    private byte[] Encode(SqlTable table, SqliteFileReader.Row row)
    {
        if (row.Id < 0 || row.Id >= 1L << 40 || row.Values.Length != table.Columns.Count) throw new NotSupportedException("Phase 1 requires nonnegative INTEGER PRIMARY KEY below 2^40 and no omitted/default columns.");
        var values = row.Values;
        if (values[table.Primary] is not null && !Equals(values[table.Primary], row.Id)) throw new NotSupportedException("INTEGER PRIMARY KEY must alias rowid.");
        values[table.Primary] = row.Id;
        for (int i = 0; i < values.Length; i++)
        {
            var column = table.Columns[i]; object? value = values[i];
            if (value is null) { if (column.NotNull || column.Primary) throw new InvalidDataException("NULL in a required column."); continue; }
            bool supported = column.Type switch { "INTEGER" => value is long, "REAL" => value is long or double, "TEXT" => value is string, "BLOB" => value is byte[], "JSON" => value is string, _ => false };
            if (!supported) throw new NotSupportedException("SQLite dynamic value type is outside the phase 1 typed subset: " + table.Name + "." + column.Name);
            if (column.Type == "REAL" && value is long n) values[i] = (double)n;
            if (column.Type == "JSON") { using var json = JsonDocument.Parse((string)value); }
        }
        return SqlCodec.Encode(values);
    }
    public bool HasPersistentIndexes => false;
    public string Path => _path;
    public int Generation => unchecked((int)_reader.ChangeCounter);
    public long Length => _file.Length;
    public long SourceTime => File.GetLastWriteTimeUtc(_path).Ticks;
    public long Version => _version;
    public long KeyVersion(long key) => _version;
    public int CountKey(long key)
    {
        if (key == -1) return 1; if (key <= 0 || key > int.MaxValue || !_roots.TryGetValue((int)key, out long root)) return 0;
        if (!_counts.TryGetValue((int)key, out int count)) _counts[(int)key] = count = checked((int)_reader.Count(root)); return count;
    }
    public long Count => 1 + _roots.Keys.Sum(k => (long)CountKey(k));
    public IEnumerable<long> Ids(long key)
    {
        if (key == -1) { yield return -1; yield break; }
        if (key <= 0 || key > int.MaxValue || !_roots.TryGetValue((int)key, out long root)) yield break;
        var table = _catalog.Tables.Single(t => t.Id == key);
        foreach (var row in _reader.Table(root)) { _lastId = (key << 40) | row.Id; _lastValue = Encode(table, row); yield return _lastId; }
    }
    public ReadOnlyMemory<byte> ValueMemory(long id)
    {
        if (id == -1) return _catalogBytes;
        if (_lastId == id && _lastValue is not null) return _lastValue;
        long key = id >> 40;
        if (key <= 0 || key > int.MaxValue || !_roots.TryGetValue((int)key, out long root)) return default;
        var row = _reader.Seek(root, id & ((1L << 40) - 1));
        if (row is null) return default;
        _lastId = id; _lastValue = Encode(_catalog.Tables.Single(t => t.Id == key), row); return _lastValue;
    }
    public ReadOnlySpan<byte> Value(long id) => ValueMemory(id).Span;
    public DbRecord? Get(long id) { var value = ValueMemory(id); return value.IsEmpty ? null : new(id, id == -1 ? -1 : id >> 40, value.ToArray()); }
    public IEnumerable<DbRecord> Range(long min, long max)
    { foreach (int key in _roots.Keys.Order()) if (key >= min && key <= max) foreach (long id in Ids(key)) yield return new(id, key, ValueMemory(id).ToArray()); }
    public DiskIndex.Item IndexItem(long id, long key) => throw new NotSupportedException();
    public ReadOnlySpan<byte> IndexValue(DiskIndex.Item entry) => throw new NotSupportedException();
    public ReadOnlyMemory<byte> IndexMemory(DiskIndex.Item entry) => throw new NotSupportedException();
    public IEnumerable<DiskIndex.Item> TableEntries(int tableId) => throw new NotSupportedException();
    public ValueTask CompactAsync(CancellationToken token = default) => throw new NotSupportedException("Phase 1 transactions already rebuild the SQLite image; VACUUM is not exposed.");
    public ValueTask CheckpointAsync(CancellationToken token = default) { lock (_sync) { Check(); token.ThrowIfCancellationRequested(); _file.Flush(true); return ValueTask.CompletedTask; } }
    public ValueTask DrainForCloseAsync() { lock (_sync) { _draining = true; return ValueTask.CompletedTask; } }
    public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    public void Dispose()
    {
        lock (_sync)
        {
            if (_closed) return; _closed = true; _reader?.Dispose();
            try { if (!OperatingSystem.IsMacOS()) { if (_reserved) _file.Unlock(Pending + 1, 1); if (_exclusive) _file.Unlock(Pending + 2, 510); if (_pending) _file.Unlock(Pending, 1); } }
            finally { _file?.Dispose(); if (_linuxLease) { _linuxLease = false; Interlocked.Exchange(ref _linuxOwner, 0); } }
        }
    }
    private sealed class Overlay(SqliteStorage source, Dictionary<long, Mutation> changes) : ISqlSnapshot
    {
        public bool HasPersistentIndexes => false;
        public string Path => source.Path; public int Generation => source.Generation; public long Length => source.Length; public long SourceTime => source.SourceTime;
        public long Version => source.Version; public long KeyVersion(long key) => source.KeyVersion(key);
        public IEnumerable<long> Ids(long key) => source.Ids(key).Where(id => !changes.ContainsKey(id)).Concat(changes.Values.Where(m => !m.IsDelete && m.IndexKey == key).Select(m => m.Id)).Order();
        public ReadOnlyMemory<byte> ValueMemory(long id) => changes.TryGetValue(id, out var m) ? m.IsDelete ? default : m.Value : source.ValueMemory(id);
        public ReadOnlySpan<byte> Value(long id) => ValueMemory(id).Span;
        public DbRecord? Get(long id) => throw new NotSupportedException(); public int CountKey(long key) => throw new NotSupportedException(); public long Count => throw new NotSupportedException();
        public IEnumerable<DbRecord> Range(long min, long max) => throw new NotSupportedException();
        public DiskIndex.Item IndexItem(long id, long key) => throw new NotSupportedException(); public ReadOnlySpan<byte> IndexValue(DiskIndex.Item entry) => throw new NotSupportedException();
        public ReadOnlyMemory<byte> IndexMemory(DiskIndex.Item entry) => throw new NotSupportedException(); public IEnumerable<DiskIndex.Item> TableEntries(int tableId) => throw new NotSupportedException();
    }
}
