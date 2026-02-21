using System.Text.Json;
using Lib.Modules;

namespace Lib;

/// <summary>
/// Reads a module's JSON config element and instantiates the appropriate IModule implementation.
/// </summary>
public static class ModuleFactory
{
    public static IModule Create(JsonElement el)
    {
        var type = el.GetProperty("type").GetString()
            ?? throw new InvalidOperationException("Module config is missing the required 'type' field.");

        return type switch
        {
            "DataSourcing"    => CreateDataSourcing(el),
            "Transformation"  => CreateTransformation(el),
            "DataFrameWriter" => CreateDataFrameWriter(el),
            "External"        => CreateExternal(el),
            _ => throw new InvalidOperationException($"Unknown module type: '{type}'.")
        };
    }

    private static DataSourcing CreateDataSourcing(JsonElement el) => new(
        el.GetProperty("resultName").GetString()!,
        el.GetProperty("schema").GetString()!,
        el.GetProperty("table").GetString()!,
        el.GetProperty("columns").EnumerateArray().Select(c => c.GetString()!).ToArray(),
        DateOnly.Parse(el.GetProperty("minEffectiveDate").GetString()!),
        DateOnly.Parse(el.GetProperty("maxEffectiveDate").GetString()!),
        el.TryGetProperty("additionalFilter", out var af) ? af.GetString() ?? "" : ""
    );

    private static Transformation CreateTransformation(JsonElement el) => new(
        el.GetProperty("resultName").GetString()!,
        el.GetProperty("sql").GetString()!
    );

    private static DataFrameWriter CreateDataFrameWriter(JsonElement el) => new(
        el.GetProperty("source").GetString()!,
        el.GetProperty("targetTable").GetString()!,
        Enum.Parse<WriteMode>(el.GetProperty("writeMode").GetString()!)
    );

    private static External CreateExternal(JsonElement el) => new(
        el.GetProperty("assemblyPath").GetString()!,
        el.GetProperty("typeName").GetString()!
    );
}
