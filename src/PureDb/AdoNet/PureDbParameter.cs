using System.Collections;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

namespace PureDb.AdoNet;

public sealed class PureDbParameter : DbParameter
{
    public PureDbParameter() { }
    public PureDbParameter(string name, object? value) { ParameterName = name; Value = value; }
    public override DbType DbType { get; set; } = DbType.Object;
    public override ParameterDirection Direction { get; set; } = ParameterDirection.Input;
    public override bool IsNullable { get; set; } = true;
    [AllowNull] public override string ParameterName { get; set; } = "";
    [AllowNull] public override string SourceColumn { get; set; } = "";
    public override object? Value { get; set; }
    public override bool SourceColumnNullMapping { get; set; }
    public override int Size { get; set; }
    public override void ResetDbType() => DbType = DbType.Object;
    internal PureDbParameterCollection? Owner;
}
public sealed class PureDbParameterCollection : DbParameterCollection
{
    private readonly List<PureDbParameter> _items = [];
    public override int Count => _items.Count;
    public override object SyncRoot => ((ICollection)_items).SyncRoot;
    internal static string Name(string? name) => (name ?? "").TrimStart('@', ':', '$', '?');
    public PureDbParameter AddWithValue(string name, object? value) { var p = new PureDbParameter(name, value); Add(p); return p; }
    private PureDbParameter Check(object value)
    {
        if (value is not PureDbParameter p) throw new ArgumentException("Expected PureDbParameter");
        if (p.Owner is not null) throw new ArgumentException("Parameter already belongs to a collection"); return p;
    }
    public override int Add(object value) { var p = Check(value); p.Owner = this; _items.Add(p); return Count - 1; }
    public override void AddRange(Array values) { foreach (var v in values) Add(v!); }
    public override void Clear() { foreach (var p in _items) p.Owner = null; _items.Clear(); }
    public override bool Contains(object value) => value is PureDbParameter p && _items.Contains(p);
    public override bool Contains(string value) => IndexOf(value) >= 0;
    public override void CopyTo(Array array, int index) => ((ICollection)_items).CopyTo(array, index);
    public override IEnumerator GetEnumerator() => _items.GetEnumerator();
    public override int IndexOf(object value) => value is PureDbParameter p ? _items.IndexOf(p) : -1;
    public override int IndexOf(string name) => _items.FindIndex(p => Name(p.ParameterName).Equals(Name(name), StringComparison.OrdinalIgnoreCase));
    public override void Insert(int index, object value) { var p = Check(value); _items.Insert(index, p); p.Owner = this; }
    public override void Remove(object value) { int i = IndexOf(value); if (i >= 0) RemoveAt(i); }
    public override void RemoveAt(int index) { _items[index].Owner = null; _items.RemoveAt(index); }
    public override void RemoveAt(string name) { int i = IndexOf(name); if (i < 0) throw new IndexOutOfRangeException(name); RemoveAt(i); }
    protected override DbParameter GetParameter(int index) => _items[index];
    protected override DbParameter GetParameter(string name) { int i = IndexOf(name); return i < 0 ? throw new IndexOutOfRangeException(name) : _items[i]; }
    protected override void SetParameter(int index, DbParameter value)
    {
        if (ReferenceEquals(_items[index], value)) return;
        var p = Check(value); _items[index].Owner = null; _items[index] = p; p.Owner = this;
    }
    protected override void SetParameter(string name, DbParameter value) { int i = IndexOf(name); if (i < 0) Add(value); else SetParameter(i, value); }
}
