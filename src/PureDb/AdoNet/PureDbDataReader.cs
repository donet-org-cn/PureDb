using System.Collections;
using System.Data;
using System.Data.Common;
using System.Globalization;

namespace PureDb.AdoNet;

/// <summary>A reader over materialized results; no database lock is held while enumerating.</summary>
public sealed class PureDbDataReader : DbDataReader
{
    private readonly IReadOnlyList<SqlResult> _results;
    private readonly CommandBehavior _behavior;
    private readonly PureDbConnection _connection;
    private int _result, _row = -1;
    private bool _closed, _positioned;
    internal PureDbDataReader(IReadOnlyList<SqlResult> results, CommandBehavior behavior, PureDbConnection connection)
    { _results = results; _behavior = behavior; _connection = connection; }
    private SqlResult Current { get { Check(); return _results[_result]; } }
    private void Check() { if (_closed) throw new InvalidOperationException("Reader is closed"); }
    public override int FieldCount => Current.Columns.Count;
    public override bool HasRows => (_behavior & CommandBehavior.SchemaOnly) == 0 && Current.Rows.Count > 0;
    public override bool IsClosed => _closed;
    public override int Depth => 0;
    public override int RecordsAffected => -1;
    public override object this[int ordinal] => GetValue(ordinal);
    public override object this[string name] => GetValue(GetOrdinal(name));
    public override bool Read()
    {
        Check();
        int count = (_behavior & CommandBehavior.SchemaOnly) != 0 ? 0 : Current.Rows.Count;
        if ((_behavior & CommandBehavior.SingleRow) != 0) count = Math.Min(1, count);
        _positioned = false;
        if (_row >= count) return false;
        return _positioned = ++_row < count;
    }
    public override bool NextResult()
    {
        Check(); _positioned = false;
        if ((_behavior & (CommandBehavior.SingleResult | CommandBehavior.SingleRow)) != 0 || _result + 1 >= _results.Count) return false;
        _result++; _row = -1; return true;
    }
    public override Task<bool> ReadAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.FromResult(Read()); }
    public override Task<bool> NextResultAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.FromResult(NextResult()); }
    public override void Close() { if (_closed) return; _closed = true; if ((_behavior & CommandBehavior.CloseConnection) != 0) _connection.Close(); }
    protected override void Dispose(bool disposing) { if (disposing) Close(); base.Dispose(disposing); }
    public override string GetName(int ordinal) => Current.Columns[ordinal];
    public override int GetOrdinal(string name)
    {
        for (int i = 0; i < FieldCount; i++) if (GetName(i) == name) return i;
        for (int i = 0; i < FieldCount; i++) if (GetName(i).Equals(name, StringComparison.OrdinalIgnoreCase)) return i;
        throw new IndexOutOfRangeException(name);
    }
    public override object GetValue(int ordinal)
    {
        Check(); if (!_positioned) throw new InvalidOperationException("Read must position the reader on a row");
        return Current.Rows[_row][ordinal] ?? DBNull.Value;
    }
    public override int GetValues(object[] values) { int n = Math.Min(values.Length, FieldCount); for (int i = 0; i < n; i++) values[i] = GetValue(i); return n; }
    public override bool IsDBNull(int ordinal) => GetValue(ordinal) is DBNull;
    public override Type GetFieldType(int ordinal)
    {
        if (ordinal < 0 || ordinal >= FieldCount) throw new IndexOutOfRangeException();
        if (Current.FieldTypes.Count == FieldCount && Current.FieldTypes[ordinal] != typeof(object)) return Current.FieldTypes[ordinal];
        foreach (var row in Current.Rows) if (row[ordinal] is { } value) return value.GetType();
        return typeof(object);
    }
    public override string GetDataTypeName(int ordinal) => GetFieldType(ordinal) switch { Type t when t == typeof(long) => "INTEGER", Type t when t == typeof(double) => "REAL", Type t when t == typeof(string) => "TEXT", Type t when t == typeof(byte[]) => "BLOB", _ => "NULL" };
    private T ConvertValue<T>(int ordinal) => (T)Convert.ChangeType(GetValue(ordinal), typeof(T), CultureInfo.InvariantCulture);
    public override bool GetBoolean(int ordinal) => ConvertValue<bool>(ordinal);
    public override byte GetByte(int ordinal) => ConvertValue<byte>(ordinal);
    public override char GetChar(int ordinal) => ConvertValue<char>(ordinal);
    public override short GetInt16(int ordinal) => ConvertValue<short>(ordinal);
    public override int GetInt32(int ordinal) => ConvertValue<int>(ordinal);
    public override long GetInt64(int ordinal) => ConvertValue<long>(ordinal);
    public override float GetFloat(int ordinal) => ConvertValue<float>(ordinal);
    public override double GetDouble(int ordinal) => ConvertValue<double>(ordinal);
    public override decimal GetDecimal(int ordinal) => ConvertValue<decimal>(ordinal);
    public override string GetString(int ordinal) => (string)GetValue(ordinal);
    public override Guid GetGuid(int ordinal) => Guid.Parse(GetString(ordinal));
    public override DateTime GetDateTime(int ordinal) => DateTime.Parse(GetString(ordinal), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length)
    {
        var bytes = (byte[])GetValue(ordinal); if (buffer is null) return bytes.LongLength;
        ValidateCopy(dataOffset, buffer.Length, bufferOffset, length); int n = (int)Math.Min(length, Math.Max(0, bytes.LongLength - dataOffset));
        if (n > 0) bytes.AsSpan((int)dataOffset, n).CopyTo(buffer.AsSpan(bufferOffset)); return n;
    }
    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length)
    {
        string text = GetString(ordinal); if (buffer is null) return text.Length;
        ValidateCopy(dataOffset, buffer.Length, bufferOffset, length); int n = (int)Math.Min(length, Math.Max(0, (long)text.Length - dataOffset));
        if (n > 0) text.AsSpan((int)dataOffset, n).CopyTo(buffer.AsSpan(bufferOffset)); return n;
    }
    private static void ValidateCopy(long offset, int capacity, int start, int length)
    { if (offset < 0 || start < 0 || length < 0 || start > capacity || length > capacity - start) throw new ArgumentOutOfRangeException(); }
    public override DataTable GetSchemaTable()
    {
        var t = new DataTable("SchemaTable");
        t.Columns.Add(SchemaTableColumn.ColumnName, typeof(string)); t.Columns.Add(SchemaTableColumn.ColumnOrdinal, typeof(int));
        t.Columns.Add(SchemaTableColumn.ColumnSize, typeof(int)); t.Columns.Add(SchemaTableColumn.DataType, typeof(Type));
        t.Columns.Add(SchemaTableColumn.AllowDBNull, typeof(bool)); t.Columns.Add(SchemaTableColumn.IsUnique, typeof(bool));
        t.Columns.Add(SchemaTableColumn.IsKey, typeof(bool));
        for (int i = 0; i < FieldCount; i++) t.Rows.Add(GetName(i), i, -1, GetFieldType(i), true, false, false);
        return t;
    }
    public override IEnumerator GetEnumerator() => new DbEnumerator(this, false);
}
