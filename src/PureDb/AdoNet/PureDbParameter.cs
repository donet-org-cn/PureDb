using System.Collections;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

namespace PureDb.AdoNet;
/// <summary>输入参数的名称、值和 ADO.NET 元数据；实际存储类型由传入标量决定。</summary>
public sealed class PureDbParameter : DbParameter
{
    /// <summary>创建输入参数，可同时指定参数名及值。</summary>
    public PureDbParameter()
    {
    }

    /// <summary>创建输入参数，可同时指定参数名及值。</summary>
    /// <param name="name">参数名称，可以带 @、:、$ 或 ? 前缀。</param>
    /// <param name="value">输入标量；null 或 DBNull.Value 表示 SQL NULL。</param>
    public PureDbParameter(string name, object? value)
    {
        ParameterName = name;
        Value = value;
    }

    /// <summary>参数的 ADO.NET 类型元数据；不据此隐式转换传入值。</summary>
    public override DbType DbType { get; set; } = DbType.Object;
    /// <summary>参数方向；执行命令时仅支持 Input。</summary>
    public override ParameterDirection Direction { get; set; } = ParameterDirection.Input;
    /// <summary>参数是否允许空值的元数据；不替代数据库列约束。</summary>
    public override bool IsNullable { get; set; } = true;

    /// <summary>参数名称；匹配时忽略 @、:、$、? 前缀。</summary>
    [AllowNull]
    public override string ParameterName { get; set; } = "";

    /// <summary>数据适配器映射的源列名称。</summary>
    [AllowNull]
    public override string SourceColumn { get; set; } = "";
    /// <summary>参数值；null 或 DBNull.Value 表示 SQL NULL。</summary>
    public override object? Value
    {
        get; set;
    }
    /// <summary>数据适配器是否使用该参数表示源列的空值状态。</summary>
    public override bool SourceColumnNullMapping
    {
        get; set;
    }
    /// <summary>参数长度元数据；不会截断传入字符串或 BLOB。</summary>
    public override int Size
    {
        get; set;
    }

    /// <summary>将 DbType 元数据重置为 Object。</summary>
    public override void ResetDbType() => DbType = DbType.Object;
    internal PureDbParameterCollection? Owner;
}

/// <summary>维护参数顺序和所有权的集合；单个参数实例只能属于一个集合。</summary>
public sealed class PureDbParameterCollection : DbParameterCollection
{
    private readonly List<PureDbParameter> _items = [];
    /// <summary>当前集合中的参数数量。</summary>
    public override int Count => _items.Count;
    /// <summary>实现 ICollection 所要求的同步对象；不表示集合操作自动线程安全。</summary>
    public override object SyncRoot => ((ICollection)_items).SyncRoot;

    /// <summary>移除参数名称的 SQL 前缀，得到集合匹配使用的名称。</summary>
    /// <param name="name">要解析、规范化或查找的标识符。</param>
    /// <returns>按当前方法语义生成、解析或规范化后的文本。</returns>
    internal static string Name(string? name) => (name ?? "").TrimStart('@', ':', '$', '?');
    /// <summary>创建并追加一个参数，返回可继续配置的参数实例。</summary>
    /// <param name="name">参数名称，可以带 @、:、$ 或 ? 前缀。</param>
    /// <param name="value">输入标量；null 或 DBNull.Value 表示 SQL NULL。</param>
    /// <returns>新建并已加入集合的参数实例。</returns>
    public PureDbParameter AddWithValue(string name, object? value)
    {
        var p = new PureDbParameter(name, value);
        Add(p);
        return p;
    }

    /// <summary>确保对象为 PureDbParameter 且尚未归属其他参数集合。</summary>
    /// <param name="value">待处理的标量或参数实例，受当前接口的类型约束。</param>
    /// <returns>本次绑定、解析或查询得到的结果对象；生命周期见方法说明。</returns>
    private PureDbParameter Check(object value)
    {
        if (value is not PureDbParameter p)
            throw new ArgumentException("Expected PureDbParameter");
        if (p.Owner is not null)
            throw new ArgumentException("Parameter already belongs to a collection");
        return p;
    }

    /// <summary>追加一个未归属其他集合的 PureDbParameter，并返回其序号。</summary>
    /// <param name="value">待处理的标量或参数实例，受当前接口的类型约束。</param>
    /// <returns>追加参数的零基序号。</returns>
    public override int Add(object value)
    {
        var p = Check(value);
        p.Owner = this;
        _items.Add(p);
        return Count - 1;
    }

    /// <summary>按输入顺序追加参数；遇到无效参数时抛出异常，此前已追加的参数保留。</summary>
    /// <param name="values">按顺序加入参数集合的参数实例数组。</param>
    public override void AddRange(Array values)
    {
        foreach (var v in values)
            Add(v!);
    }

    /// <summary>移除全部参数并解除它们的集合所有权。</summary>
    public override void Clear()
    {
        foreach (var p in _items)
            p.Owner = null;
        _items.Clear();
    }

    /// <summary>按参数实例或规范化参数名判断集合是否包含参数。</summary>
    /// <param name="value">待处理的标量或参数实例，受当前接口的类型约束。</param>
    /// <returns>条件满足或成功定位时为 true，否则为 false。</returns>
    public override bool Contains(object value) => value is PureDbParameter p && _items.Contains(p);
    /// <summary>按参数实例或规范化参数名判断集合是否包含参数。</summary>
    /// <param name="value">需要转义的 SQL 标识符或待解析的 JSON 路径。</param>
    /// <returns>条件满足或成功定位时为 true，否则为 false。</returns>
    public override bool Contains(string value) => IndexOf(value) >= 0;
    /// <summary>将当前参数实例复制到目标数组的指定位置。</summary>
    /// <param name="array">接收参数实例的目标数组。</param>
    /// <param name="index">当前集合中的零基位置。</param>
    public override void CopyTo(Array array, int index) => ((ICollection)_items).CopyTo(array, index);
    /// <summary>按参数加入顺序枚举参数实例。</summary>
    /// <returns>按当前顺序遍历数据的枚举器。</returns>
    public override IEnumerator GetEnumerator() => _items.GetEnumerator();
    /// <summary>按参数实例或名称查找零基序号；没有匹配项时返回 -1。</summary>
    /// <param name="value">待处理的标量或参数实例，受当前接口的类型约束。</param>
    /// <returns>匹配参数的零基序号，不存在时为 -1。</returns>
    public override int IndexOf(object value) => value is PureDbParameter p ? _items.IndexOf(p) : -1;
    /// <summary>按参数实例或名称查找零基序号；没有匹配项时返回 -1。</summary>
    /// <param name="name">要查找的名称；大小写规则由当前接口说明。</param>
    /// <returns>匹配参数的零基序号，不存在时为 -1。</returns>
    public override int IndexOf(string name) => _items.FindIndex(p => Name(p.ParameterName).Equals(Name(name), StringComparison.OrdinalIgnoreCase));
    /// <summary>在指定零基序号插入一个尚未归属集合的参数。</summary>
    /// <param name="index">当前集合中的零基位置。</param>
    /// <param name="value">待处理的标量或参数实例，受当前接口的类型约束。</param>
    public override void Insert(int index, object value)
    {
        var p = Check(value);
        _items.Insert(index, p);
        p.Owner = this;
    }

    /// <summary>移除指定参数实例并解除所有权；不存在时无操作。</summary>
    /// <param name="value">待处理的标量或参数实例，受当前接口的类型约束。</param>
    public override void Remove(object value)
    {
        int i = IndexOf(value);
        if (i >= 0)
            RemoveAt(i);
    }

    /// <summary>按零基序号或参数名移除参数；目标不存在时抛出异常。</summary>
    /// <param name="index">当前集合中的零基位置。</param>
    public override void RemoveAt(int index)
    {
        _items[index].Owner = null;
        _items.RemoveAt(index);
    }

    /// <summary>按零基序号或参数名移除参数；目标不存在时抛出异常。</summary>
    /// <param name="name">要查找的名称；大小写规则由当前接口说明。</param>
    public override void RemoveAt(string name)
    {
        int i = IndexOf(name);
        if (i < 0)
            throw new IndexOutOfRangeException(name);
        RemoveAt(i);
    }

    /// <summary>按零基序号或规范化名称获取参数；目标不存在时抛出异常。</summary>
    /// <param name="index">当前集合中的零基位置。</param>
    /// <returns>当前配置对应的对象；由调用方按其生命周期约定使用和释放。</returns>
    protected override DbParameter GetParameter(int index) => _items[index];
    /// <summary>按零基序号或规范化名称获取参数；目标不存在时抛出异常。</summary>
    /// <param name="name">要查找的名称；大小写规则由当前接口说明。</param>
    /// <returns>当前配置对应的对象；由调用方按其生命周期约定使用和释放。</returns>
    protected override DbParameter GetParameter(string name)
    {
        int i = IndexOf(name);
        return i < 0 ? throw new IndexOutOfRangeException(name) : _items[i];
    }

    /// <summary>按序号替换参数，或按名称替换、追加参数；维护参数实例的唯一所有权。</summary>
    /// <param name="index">当前集合中的零基位置。</param>
    /// <param name="value">待加入或替换的 PureDbParameter 实例。</param>
    protected override void SetParameter(int index, DbParameter value)
    {
        if (ReferenceEquals(_items[index], value))
            return;
        var p = Check(value);
        _items[index].Owner = null;
        _items[index] = p;
        p.Owner = this;
    }

    /// <summary>按序号替换参数，或按名称替换、追加参数；维护参数实例的唯一所有权。</summary>
    /// <param name="value">待加入或替换的 PureDbParameter 实例。</param>
    /// <param name="name">要查找的名称；大小写规则由当前接口说明。</param>
    protected override void SetParameter(string name, DbParameter value)
    {
        int i = IndexOf(name);
        if (i < 0)
            Add(value);
        else
            SetParameter(i, value);
    }
}
