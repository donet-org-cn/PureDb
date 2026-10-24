using System.Collections;
using System.Data;
using System.Data.Common;
using System.Globalization;

namespace PureDb.AdoNet;
/// <summary>遍历已物化结果的读取器；枚举期间不持有数据库锁，SQL NULL 对外表示为 DBNull.Value。</summary>
public sealed class PureDbDataReader : DbDataReader
{
    private readonly IReadOnlyList<SqlResult> _results;
    private readonly CommandBehavior _behavior;
    private readonly PureDbConnection _connection;
    private int _result, _row = -1;
    private bool _closed, _positioned;
    /// <summary>关联物化结果、读取行为和所属连接，尚未定位到任何行。</summary>
    /// <param name="results">按语句顺序排列的已物化结果集。</param>
    /// <param name="behavior">ADO.NET 读取器的结果、行数及连接关闭行为。</param>
    /// <param name="connection">拥有当前对象的 ADO.NET 连接。</param>
    internal PureDbDataReader(IReadOnlyList<SqlResult> results, CommandBehavior behavior, PureDbConnection connection)
    {
        _results = results;
        _behavior = behavior;
        _connection = connection;
    }

    /// <summary>检查读取器状态后取得当前结果集。</summary>
    private SqlResult Current
    {
        get
        {
            Check();
            return _results[_result];
        }
    }

    /// <summary>检查读取器尚未关闭。</summary>
    private void Check()
    {
        if (_closed)
            throw new InvalidOperationException("Reader is closed");
    }

    /// <summary>当前结果集的列数。</summary>
    public override int FieldCount => Current.Columns.Count;
    /// <summary>当前结果集是否有行；SchemaOnly 模式始终为 false。</summary>
    public override bool HasRows => (_behavior & CommandBehavior.SchemaOnly) == 0 && Current.Rows.Count > 0;
    /// <summary>读取器是否已经关闭。</summary>
    public override bool IsClosed => _closed;
    /// <summary>结果嵌套深度；当前始终为 0。</summary>
    public override int Depth => 0;
    /// <summary>非查询影响行数；SELECT 读取器返回 -1。</summary>
    public override int RecordsAffected => -1;

    /// <summary>按列序号或列名获取当前行的值；SQL NULL 返回 DBNull.Value。</summary>
    /// <param name="ordinal">从 0 开始的列序号。</param>
    public override object this[int ordinal] => GetValue(ordinal);
    /// <summary>按列序号或列名获取当前行的值；SQL NULL 返回 DBNull.Value。</summary>
    /// <param name="name">要查找的名称；大小写规则由当前接口说明。</param>
    public override object this[string name] => GetValue(GetOrdinal(name));
    /// <summary>前进到当前结果集的下一行，并报告是否成功定位。</summary>
    /// <returns>条件满足或成功定位时为 true，否则为 false。</returns>
    public override bool Read()
    {
        Check();
        int count = (_behavior & CommandBehavior.SchemaOnly) != 0 ? 0 : Current.Rows.Count;
        if ((_behavior & CommandBehavior.SingleRow) != 0)
            count = Math.Min(1, count);
        _positioned = false;
        if (_row >= count)
            return false;
        return _positioned = ++_row < count;
    }

    /// <summary>切换到下一个结果集；SingleResult 或 SingleRow 模式不切换。</summary>
    /// <returns>条件满足或成功定位时为 true，否则为 false。</returns>
    public override bool NextResult()
    {
        Check();
        _positioned = false;
        if ((_behavior & (CommandBehavior.SingleResult | CommandBehavior.SingleRow)) != 0 || _result + 1 >= _results.Count)
            return false;
        _result++;
        _row = -1;
        return true;
    }

    /// <summary>检查取消后前进到下一行；结果已经物化，不发起额外文件读取。</summary>
    /// <param name="token">取消请求；执行中的同步计算不能被抢占。</param>
    /// <returns>条件满足或成功定位时为 true，否则为 false。</returns>
    public override Task<bool> ReadAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return Task.FromResult(Read());
    }

    /// <summary>检查取消后切换到下一个已物化结果集。</summary>
    /// <param name="token">取消请求；执行中的同步计算不能被抢占。</param>
    /// <returns>条件满足或成功定位时为 true，否则为 false。</returns>
    public override Task<bool> NextResultAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return Task.FromResult(NextResult());
    }

    /// <summary>关闭读取器；指定 CloseConnection 时同时关闭所属连接。</summary>
    public override void Close()
    {
        if (_closed)
            return;
        _closed = true;
        if ((_behavior & CommandBehavior.CloseConnection) != 0)
            _connection.Close();
    }

    /// <summary>释放读取器并执行 CloseConnection 选项要求的连接关闭。</summary>
    /// <param name="disposing">是否由显式资源释放触发；为 true 时释放托管连接及事务资源。</param>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
            Close();
        base.Dispose(disposing);
    }

    /// <summary>获取指定序号的结果列名。</summary>
    /// <param name="ordinal">从 0 开始的列序号。</param>
    /// <returns>指定列的名称、值或类型信息，具体形式由返回类型确定。</returns>
    public override string GetName(int ordinal) => Current.Columns[ordinal];
    /// <summary>查找列名对应的零基序号；先精确匹配，再忽略大小写匹配。</summary>
    /// <param name="name">要查找的名称；大小写规则由当前接口说明。</param>
    /// <returns>匹配列的零基序号；找不到时抛出异常。</returns>
    /// <exception cref = "System.IndexOutOfRangeException">结果中不存在指定列。</exception>
    public override int GetOrdinal(string name)
    {
        for (int i = 0; i < FieldCount; i++)
            if (GetName(i) == name)
                return i;
        for (int i = 0; i < FieldCount; i++)
            if (GetName(i).Equals(name, StringComparison.OrdinalIgnoreCase))
                return i;
        throw new IndexOutOfRangeException(name);
    }

    /// <summary>获取当前行的标量值；SQL NULL 返回 DBNull.Value。</summary>
    /// <param name="ordinal">从 0 开始的列序号。</param>
    /// <returns>指定列的名称、值或类型信息，具体形式由返回类型确定。</returns>
    public override object GetValue(int ordinal)
    {
        Check();
        if (!_positioned)
            throw new InvalidOperationException("Read must position the reader on a row");
        return Current.Rows[_row][ordinal] ?? DBNull.Value;
    }

    /// <summary>将当前行的值复制到调用方数组，最多复制数组容量或列数中的较小值。</summary>
    /// <param name="values">接收当前结果行列值的目标数组。</param>
    /// <returns>实际复制到数组中的列数。</returns>
    public override int GetValues(object[] values)
    {
        int n = Math.Min(values.Length, FieldCount);
        for (int i = 0; i < n; i++)
            values[i] = GetValue(i);
        return n;
    }

    /// <summary>判断当前行指定列是否为 SQL NULL。</summary>
    /// <param name="ordinal">从 0 开始的列序号。</param>
    /// <returns>条件满足或成功定位时为 true，否则为 false。</returns>
    public override bool IsDBNull(int ordinal) => GetValue(ordinal) is DBNull;
    /// <summary>获取列的 CLR 类型；优先使用模式信息，再检查实际值，未知类型返回 object。</summary>
    /// <param name="ordinal">从 0 开始的列序号。</param>
    /// <returns>指定列的名称、值或类型信息，具体形式由返回类型确定。</returns>
    [return: System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicProperties | System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicFields)]
    public override Type GetFieldType(int ordinal)
    {
        if (ordinal < 0 || ordinal >= FieldCount)
            throw new IndexOutOfRangeException();
        if (Current.FieldTypes.Count == FieldCount && Current.FieldTypes[ordinal] != typeof(object))
            return PreservedFieldType(Current.FieldTypes[ordinal]);
        foreach (var row in Current.Rows)
            if (row[ordinal] is { } value)
                return PreservedFieldType(value.GetType());
        return typeof(object);
    }

    /// <summary>将 SQL 支持的 CLR 类型映射为编译器可证明保留元数据的常量，避免 AOT 反射推断。</summary>
    /// <param name="type">结果模式或实际 SQL 标量的 CLR 类型。</param>
    /// <returns>已保留公共字段及属性的 SQL 标量类型；未知类型返回 object。</returns>
    [return: System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicProperties | System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicFields)]
    private static Type PreservedFieldType(Type type)
        => type == typeof(long) ? typeof(long) : type == typeof(double) ? typeof(double) : type == typeof(string) ? typeof(string) : type == typeof(byte[]) ? typeof(byte[]) : typeof(object);

    /// <summary>获取列对应的 SQL 存储类型名称，如 INTEGER、REAL、TEXT 或 BLOB。</summary>
    /// <param name="ordinal">从 0 开始的列序号。</param>
    /// <returns>指定列的名称、值或类型信息，具体形式由返回类型确定。</returns>
    public override string GetDataTypeName(int ordinal) => GetFieldType(ordinal) switch
    {
        Type t when t == typeof(long) => "INTEGER",
        Type t when t == typeof(double) => "REAL",
        Type t when t == typeof(string) => "TEXT",
        Type t when t == typeof(byte[]) => "BLOB",
        _ => "NULL"
    };
    /// <summary>使用固定文化区域将当前列值转换为请求的 CLR 标量类型。</summary>
    /// <param name="ordinal">从 0 开始的条目或列序号。</param>
    /// <typeparam name="T">回调返回值或请求转换的结果类型。</typeparam>
    /// <returns>回调或类型转换得到的结果。</returns>
    private T ConvertValue<T>(int ordinal) => (T)Convert.ChangeType(GetValue(ordinal), typeof(T), CultureInfo.InvariantCulture);
    /// <summary>读取当前行指定列并转换为布尔值；SQL NULL 不进行默认值替换。</summary>
    /// <param name="ordinal">从 0 开始的列序号。</param>
    /// <returns>条件满足或成功定位时为 true，否则为 false。</returns>
    public override bool GetBoolean(int ordinal) => ConvertValue<bool>(ordinal);
    /// <summary>读取当前行指定列并转换为无符号字节；SQL NULL 不进行默认值替换。</summary>
    /// <param name="ordinal">从 0 开始的列序号。</param>
    /// <returns>指定列的名称、值或类型信息，具体形式由返回类型确定。</returns>
    public override byte GetByte(int ordinal) => ConvertValue<byte>(ordinal);
    /// <summary>读取当前行指定列并转换为字符；SQL NULL 不进行默认值替换。</summary>
    /// <param name="ordinal">从 0 开始的列序号。</param>
    /// <returns>指定列的名称、值或类型信息，具体形式由返回类型确定。</returns>
    public override char GetChar(int ordinal) => ConvertValue<char>(ordinal);
    /// <summary>读取当前行指定列并转换为16 位整数；SQL NULL 不进行默认值替换。</summary>
    /// <param name="ordinal">从 0 开始的列序号。</param>
    /// <returns>指定列的名称、值或类型信息，具体形式由返回类型确定。</returns>
    public override short GetInt16(int ordinal) => ConvertValue<short>(ordinal);
    /// <summary>读取当前行指定列并转换为32 位整数；SQL NULL 不进行默认值替换。</summary>
    /// <param name="ordinal">从 0 开始的列序号。</param>
    /// <returns>指定列的名称、值或类型信息，具体形式由返回类型确定。</returns>
    public override int GetInt32(int ordinal) => ConvertValue<int>(ordinal);
    /// <summary>读取当前行指定列并转换为64 位整数；SQL NULL 不进行默认值替换。</summary>
    /// <param name="ordinal">从 0 开始的列序号。</param>
    /// <returns>指定列的名称、值或类型信息，具体形式由返回类型确定。</returns>
    public override long GetInt64(int ordinal) => ConvertValue<long>(ordinal);
    /// <summary>读取当前行指定列并转换为单精度浮点数；SQL NULL 不进行默认值替换。</summary>
    /// <param name="ordinal">从 0 开始的列序号。</param>
    /// <returns>指定列的名称、值或类型信息，具体形式由返回类型确定。</returns>
    public override float GetFloat(int ordinal) => ConvertValue<float>(ordinal);
    /// <summary>读取当前行指定列并转换为双精度浮点数；SQL NULL 不进行默认值替换。</summary>
    /// <param name="ordinal">从 0 开始的列序号。</param>
    /// <returns>指定列的名称、值或类型信息，具体形式由返回类型确定。</returns>
    public override double GetDouble(int ordinal) => ConvertValue<double>(ordinal);
    /// <summary>读取当前行指定列并转换为十进制数；SQL NULL 不进行默认值替换。</summary>
    /// <param name="ordinal">从 0 开始的列序号。</param>
    /// <returns>指定列的名称、值或类型信息，具体形式由返回类型确定。</returns>
    public override decimal GetDecimal(int ordinal) => ConvertValue<decimal>(ordinal);
    /// <summary>读取当前行指定列并转换为字符串；SQL NULL 不进行默认值替换。</summary>
    /// <param name="ordinal">从 0 开始的列序号。</param>
    /// <returns>指定列的名称、值或类型信息，具体形式由返回类型确定。</returns>
    public override string GetString(int ordinal) => (string)GetValue(ordinal);
    /// <summary>读取当前行指定列并转换为GUID；SQL NULL 不进行默认值替换。</summary>
    /// <param name="ordinal">从 0 开始的列序号。</param>
    /// <returns>指定列的名称、值或类型信息，具体形式由返回类型确定。</returns>
    public override Guid GetGuid(int ordinal) => Guid.Parse(GetString(ordinal));
    /// <summary>读取当前行指定列并转换为保留往返时间信息的 DateTime；SQL NULL 不进行默认值替换。</summary>
    /// <param name="ordinal">从 0 开始的列序号。</param>
    /// <returns>指定列的名称、值或类型信息，具体形式由返回类型确定。</returns>
    public override DateTime GetDateTime(int ordinal) => DateTime.Parse(GetString(ordinal), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    /// <summary>复制当前 BLOB 列的指定片段；buffer 为 null 时仅返回完整字节数。</summary>
    /// <param name="ordinal">从 0 开始的列序号。</param>
    /// <param name="dataOffset">源列中的零基读取偏移，BLOB 以字节计，文本以字符计。</param>
    /// <param name="buffer">接收片段的数组；为 null 时仅查询完整长度。</param>
    /// <param name="bufferOffset">目标数组中的零基写入偏移。</param>
    /// <param name="length">本次处理的字节长度或请求长度。</param>
    /// <returns>实际复制的字节数；buffer 为 null 时为完整 BLOB 长度。</returns>
    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length)
    {
        var bytes = (byte[])GetValue(ordinal);
        if (buffer is null)
            return bytes.LongLength;
        ValidateCopy(dataOffset, buffer.Length, bufferOffset, length);
        int n = (int)Math.Min(length, Math.Max(0, bytes.LongLength - dataOffset));
        if (n > 0)
            bytes.AsSpan((int)dataOffset, n).CopyTo(buffer.AsSpan(bufferOffset));
        return n;
    }

    /// <summary>复制当前文本列的指定片段；buffer 为 null 时仅返回完整字符数。</summary>
    /// <param name="ordinal">从 0 开始的列序号。</param>
    /// <param name="dataOffset">源列中的零基读取偏移，BLOB 以字节计，文本以字符计。</param>
    /// <param name="buffer">接收片段的数组；为 null 时仅查询完整长度。</param>
    /// <param name="bufferOffset">目标数组中的零基写入偏移。</param>
    /// <param name="length">本次处理的字节长度或请求长度。</param>
    /// <returns>实际复制的字符数；buffer 为 null 时为完整文本长度。</returns>
    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length)
    {
        string text = GetString(ordinal);
        if (buffer is null)
            return text.Length;
        ValidateCopy(dataOffset, buffer.Length, bufferOffset, length);
        int n = (int)Math.Min(length, Math.Max(0, (long)text.Length - dataOffset));
        if (n > 0)
            text.AsSpan((int)dataOffset, n).CopyTo(buffer.AsSpan(bufferOffset));
        return n;
    }

    /// <summary>校验片段复制的源偏移、目标偏移和请求长度。</summary>
    /// <param name="offset">文件或缓冲区中的字节偏移。</param>
    /// <param name="capacity">目标缓冲区容量。</param>
    /// <param name="start">目标缓冲区的零基起始偏移。</param>
    /// <param name="length">本次处理的字节长度或请求长度。</param>
    private static void ValidateCopy(long offset, int capacity, int start, int length)
    {
        if (offset < 0 || start < 0 || length < 0 || start > capacity || length > capacity - start)
            throw new ArgumentOutOfRangeException();
    }

    /// <summary>返回列名、序号、CLR 类型和保守的可空性等元数据；不推断唯一键标记。</summary>
    /// <returns>指定列的名称、值或类型信息，具体形式由返回类型确定。</returns>
    public override DataTable GetSchemaTable()
    {
        var t = new DataTable("SchemaTable");
        t.Columns.Add(SchemaTableColumn.ColumnName, typeof(string));
        t.Columns.Add(SchemaTableColumn.ColumnOrdinal, typeof(int));
        t.Columns.Add(SchemaTableColumn.ColumnSize, typeof(int));
        t.Columns.Add(SchemaTypeColumn());
        t.Columns.Add(SchemaTableColumn.AllowDBNull, typeof(bool));
        t.Columns.Add(SchemaTableColumn.IsUnique, typeof(bool));
        t.Columns.Add(SchemaTableColumn.IsKey, typeof(bool));
        for (int i = 0; i < FieldCount; i++)
            t.Rows.Add(GetName(i), i, -1, GetFieldType(i), true, false, false);
        return t;
    }

    /// <summary>创建存放 Type 对象的模式列，仅按对象存取，不通过反射读取 Type 的属性。</summary>
    /// <returns>保持 ADO.NET 约定为 System.Type 的 DataType 模式列。</returns>
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2111", Justification = "此列仅保存 Type 对象，不反射其公共属性，不调用 Type.TypeInitializer；AOT 模式测试验证模式表读取。")]
    private static DataColumn SchemaTypeColumn() => new(SchemaTableColumn.DataType, typeof(Type));

    /// <summary>创建按行遍历当前读取器的 DbEnumerator。</summary>
    /// <returns>按当前顺序遍历数据的枚举器。</returns>
    public override IEnumerator GetEnumerator() => new DbEnumerator(this, false);
}
