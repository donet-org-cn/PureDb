using System.Data;
using System.Data.Common;
using PureDb;
using PureDb.AdoNet;

string folder = Path.GetFullPath(Path.Combine("artifacts", "ado-tests", Guid.NewGuid().ToString("N"))); Directory.CreateDirectory(folder);
string connectionString = new DbConnectionStringBuilder { ["Data Source"] = Path.Combine(folder, "ado.pdb"), ["Durability"] = "Buffered" }.ConnectionString;
void Assert(bool value, string message) { if (!value) throw new Exception(message); }
async Task Reject<T>(Func<Task> run) where T : Exception { try { await run(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
int passed = 0;
async Task Test(string name, Func<Task> run) { await run(); passed++; Console.WriteLine("PASS " + name); }
await using var connection = new PureDbConnection(connectionString);
await Test("factory, connection lifecycle, prepared typed and positional parameters", async () =>
{
    DbProviderFactories.RegisterFactory("PureDb", PureDbProviderFactory.Instance);
    var factory = DbProviderFactories.GetFactory("PureDb");
    Assert(factory.CreateConnection() is PureDbConnection && factory.CreateCommand() is PureDbCommand && factory.CreateParameter() is PureDbParameter && factory.CanCreateDataAdapter, "factory");
    int transitions = 0; connection.StateChange += (_, _) => transitions++;
    await connection.OpenAsync(); connection.Close(); await connection.OpenAsync(); Assert(transitions == 3, "state changes");
    using DbCommand create = connection.CreateCommand(); create.CommandText = "CREATE TABLE t(id INTEGER PRIMARY KEY,n TEXT UNIQUE,v BLOB,amount REAL,nullable INTEGER)";
    Assert(await create.ExecuteNonQueryAsync() == 0, "DDL affected");
    using var insert = connection.CreateCommand(); insert.CommandText = "INSERT INTO t VALUES(@id,@n,@v,@amount,@nil)";
    insert.Parameters.AddWithValue("@id", 1); insert.Parameters.AddWithValue("n", "你好 O'Brien"); insert.Parameters.AddWithValue("v", new byte[] { 1, 2, 3 }); insert.Parameters.AddWithValue("amount", 1.25); insert.Parameters.AddWithValue("nil", DBNull.Value); insert.Prepare();
    Assert(insert.ExecuteNonQuery() == 1, "insert");
    insert.CommandText = "INSERT INTO t VALUES(?1,?2,?3,?4,?5)"; insert.Parameters.Clear();
    foreach (object? value in new object?[] { 2L, "two", null, 2d, null }) insert.Parameters.Add(new PureDbParameter("", value));
    Assert(await insert.ExecuteNonQueryAsync() == 1, "positional parameters");
});
await Test("reader types, DBNull, bytes/chars slices, multiple results, enumeration", async () =>
{
    using var command = connection.CreateCommand(); command.CommandText = "SELECT id,n,v,amount,nullable FROM t ORDER BY id; SELECT COUNT(*) FROM t";
    await using var reader = await command.ExecuteReaderAsync();
    Assert(reader.FieldCount == 5 && reader.HasRows && reader.GetOrdinal("N") == 1 && reader.GetFieldType(0) == typeof(long), "reader schema");
    await Reject<InvalidOperationException>(() => Task.Run(() => reader.GetValue(0)));
    Assert(await reader.ReadAsync() && reader.GetInt32(0) == 1 && reader.GetString(1) == "你好 O'Brien" && reader.GetDouble(3) == 1.25 && reader.IsDBNull(4) && reader.GetValue(4) == DBNull.Value, "typed values");
    var bytes = new byte[2]; Assert(reader.GetBytes(2, 0, null, 0, 0) == 3 && reader.GetBytes(2, 1, bytes, 0, 2) == 2 && bytes.SequenceEqual(new byte[] { 2, 3 }), "byte slice");
    var chars = new char[2]; Assert(reader.GetChars(1, 0, chars, 0, 2) == 2 && new string(chars) == "你好", "chars");
    Assert(await reader.ReadAsync() && !await reader.ReadAsync(), "end rows");
    await Reject<InvalidOperationException>(() => Task.Run(() => reader.GetValue(0)));
    Assert(await reader.NextResultAsync() && reader.Read() && reader.GetInt64(0) == 2 && !reader.NextResult(), "next result");
    reader.Close(); Assert(reader.IsClosed, "reader close");
});
await Test("DataTable.Load, DataAdapter.Fill and Update", async () =>
{
    using var select = connection.CreateCommand(); select.CommandText = "SELECT id,n FROM t ORDER BY id";
    var loaded = new DataTable(); using (var reader = select.ExecuteReader()) loaded.Load(reader);
    Assert(loaded.Rows.Count == 2 && loaded.Columns[0].DataType == typeof(long), "DataTable.Load");
    using var adapter = new PureDbDataAdapter(select); var data = new DataTable(); Assert(adapter.Fill(data) == 2, "adapter fill");
    using var update = connection.CreateCommand(); update.CommandText = "UPDATE t SET n=@n WHERE id=@id";
    update.Parameters.Add(new PureDbParameter("n", null) { SourceColumn = "n" }); update.Parameters.Add(new PureDbParameter("id", null) { SourceColumn = "id" });
    adapter.UpdateCommand = update; data.Rows[1]["n"] = "adapter"; Assert(adapter.Update(data) == 1, "adapter update");
    Assert(connection.GetSchema("Tables").Rows.Count == 1 && connection.GetSchema("Columns", new string?[] { null, null, "t", "n" }).Rows.Count == 1, "GetSchema");
    await Task.CompletedTask;
});
await Test("transactions read your writes, isolation, commit and rollback", async () =>
{
    await using var other = new PureDbConnection(connectionString); await other.OpenAsync();
    using var tx = connection.BeginTransaction(IsolationLevel.Serializable); using var command = connection.CreateCommand(); command.Transaction = tx;
    command.CommandText = "UPDATE t SET n='staged' WHERE id=2"; Assert(command.ExecuteNonQuery() == 1, "staged update");
    command.CommandText = "SELECT n FROM t WHERE id=2"; Assert((string)command.ExecuteScalar()! == "staged", "read own writes");
    using var external = other.CreateCommand(); external.CommandText = "SELECT n FROM t WHERE id=2"; Assert((string)external.ExecuteScalar()! == "adapter", "no dirty reads");
    await tx.CommitAsync(); Assert((string)external.ExecuteScalar()! == "staged", "commit visible");
    using (var rollback = connection.BeginTransaction()) { command.Transaction = rollback; command.CommandText = "DELETE FROM t WHERE id=2"; command.ExecuteNonQuery(); }
    command.Transaction = null; command.CommandText = "SELECT COUNT(*) FROM t"; Assert((long)command.ExecuteScalar()! == 2, "dispose rollback");
});
await Test("transaction conflict and failed statement never partially commit", async () =>
{
    using var tx = connection.BeginTransaction(); using var command = connection.CreateCommand(); command.Transaction = tx; command.CommandText = "UPDATE t SET n='lost' WHERE id=2"; command.ExecuteNonQuery();
    await using (var other = new PureDbConnection(connectionString)) { await other.OpenAsync(); using var c = other.CreateCommand(); c.CommandText = "UPDATE t SET n='winner' WHERE id=2"; c.ExecuteNonQuery(); }
    await Reject<DBConcurrencyException>(() => tx.CommitAsync());
    command.Transaction = null; command.CommandText = "SELECT n FROM t WHERE id=2"; Assert((string)command.ExecuteScalar()! == "winner", "conflict no overwrite");
    using var failed = connection.BeginTransaction(); command.Transaction = failed; command.CommandText = "INSERT INTO t VALUES(3,'three',NULL,3,NULL); INSERT INTO t VALUES(1,'duplicate',NULL,1,NULL)";
    await Reject<SqlException>(() => command.ExecuteNonQueryAsync()); await Reject<InvalidOperationException>(() => failed.CommitAsync()); failed.Rollback();
    command.Transaction = null; command.CommandText = "SELECT COUNT(*) FROM t"; Assert((long)command.ExecuteScalar()! == 2, "no partial insert");
});
await Test("transaction command ownership, cancellation, unsupported features", async () =>
{
    using var tx = connection.BeginTransaction(); using var command = connection.CreateCommand(); command.CommandText = "SELECT COUNT(*) FROM t";
    await Reject<InvalidOperationException>(() => command.ExecuteScalarAsync()); command.Transaction = tx;
    using var stop = new CancellationTokenSource(); stop.Cancel(); await Reject<OperationCanceledException>(() => command.ExecuteScalarAsync(stop.Token)); tx.Rollback(); command.Transaction = null;
    command.CommandType = CommandType.StoredProcedure; await Reject<NotSupportedException>(() => command.ExecuteNonQueryAsync()); command.CommandType = CommandType.Text;
    await Reject<NotSupportedException>(() => Task.Run(() => connection.BeginTransaction(IsolationLevel.ReadCommitted)));
    using (var reader = command.ExecuteReader(CommandBehavior.SchemaOnly)) { Assert(reader.FieldCount == 1 && !reader.Read(), "schema only"); await Reject<InvalidOperationException>(() => Task.Run(() => reader.GetValue(0))); }
    using (var reader = command.ExecuteReader(CommandBehavior.CloseConnection)) { Assert(reader.Read(), "close connection read"); }
    Assert(connection.State == ConnectionState.Closed, "close connection behavior"); await connection.OpenAsync();
});
await Test("derived group/JSON caches invalidate on writes, caller BLOB isolation", async () =>
{
    string path = Path.Combine(folder, "cache.pdb"); await using var db = new SqlDatabase(path, new() { Storage = new() { Durability = Durability.Buffered } });
    await db.ExecuteAsync("CREATE TABLE d(id INTEGER PRIMARY KEY,j JSON,k INTEGER,b BLOB); INSERT INTO d VALUES(1,'{\"a\":1}',2,X'0102'),(2,'{\"a\":2}',2,X'0304')");
    Assert((long)db.Query("SELECT COUNT(*) FROM d WHERE json_extract(j,'$.a')=1").Rows[0][0]! == 1, "cached extract");
    Assert((long)db.Query("SELECT k,COUNT(*) FROM d GROUP BY k ORDER BY k").Rows[0][1]! == 2, "group metadata");
    await db.ExecuteAsync("UPDATE d SET j='{\"a\":3}',k=3 WHERE id=1");
    Assert((long)db.Query("SELECT COUNT(*) FROM d WHERE json_extract(j,'$.a')=1").Rows[0][0]! == 0 && db.Query("SELECT k,COUNT(*) FROM d GROUP BY k ORDER BY k").Rows.Count == 2, "invalidate");
    ((byte[])db.Query("SELECT b FROM d WHERE id=1").Rows[0][0]!)[0] = 99;
    Assert(((byte[])db.Query("SELECT b FROM d WHERE id=1").Rows[0][0]!)[0] == 1, "blob copies");
});
await Test("empty typed schema, bulk ADO transaction, persistence and numeric normalization", async () =>
{
    using var command = connection.CreateCommand(); command.CommandText = "SELECT id,n,v,amount FROM t WHERE id=9999";
    using (var reader = command.ExecuteReader(CommandBehavior.SchemaOnly)) Assert(reader.GetFieldType(0) == typeof(long) && reader.GetFieldType(1) == typeof(string) && reader.GetFieldType(2) == typeof(byte[]) && reader.GetFieldType(3) == typeof(double) && !reader.Read(), "empty typed schema");
    using (var tx = connection.BeginTransaction())
    {
        command.Transaction = tx; command.CommandText = "INSERT INTO t VALUES(@id,@n,NULL,1,NULL)";
        var id = command.Parameters.AddWithValue("id", 100L); var name = command.Parameters.AddWithValue("n", "bulk"); command.Prepare();
        for (int i = 100; i < 1100; i++) { id.Value = i; name.Value = "bulk" + i; command.ExecuteNonQuery(); }
        await tx.CommitAsync();
    }
    command.Transaction = null; command.Parameters.Clear(); command.CommandText = "SELECT COUNT(*) FROM t"; Assert((long)command.ExecuteScalar()! == 1002, "bulk committed");
    connection.Close(); connection.Open(); Assert((long)command.ExecuteScalar()! == 1002, "ADO reopen");
});
await Test("derived integer predicates and JSONB scalar/composite fast paths", async () =>
{
    await using var db = new SqlDatabase(Path.Combine(folder, "derived.pdb"), new() { Storage = new() { Durability = Durability.Buffered } });
    await db.ExecuteAsync("CREATE TABLE d(id INTEGER PRIMARY KEY,j JSON); INSERT INTO d VALUES(1,'{\"a\":4,\"v\":[1,2],\"s\":\"中文\"}'),(2,'{\"a\":null}'),(3,'{}'),(4,'{\"a\":7}')");
    var p = new Dictionary<string, object?> { ["mod"] = 2L, ["value"] = 0L };
    Assert((long)db.Query("SELECT COUNT(*) FROM d x WHERE json_extract(x.j,'$.a')%@mod=@value", p).Rows[0][0]! == 1, "derived parameter predicate");
    p["value"] = 1L; Assert((long)db.Query("SELECT COUNT(*) FROM d x WHERE json_extract(x.j,'$.a')%@mod=@value", p).Rows[0][0]! == 1, "no cached final result");
    Assert((long)db.Query("SELECT COUNT(*) FROM d WHERE json_extract(j,'$.a') IS NULL").Rows[0][0]! == 2, "missing and null");
    Assert((long)db.Query("SELECT COUNT(*) FROM d WHERE json_extract(j,'$.a')>3 AND json_extract(j,'$.a')<6").Rows[0][0]! == 1, "derived conjunction");
    var result = db.Query("SELECT json_extract(jsonb(j),'$.v[#-1]'),json_extract(jsonb(j),'$.v'),json_extract(jsonb(j),'$.s'),json_extract(jsonb(j),'$.none') FROM d WHERE id=1");
    Assert((long)result.Rows[0][0]! == 2 && (string)result.Rows[0][1]! == "[1,2]" && (string)result.Rows[0][2]! == "中文" && result.Rows[0][3] is null && result.Plan == "PrimaryScalarSeek", "JSONB fast projections");
    byte[] binary = (byte[])db.Query("SELECT jsonb(j) FROM d WHERE id=1").Rows[0][0]!; binary[0] = 0;
    Assert(((byte[])db.Query("SELECT jsonb(j) FROM d WHERE id=1").Rows[0][0]!)[0] == (byte)'P', "binary memo never exposed");
    await db.ExecuteAsync("UPDATE d SET j='{\"a\":9}' WHERE id=1");
    Assert((long)db.Query("SELECT COUNT(*) FROM d WHERE json_extract(j,'$.a')>6").Rows[0][0]! == 2, "derived invalidation");
});
await Test("page-backed cached BLOBs never leak and retain comparison/constraint semantics", async () =>
{
    await using var db = new SqlDatabase(Path.Combine(folder,"blobs.pdb"), new() { Storage = new() { Durability = Durability.Buffered } });
    await db.ExecuteAsync("CREATE TABLE t(id INTEGER PRIMARY KEY,k INTEGER,b BLOB UNIQUE,j JSONB); CREATE INDEX ix ON t(k); INSERT INTO t VALUES(1,1,X'0102',jsonb('{\"a\":3}')),(2,2,X'0304',jsonb('{\"a\":4}'))");
    var range = db.Query("SELECT b FROM t WHERE k BETWEEN 0 AND 3 ORDER BY k");
    ((byte[])range.Rows[0][0]!)[0]=99;
    Assert(((byte[])db.Query("SELECT b FROM t WHERE k=1").Rows[0][0]!)[0]==1,"range returns caller owned BLOB");
    Assert((long)db.Query("SELECT LENGTH(b) FROM t WHERE id=1").Rows[0][0]! == 2,"cached length");
    Assert(db.Query("SELECT id FROM t WHERE b=X'0102'").Rows.Count==1,"BLOB predicate");
    Assert(((byte[])db.Query("SELECT MIN(b) FROM t").Rows[0][0]!)[0]==1,"BLOB min");
    Assert(db.Query("SELECT b,COUNT(*) FROM t GROUP BY b ORDER BY b").Rows.All(r=>r[0] is byte[]),"BLOB grouping materializes");
    Assert((long)db.Query("SELECT json_extract(j,'$.a') FROM t WHERE id=1").Rows[0][0]! == 3,"page backed JSONB");
    await db.CompactAsync(); Assert(((byte[])db.Query("SELECT b FROM t WHERE k=1").Rows[0][0]!)[0]==1,"view survives compaction");
    await db.ExecuteAsync("UPDATE t SET k=3 WHERE id=1");
    await Reject<SqlException>(async()=>await db.ExecuteAsync("INSERT INTO t VALUES(3,3,X'0102',NULL)"));
    Assert(((byte[])db.Query("SELECT b FROM t WHERE k=3").Rows[0][0]!)[0]==1,"cached BLOB update");
});
Console.WriteLine($"{passed} ADO.NET/cache test groups passed: {folder}");
