namespace PureDb.AdoNet;
/// <summary>PureDb 的 ADO.NET 连接；支持参数化命令、乐观 Serializable 事务和连接级用户函数。</summary>
public partial class PureDbConnection
{
    // 注册信息属于连接实例，关闭再打开仍会应用；委托不会写入数据库文件。
    private readonly Dictionary<(string Name, int Arity), Action<SqlDatabase>> _functionRegistrations = [];
    /// <summary>注册无参数 C# 函数；可以在打开连接之前或之后调用。</summary>
    /// <typeparam name="TResult">可转换为 SQL 标量的返回类型。</typeparam>
    /// <param name="name">不区分大小写的 SQL 函数名。</param>
    /// <param name="function">执行函数计算的委托。</param>
    /// <param name="isDeterministic">相同参数是否始终返回相同结果。</param>
    public void CreateFunction<TResult>(string name, Func<TResult> function, bool isDeterministic = false) => RegisterFunction(name, 0, function, db => db.CreateFunction(name, function, isDeterministic));
    /// <summary>注册单参数 C# 函数，例如在 WHERE RELIGIBLE(User) 中筛选行。</summary>
    /// <typeparam name="T">输入参数的标量类型；使用可空类型接收 SQL NULL。</typeparam>
    /// <typeparam name="TResult">返回标量的类型；bool 转换为 SQL 的 0 或 1。</typeparam>
    /// <param name="name">不区分大小写的 SQL 函数名。</param>
    /// <param name="function">接收列值或表达式值的 C# 委托。</param>
    /// <param name="isDeterministic">相同参数是否始终返回相同结果。</param>
    public void CreateFunction<T, TResult>(string name, Func<T, TResult> function, bool isDeterministic = false) => RegisterFunction(name, 1, function, db => db.CreateFunction(name, function, isDeterministic));
    /// <summary>注册双参数 C# 函数；同名、同参数数量的注册会替换原函数。</summary>
    /// <typeparam name="T1">第一个输入参数的类型。</typeparam>
    /// <typeparam name="T2">第二个输入参数的类型。</typeparam>
    /// <typeparam name="TResult">返回值类型。</typeparam>
    /// <param name="name">不区分大小写的 SQL 函数名。</param>
    /// <param name="function">执行计算的 C# 委托。</param>
    /// <param name="isDeterministic">是否为确定性函数。</param>
    public void CreateFunction<T1, T2, TResult>(string name, Func<T1, T2, TResult> function, bool isDeterministic = false) => RegisterFunction(name, 2, function, db => db.CreateFunction(name, function, isDeterministic));
    /// <summary>注册固定参数数量或可变参数的 C# 标量函数。</summary>
    /// <param name="name">不区分大小写的 SQL 函数名。</param>
    /// <param name="parameterCount">参数数量；-1 表示可变参数。</param>
    /// <param name="function">接收参数数组的委托。</param>
    /// <param name="isDeterministic">是否为确定性函数。</param>
    public void CreateFunction(string name, int parameterCount, SqlScalarFunction function, bool isDeterministic = false) => RegisterFunction(name, parameterCount, function, db => db.CreateFunction(name, parameterCount, function, isDeterministic));
    /// <summary>移除当前连接的指定函数重载；关闭后重新打开也不会恢复该重载。</summary>
    /// <param name="name">函数名。</param>
    /// <param name="parameterCount">参数数量；-1 表示可变参数重载。</param>
    /// <returns>存在并移除注册时为 true。</returns>
    public bool RemoveFunction(string name, int parameterCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        bool removed = _functionRegistrations.Remove((name.ToUpperInvariant(), parameterCount));
        return (_database?.RemoveFunction(name, parameterCount) ?? false) || removed;
    }

    /// <summary>校验并保存连接级函数注册，在已打开引擎中立即应用。</summary>
    /// <param name="name">要解析、规范化或查找的标识符。</param>
    /// <param name="arity">参数数量；-1 表示可变参数重载。</param>
    /// <param name="function">用户 C# 委托，或成功查找后返回的函数定义。</param>
    /// <param name="register">将连接级注册应用到新建 SQL 引擎的回调。</param>
    private void RegisterFunction(string name, int arity, Delegate function, Action<SqlDatabase> register)
    {
        ArgumentNullException.ThrowIfNull(function);
        SqlDatabase.ValidateFunctionName(name, arity);
        if (_database is not null)
            register(_database);
        _functionRegistrations[(name.ToUpperInvariant(), arity)] = register;
    }
}
