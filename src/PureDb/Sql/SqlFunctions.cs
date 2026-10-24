using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace PureDb;
/// <summary>供 SQL 调用的可变参数 C# 标量函数。</summary>
/// <param name="arguments">当前行的 SQL 标量参数；NULL 对应 null，BLOB 为可独立修改的副本。</param>
/// <returns>SQL 标量结果；bool 自动转换为整数 0 或 1。</returns>
public delegate object? SqlScalarFunction(params object?[] arguments);
/// <remarks>本部分提供连接级用户函数、委托绑定及注册变更后的缓存失效。</remarks>
public sealed partial class SqlDatabase
{
    private ConcurrentDictionary<(string Name, int Arity), SqlFunctionDefinition>? _functions;
    /// <summary>注册无参数函数；同名、同参数数目的注册会替换原函数。</summary>
    /// <typeparam name="TResult">可转换为 SQL 标量的返回类型。</typeparam>
    /// <param name="name">当前连接中不区分大小写的 SQL 函数名。</param>
    /// <param name="function">执行计算的 C# 委托。</param>
    /// <param name="isDeterministic">相同参数是否始终返回相同结果；默认不作此保证。</param>
    public void CreateFunction<TResult>(string name, Func<TResult> function, bool isDeterministic = false)
    {
        ArgumentNullException.ThrowIfNull(function);
        RegisterFunction(name, new SqlFunctionDefinition(0, typeof(TResult), isDeterministic, _ => function(), _ => _ => SqlFunctionDefinition.Result(function())));
    }

    /// <summary>注册单参数函数；常用逐行调用路径不创建临时参数数组，也不使用反射调用。</summary>
    /// <typeparam name="T">输入参数的 C# 标量类型。</typeparam>
    /// <typeparam name="TResult">返回结果的 C# 标量类型。</typeparam>
    /// <param name="name">当前连接中不区分大小写的 SQL 函数名。</param>
    /// <param name="function">接收列值或表达式值的 C# 委托。</param>
    /// <param name="isDeterministic">相同参数是否始终返回相同结果。</param>
    public void CreateFunction<T, TResult>(string name, Func<T, TResult> function, bool isDeterministic = false)
    {
        ArgumentNullException.ThrowIfNull(function);
        RegisterFunction(name, new SqlFunctionDefinition(1, typeof(TResult), isDeterministic, a => function(SqlFunctionDefinition.Argument<T>(a[0])), a => row => SqlFunctionDefinition.Result(function(SqlFunctionDefinition.Argument<T>(a[0](row))))));
    }

    /// <summary>注册双参数 C# 标量函数。</summary>
    /// <typeparam name="T1">第一个输入参数的类型。</typeparam>
    /// <typeparam name="T2">第二个输入参数的类型。</typeparam>
    /// <typeparam name="TResult">返回结果的类型。</typeparam>
    /// <param name="name">当前连接中不区分大小写的 SQL 函数名。</param>
    /// <param name="function">接收两个 SQL 标量的委托。</param>
    /// <param name="isDeterministic">是否为确定性函数。</param>
    public void CreateFunction<T1, T2, TResult>(string name, Func<T1, T2, TResult> function, bool isDeterministic = false)
    {
        ArgumentNullException.ThrowIfNull(function);
        RegisterFunction(name, new SqlFunctionDefinition(2, typeof(TResult), isDeterministic, a => function(SqlFunctionDefinition.Argument<T1>(a[0]), SqlFunctionDefinition.Argument<T2>(a[1])), a => row => SqlFunctionDefinition.Result(function(SqlFunctionDefinition.Argument<T1>(a[0](row)), SqlFunctionDefinition.Argument<T2>(a[1](row))))));
    }

    /// <summary>按参数数量注册通用标量函数；参数数量 -1 表示可变参数。</summary>
    /// <param name="name">当前连接中不区分大小写的 SQL 函数名。</param>
    /// <param name="parameterCount">固定参数数量，或者表示可变参数的 -1。</param>
    /// <param name="function">接收参数数组的 C# 委托。</param>
    /// <param name="isDeterministic">是否为确定性函数。</param>
    public void CreateFunction(string name, int parameterCount, SqlScalarFunction function, bool isDeterministic = false)
    {
        ArgumentNullException.ThrowIfNull(function);
        if (parameterCount is < -1 or > 127)
            throw new ArgumentOutOfRangeException(nameof(parameterCount));
        RegisterFunction(name, new SqlFunctionDefinition(parameterCount, typeof(object), isDeterministic, a => function(a), null));
    }

    /// <summary>删除本连接中的指定函数重载，不影响其他连接。</summary>
    /// <param name="name">函数名。</param>
    /// <param name="parameterCount">重载的参数数量；-1 表示可变参数重载。</param>
    /// <returns>存在并删除时为 true，否则为 false。</returns>
    public bool RemoveFunction(string name, int parameterCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        bool removed = _functions?.TryRemove((name.ToUpperInvariant(), parameterCount), out _) ?? false;
        if (removed)
            ClearFunctionPlans();
        return removed;
    }

    /// <summary>在连接中保存函数定义，并使可能捕获旧委托或旧类型的计划失效。</summary>
    /// <param name="name">要解析、规范化或查找的标识符。</param>
    /// <param name="definition">当前连接要保存的用户函数定义。</param>
    private void RegisterFunction(string name, SqlFunctionDefinition definition)
    {
        ValidateFunctionName(name, definition.Arity);
        var functions = LazyInitializer.EnsureInitialized(ref _functions);
        functions[(name.ToUpperInvariant(), definition.Arity)] = definition;
        ClearFunctionPlans();
    }

    /// <summary>校验用户函数名称、参数数量和内置函数名称冲突。</summary>
    /// <param name="name">要解析、规范化或查找的标识符。</param>
    /// <param name="arity">参数数量；-1 表示可变参数重载。</param>
    internal static void ValidateFunctionName(string name, int arity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (arity is < -1 or > 127)
            throw new ArgumentOutOfRangeException(nameof(arity));
        if (name.Length > 255 || !char.IsLetter(name[0]) && name[0] != '_' || name.Any(c => !char.IsLetterOrDigit(c) && c != '_'))
            throw new ArgumentException("函数名必须是有效的 SQL 标识符。", nameof(name));
        string key = name.ToUpperInvariant();
        if (SqlBinding.BuiltinFunction(key))
            throw new ArgumentException("不能替换内置 SQL 函数。", nameof(name));
    }

    /// <summary>清除可能持有旧用户函数委托或结果类型的缓存。</summary>
    private void ClearFunctionPlans()
    {
        // 已开始的语句保留其绑定的委托；后续语句重新绑定，避免替换函数后继续执行旧委托。
        _scalarPlans.Clear();
        if (_dmlPlans.IsValueCreated)
            _dmlPlans.Value.Clear();
        lock (_cacheGate)
            _descriptions.Clear();
    }

    /// <summary>按名称和参数数量查找函数；固定参数重载优先于可变参数重载。</summary>
    /// <param name="name">要解析、规范化或查找的标识符。</param>
    /// <param name="arity">参数数量；-1 表示可变参数重载。</param>
    /// <param name="function">成功时返回本次绑定使用的用户函数定义。</param>
    /// <returns>匹配、校验或操作成功时为 true，否则为 false；具体条件见方法说明。</returns>
    internal bool TryFunction(string name, int arity, out SqlFunctionDefinition function)
    {
        // 未注册函数的连接只做一次空引用判断，保留原有查询和打开路径的低开销。
        var functions = Volatile.Read(ref _functions);
        function = null!;
        return functions is not null && (functions.TryGetValue((name, arity), out function!) ||
            functions.TryGetValue((name, -1), out function!));
    }
    /// <summary>判断表达式或查询是否引用当前连接注册的用户函数。</summary>
    /// <param name="e">要递归检查是否引用用户函数的表达式。</param>
    /// <returns>匹配、校验或操作成功时为 true，否则为 false；具体条件见方法说明。</returns>
    internal bool UsesFunction(Expr? e) => Volatile.Read(ref _functions) is not null && e is not null && (TryFunction(e.Op, e.Args.Length, out _) || e.Args.Any(UsesFunction));
    /// <summary>判断表达式或查询是否引用当前连接注册的用户函数。</summary>
    /// <param name="q">要检查的 SELECT 查询结构。</param>
    /// <returns>匹配、校验或操作成功时为 true，否则为 false；具体条件见方法说明。</returns>
    internal bool UsesFunction(Select q) => Volatile.Read(ref _functions) is not null &&
        (q.Columns.Any(p => UsesFunction(p.Expr)) || UsesFunction(q.Where) || q.Joins.Any(j => UsesFunction(j.On)) ||
        q.GroupBy.Any(UsesFunction) || UsesFunction(q.Having) || q.Order.Any(o => UsesFunction(o.Expr)) ||
        UsesFunction(q.Limit) || UsesFunction(q.Offset));
}

/// <summary>连接内的用户函数描述及直接委托调用器；输入输出 BLOB 均隔离所有权。</summary>
/// <param name="arity">固定参数数量；-1 表示可变参数。</param>
/// <param name="resultType">用于 ADO.NET 元数据的 C# 返回类型。</param>
/// <param name="deterministic">是否允许确定性表达式相关优化。</param>
/// <param name="invoke">通用参数数组调用入口。</param>
/// <param name="bind">可选的专用绑定器，用于避免逐行参数数组分配。</param>
internal sealed class SqlFunctionDefinition(int arity, Type resultType, bool deterministic, Func<object?[], object?> invoke, Func<Func<object?[], object?>[], Func<object?[], object?>>? bind)
{
    private static readonly object SqlTrue = 1L;
    private static readonly object SqlFalse = 0L;
    /// <summary>固定参数数量；-1 表示可变参数重载。</summary>
    internal int Arity => arity;
    /// <summary>是否由注册方保证相同参数始终返回相同结果。</summary>
    internal bool Deterministic => deterministic;

    /// <summary>将 C# 返回类型映射为 ADO.NET 可见的 SQL 标量 CLR 类型。</summary>
    internal Type ResultType
    {
        get
        {
            Type type = Nullable.GetUnderlyingType(resultType) ?? resultType;
            if (type == typeof(bool) || type == typeof(sbyte) || type == typeof(byte) || type == typeof(short) || type == typeof(ushort) || type == typeof(int) || type == typeof(uint) || type == typeof(long))
                return typeof(long);
            if (type == typeof(float) || type == typeof(decimal) || type == typeof(double))
                return typeof(double);
            return type;
        }
    }

    /// <summary>调用通用参数入口，隔离 BLOB 所有权并将用户异常包装为 SqlException。</summary>
    /// <param name="values">已经计算的 SQL 参数值；交给用户前会复制可变 BLOB。</param>
    /// <returns>规范化后的函数结果；用户委托返回 null 时为 SQL NULL。</returns>
    internal object? Invoke(object?[] values)
    {
        try
        {
            var inputs = new object?[values.Length];
            for (int i = 0; i < inputs.Length; i++)
                inputs[i] = Input(values[i]);
            return Result(invoke(inputs));
        }
        catch (SqlException)
        {
            throw;
        }
        catch (Exception error)
        {
            throw new SqlException("用户函数执行失败：" + error.Message, error);
        }
    }

    /// <summary>绑定逐行调用委托；专用泛型入口不为每行创建临时参数数组。</summary>
    /// <param name="arguments">按函数参数顺序排列的逐行求值委托。</param>
    /// <returns>可复用的逐行函数调用委托。</returns>
    internal Func<object?[], object?> Bind(Func<object?[], object?>[] arguments)
    {
        var call = bind is null ? (Func<object?[], object?>)(row =>
        {
            var values = new object?[arguments.Length];
            for (int i = 0; i < values.Length; i++)
                values[i] = Input(arguments[i](row));
            return Result(invoke(values));
        }) : bind(arguments);
        // 只在绑定时创建闭包；逐行执行不再创建用于异常包装的临时委托。
        return row =>
        {
            try
            {
                return call(row);
            }
            catch (SqlException)
            {
                throw;
            }
            catch (Exception error)
            {
                throw new SqlException("用户函数执行失败：" + error.Message, error);
            }
        };
    }

    /// <summary>将内部 JSON 或 BLOB 视图转换为用户可安全读取和修改的独立输入值。</summary>
    /// <param name="value">内部 SQL 标量；缓存 BLOB 和字节数组转为独立副本。</param>
    /// <returns>用户可安全持有的输入标量；SQL NULL 保持为 null。</returns>
    private static object? Input(object? value) => value is byte[] bytes ? bytes.ToArray() : SqlJsonFunctions.Unwrap(value);
    /// <summary>规范化用户函数返回值，并复制可变 BLOB 以隔离委托持有的数组。</summary>
    /// <param name="value">用户委托返回的 CLR 标量；BLOB 在返回给 SQL 执行器前复制。</param>
    /// <returns>符合 SQL 存储类型且不共享外部可变数组的结果。</returns>
    internal static object? Result(object? value)
    {
        var normalized = SqlValueComparer.Normalize(value);
        return normalized is byte[] bytes ? bytes.ToArray() : normalized;
    }

    /// <summary>直接规范化强类型返回值；布尔筛选复用 SQL 的 0/1 对象，避免每行先后装箱 bool 和 long。</summary>
    /// <typeparam name="T">用户委托的实际返回类型。</typeparam>
    /// <param name="value">用户委托刚计算出的强类型结果。</param>
    /// <returns>SQL 标量结果；可空布尔的 null 保留为 SQL NULL。</returns>
    internal static object? Result<T>(T value)
    {
        // typeof 分支在泛型值类型实例化时可消除；As 的类型一致性由前置比较保证。
        if (typeof(T) == typeof(bool))
            return Unsafe.As<T, bool>(ref value) ? SqlTrue : SqlFalse;
        if (typeof(T) == typeof(bool?))
        {
            bool? boolean = Unsafe.As<T, bool?>(ref value);
            return boolean.HasValue ? boolean.Value ? SqlTrue : SqlFalse : null;
        }
        return Result((object?)value);
    }

    /// <summary>将 SQL 标量转换为注册参数的 C# 类型；NULL 只能传给可空或引用类型。</summary>
    /// <param name="value">需要转换为注册 C# 参数类型的 SQL 标量。</param>
    /// <typeparam name="T">回调返回值或请求转换的结果类型。</typeparam>
    /// <returns>回调或类型转换得到的结果。</returns>
    internal static T Argument<T>(object? value)
    {
        value = Input(value);
        if (value is null)
        {
            if (!typeof(T).IsValueType || Nullable.GetUnderlyingType(typeof(T)) is not null)
                return default!;
            throw new SqlException("SQL NULL 不能传给不可空的用户函数参数。");
        }

        if (value is T typed)
            return typed;
        Type type = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        if (type == typeof(bool))
            return (T)(object)(SqlValueComparer.Truth(value) ?? false);
        if (value is long or double && type.IsPrimitive || type == typeof(decimal) || type == typeof(string))
            return (T)Convert.ChangeType(value, type, CultureInfo.InvariantCulture);
        throw new SqlException($"用户函数参数类型不匹配：{typeof(T).Name}。");
    }
}
