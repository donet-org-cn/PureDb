using System.Runtime.InteropServices;
#if STANDALONE_SQLITE
using Durability = PureDb.Benchmarks.SqliteDurability;
using DbRecord = PureDb.Benchmarks.SqliteRecord;
#else
using PureDb;
#endif
namespace PureDb.Benchmarks;

// Only the benchmark loads native SQLite. The storage library has no native dependencies.
internal sealed class Sqlite : IDisposable
{
    private nint _db;
    public static string Version => Marshal.PtrToStringUTF8(Native.sqlite3_libversion())!;
    static Sqlite()
    {
        NativeLibrary.SetDllImportResolver(typeof(Sqlite).Assembly, (name, _, _) =>
            name == "sqlite-benchmark" ? NativeLibrary.Load(Environment.GetEnvironmentVariable("SQLITE_LIBRARY") ??
                (OperatingSystem.IsWindows() ? "winsqlite3.dll" : OperatingSystem.IsMacOS() ? "libsqlite3.dylib" : "libsqlite3.so.0")) : 0);
    }
    public Sqlite(string path, Durability durability, bool initialize = false, bool configure = true)
    {
        Check(Native.sqlite3_open_v2(path, out _db, 2 | 4 | 0x10000, 0));
        Check(Native.sqlite3_busy_timeout(_db, 60000));
        if (configure)
            Exec("PRAGMA journal_mode=WAL; PRAGMA synchronous=" + (durability == Durability.Full ? "FULL" : "NORMAL") + "; PRAGMA cache_size=-8192;");
        if (initialize)
            Exec("CREATE TABLE records(id INTEGER PRIMARY KEY, index_key INTEGER NOT NULL, value BLOB NOT NULL); CREATE INDEX ix_records ON records(index_key,id);");
    }
    public void Exec(string sql) => Check(Native.sqlite3_exec(_db, sql, 0, 0, 0));
    public Statement Prepare(string sql) => new(this, sql);
    private void Check(int rc)
    {
        if (rc != 0)
            throw new IOException($"SQLite {rc}: {Marshal.PtrToStringUTF8(Native.sqlite3_errmsg(_db))}");
    }
    public void Dispose()
    {
        if (_db != 0)
        {
            Check(Native.sqlite3_close_v2(_db));
            _db = 0;
        }
    }
    internal sealed class Statement : IDisposable
    {
        private readonly Sqlite _owner;
        private nint _stmt;
        public Statement(Sqlite owner, string sql)
        {
            _owner = owner;
            owner.Check(Native.sqlite3_prepare_v2(owner._db, sql, -1, out _stmt, 0));
        }
        public void Bind(int i, long value) => _owner.Check(Native.sqlite3_bind_int64(_stmt, i, value));
        /// <summary>按 UTF-8 绑定文本，由 SQLite 复制数据，保持与托管写入相同的值所有权。</summary>
        /// <param name="i">从 1 开始的参数位置。</param>
        /// <param name="value">需要绑定的文本值。</param>
        public void Bind(int i, string value) => _owner.Check(Native.sqlite3_bind_text(_stmt, i, value, -1, new nint(-1)));
        public void Bind(int i, byte[] value) => _owner.Check(Native.sqlite3_bind_blob(_stmt, i, value, value.Length, new nint(-1)));
        public bool Step()
        {
            int rc = Native.sqlite3_step(_stmt);
            if (rc == 100)
                return true;
            if (rc == 101)
                return false;
            _owner.Check(rc);
            return false;
        }
        public object?[] Values()
        {
            // Match the managed engine's direct row-array materialization: no LINQ
            // iterator, closure or intermediate list in the SQLite measurement path.
            var values = new object?[Native.sqlite3_column_count(_stmt)];
            for (int i = 0; i < values.Length; i++)
                values[i] = Native.sqlite3_column_type(_stmt, i) switch
                {
                    1 => (object)Native.sqlite3_column_int64(_stmt, i),
                    2 => Native.sqlite3_column_double(_stmt, i),
                    3 => Marshal.PtrToStringUTF8(Native.sqlite3_column_text(_stmt, i)),
                    4 => Blob(i),
                    _ => null
                };
            return values;
        }
        private byte[] Blob(int i)
        {
            int count = Native.sqlite3_column_bytes(_stmt, i);
            var bytes = new byte[count];
            if (count > 0)
                Marshal.Copy(Native.sqlite3_column_blob(_stmt, i), bytes, 0, count);
            return bytes;
        }
        public long Int64(int i) => Native.sqlite3_column_int64(_stmt, i);
        public DbRecord Record()
        {
            int size = Native.sqlite3_column_bytes(_stmt, 2);
            var value = new byte[size];
            if (size > 0)
                Marshal.Copy(Native.sqlite3_column_blob(_stmt, 2), value, 0, size);
            return new(Int64(0), Int64(1), value);
        }
        public void Reset() => _owner.Check(Native.sqlite3_reset(_stmt));
        public void Dispose()
        {
            if (_stmt != 0)
            {
                _owner.Check(Native.sqlite3_finalize(_stmt));
                _stmt = 0;
            }
        }
    }
    private static class Native
    {
        [DllImport("sqlite-benchmark", CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_column_count(nint stmt);
        [DllImport("sqlite-benchmark", CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_column_type(nint stmt, int i);
        [DllImport("sqlite-benchmark", CallingConvention = CallingConvention.Cdecl)] internal static extern double sqlite3_column_double(nint stmt, int i);
        [DllImport("sqlite-benchmark", CallingConvention = CallingConvention.Cdecl)] internal static extern nint sqlite3_column_text(nint stmt, int i);
        private const string Lib = "sqlite-benchmark";
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern nint sqlite3_libversion();
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out nint db, int flags, nint vfs);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_close_v2(nint db);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern nint sqlite3_errmsg(nint db);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_busy_timeout(nint db, int ms);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_exec(nint db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, nint callback, nint arg, nint error);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_prepare_v2(nint db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, int size, out nint stmt, nint tail);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_bind_int64(nint stmt, int i, long value);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_bind_text(nint stmt, int i, [MarshalAs(UnmanagedType.LPUTF8Str)] string value, int size, nint destructor);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_bind_blob(nint stmt, int i, byte[] value, int size, nint destructor);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_step(nint stmt);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_reset(nint stmt);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_finalize(nint stmt);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern long sqlite3_column_int64(nint stmt, int i);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_column_bytes(nint stmt, int i);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern nint sqlite3_column_blob(nint stmt, int i);
    }
}
