using System.Runtime.CompilerServices;

namespace PureDb;

/// <summary>按不可变语法树实例和模式上下文比较计划键，避免命中缓存时递归散列整条 SQL。</summary>
/// <typeparam name="TStatement">解析缓存持有的语句引用类型。</typeparam>
/// <typeparam name="TContext">影响计划绑定的表、索引数量或模式上下文。</typeparam>
internal sealed class SqlPlanComparer<TStatement, TContext> : IEqualityComparer<(TStatement Statement, TContext Context)> where TStatement : class
{
    /// <summary>同一键类型共享的无状态比较器。</summary>
    internal static readonly SqlPlanComparer<TStatement, TContext> Instance = new();

    /// <summary>仅在同一语法树实例且绑定上下文相同时复用计划。</summary>
    /// <param name="left">第一个语法树和上下文。</param>
    /// <param name="right">第二个语法树和上下文。</param>
    /// <returns>两个键是否指向同一编译输入。</returns>
    public bool Equals((TStatement Statement, TContext Context) left, (TStatement Statement, TContext Context) right)
        => ReferenceEquals(left.Statement, right.Statement) && EqualityComparer<TContext>.Default.Equals(left.Context, right.Context);

    /// <summary>使用语法树身份散列和上下文散列，复杂查询的成本不再随表达式深度增加。</summary>
    /// <param name="key">已经解析且不可变的编译输入。</param>
    /// <returns>与实例相等规则一致的散列值。</returns>
    public int GetHashCode((TStatement Statement, TContext Context) key)
        => HashCode.Combine(RuntimeHelpers.GetHashCode(key.Statement), EqualityComparer<TContext>.Default.GetHashCode(key.Context!));
}
