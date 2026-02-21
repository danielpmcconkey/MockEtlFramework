using System.Dynamic;

namespace Lib.DataFrames;

public class Row : DynamicObject
{
    private readonly Dictionary<string, object?> _data;

    public Row(Dictionary<string, object?> data)
    {
        _data = data;
    }

    public override bool TryGetMember(GetMemberBinder binder, out object? result)
    {
        return _data.TryGetValue(binder.Name, out result);
    }

    public override bool TrySetMember(SetMemberBinder binder, object? value)
    {
        _data[binder.Name] = value;
        return true;
    }

    public object? this[string columnName]
    {
        get => _data.GetValueOrDefault(columnName);
        set => _data[columnName] = value;
    }

    public Dictionary<string, object?> ToDictionary() => new(_data);
}