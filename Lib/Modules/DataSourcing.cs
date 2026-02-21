using System.Data;
using Npgsql;
using Lib.DataFrames;

namespace Lib.Modules;

/// <summary>
/// Users provide table, column, and date range information. The module queries the source table
/// across the full date range and stores a single DataFrame (with as_of column included) in shared state.
///
/// Effective dates may be supplied in two ways:
///   1. Directly in the module config (minEffectiveDate / maxEffectiveDate fields in the job conf JSON).
///   2. Via reserved shared-state keys injected by the executor before the pipeline runs:
///        "__minEffectiveDate" (DateOnly) and "__maxEffectiveDate" (DateOnly).
/// If neither is present, Execute throws InvalidOperationException.
/// </summary>
public class DataSourcing : IModule
{
    // Reserved shared-state keys set by the executor at runtime.
    public const string MinDateKey = "__minEffectiveDate";
    public const string MaxDateKey = "__maxEffectiveDate";

    private readonly string    _resultingDataFrameName;
    private readonly string    _schema;
    private readonly string    _tableName;
    private readonly string[]  _columnNames;
    private readonly DateOnly? _minEffectiveDate;
    private readonly DateOnly? _maxEffectiveDate;
    private readonly string    _additionalFilter;

    public DataSourcing(
        string    resultingDataFrameName,
        string    schema,
        string    tableName,
        string[]  columnNames,
        DateOnly? minEffectiveDate = null,
        DateOnly? maxEffectiveDate = null,
        string    additionalFilter = "")
    {
        _resultingDataFrameName = resultingDataFrameName ?? throw new ArgumentNullException(nameof(resultingDataFrameName));
        _schema                 = schema                ?? throw new ArgumentNullException(nameof(schema));
        _tableName              = tableName             ?? throw new ArgumentNullException(nameof(tableName));
        _columnNames            = columnNames           ?? throw new ArgumentNullException(nameof(columnNames));
        _minEffectiveDate       = minEffectiveDate;
        _maxEffectiveDate       = maxEffectiveDate;
        _additionalFilter       = additionalFilter      ?? "";
    }

    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var minDate = _minEffectiveDate
            ?? (sharedState.TryGetValue(MinDateKey, out var mn) && mn is DateOnly mn2 ? mn2
                : throw new InvalidOperationException(
                    $"DataSourcing '{_resultingDataFrameName}': no effective date available. " +
                    $"Supply minEffectiveDate in the job conf or inject '{MinDateKey}' into shared state."));

        var maxDate = _maxEffectiveDate
            ?? (sharedState.TryGetValue(MaxDateKey, out var mx) && mx is DateOnly mx2 ? mx2
                : throw new InvalidOperationException(
                    $"DataSourcing '{_resultingDataFrameName}': no effective date available. " +
                    $"Supply maxEffectiveDate in the job conf or inject '{MaxDateKey}' into shared state."));

        sharedState[_resultingDataFrameName] = FetchData(minDate, maxDate);
        return sharedState;
    }

    private DataFrame FetchData(DateOnly minEffectiveDate, DateOnly maxEffectiveDate)
    {
        var includesAsOf = _columnNames.Contains("as_of", StringComparer.OrdinalIgnoreCase);

        var columnList = string.Join(", ", _columnNames.Select(col => $"\"{col}\""));
        var selectClause = includesAsOf ? columnList : $"{columnList}, as_of";

        var query = $@"
        SELECT {selectClause}
        FROM ""{_schema}"".""{_tableName}""
        WHERE as_of >= @minDate
          AND as_of <= @maxDate";

        if (!string.IsNullOrWhiteSpace(_additionalFilter))
        {
            query += $" AND ({_additionalFilter})";
        }

        query += " ORDER BY as_of";

        using var connection = new NpgsqlConnection(ConnectionHelper.GetConnectionString());
        connection.Open();

        using var command = new NpgsqlCommand(query, connection);
        command.Parameters.AddWithValue("@minDate", minEffectiveDate.ToDateTime(TimeOnly.MinValue));
        command.Parameters.AddWithValue("@maxDate", maxEffectiveDate.ToDateTime(TimeOnly.MinValue));

        using var reader = command.ExecuteReader();

        var rows = new List<Dictionary<string, object?>>();

        while (reader.Read())
        {
            var rowData = new Dictionary<string, object?>();
            foreach (var columnName in _columnNames)
            {
                rowData[columnName] = reader.IsDBNull(columnName) ? null : reader[columnName];
            }
            if (!includesAsOf)
            {
                rowData["as_of"] = DateOnly.FromDateTime(reader.GetDateTime("as_of"));
            }
            rows.Add(rowData);
        }

        return new DataFrame(rows);
    }
    
}