using System.Data;
using Npgsql;
using Lib.DataFrames;

namespace Lib.Modules;

/// <summary>
/// Users provide table, column, and date range information. The module queries the source table
/// across the full date range and stores a single DataFrame (with ifw_effective_date column included) in shared state.
///
/// Effective dates may be supplied in several ways (mutually exclusive):
///   1. Directly in the module config (minEffectiveDate / maxEffectiveDate fields in the job conf JSON).
///   2. lookbackDays: pulls T-N through T-0 (N calendar days before __etlEffectiveDate).
///   3. mostRecentPrior: queries the datalake for the latest date strictly before __etlEffectiveDate.
///   4. mostRecent: queries the datalake for the latest date on or before __etlEffectiveDate.
///   5. Via a reserved shared-state key injected by the executor before the pipeline runs:
///        "__etlEffectiveDate" (DateOnly).
/// If neither is present, Execute throws InvalidOperationException.
/// </summary>
public class DataSourcing : IModule
{
    // Reserved shared-state key set by the executor at runtime.
    public const string EtlEffectiveDateKey = "__etlEffectiveDate";

    private readonly string    _resultingDataFrameName;
    private readonly string    _schema;
    private readonly string    _tableName;
    private readonly string[]  _columnNames;
    private readonly DateOnly? _minEffectiveDate;
    private readonly DateOnly? _maxEffectiveDate;
    private readonly string    _additionalFilter;
    private readonly int?      _lookbackDays;
    private readonly bool      _mostRecentPrior;
    private readonly bool      _mostRecent;

    public DataSourcing(
        string    resultingDataFrameName,
        string    schema,
        string    tableName,
        string[]  columnNames,
        DateOnly? minEffectiveDate = null,
        DateOnly? maxEffectiveDate = null,
        string    additionalFilter = "",
        int?      lookbackDays = null,
        bool      mostRecentPrior = false,
        bool      mostRecent = false)
    {
        _resultingDataFrameName = resultingDataFrameName ?? throw new ArgumentNullException(nameof(resultingDataFrameName));
        _schema                 = schema                ?? throw new ArgumentNullException(nameof(schema));
        _tableName              = tableName             ?? throw new ArgumentNullException(nameof(tableName));
        _columnNames            = columnNames           ?? throw new ArgumentNullException(nameof(columnNames));
        _minEffectiveDate       = minEffectiveDate;
        _maxEffectiveDate       = maxEffectiveDate;
        _additionalFilter       = additionalFilter      ?? "";
        _lookbackDays           = lookbackDays;
        _mostRecentPrior        = mostRecentPrior;
        _mostRecent             = mostRecent;

        ValidateDateModes();
    }

    private void ValidateDateModes()
    {
        var hasStaticDates = _minEffectiveDate.HasValue || _maxEffectiveDate.HasValue;
        var hasLookback = _lookbackDays.HasValue;
        var hasMostRecentPrior = _mostRecentPrior;
        var hasMostRecent = _mostRecent;

        var modeCount = (hasStaticDates ? 1 : 0) + (hasLookback ? 1 : 0)
                      + (hasMostRecentPrior ? 1 : 0) + (hasMostRecent ? 1 : 0);

        if (modeCount > 1)
            throw new ArgumentException(
                "DataSourcing date modes are mutually exclusive. " +
                "Specify only one of: static dates (minEffectiveDate/maxEffectiveDate), lookbackDays, mostRecentPrior, or mostRecent.");

        if (_lookbackDays.HasValue && _lookbackDays.Value < 0)
            throw new ArgumentOutOfRangeException(nameof(_lookbackDays), "lookbackDays must be >= 0.");
    }

    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var dateRange = ResolveDateRange(sharedState);

        if (dateRange is null)
        {
            // No matching date found (mostRecentPrior/mostRecent with no data) — empty DataFrame
            var columns = _columnNames.Contains("ifw_effective_date", StringComparer.OrdinalIgnoreCase)
                ? _columnNames
                : _columnNames.Append("ifw_effective_date").ToArray();
            sharedState[_resultingDataFrameName] = new DataFrame(columns);
        }
        else
        {
            var (minDate, maxDate) = dateRange.Value;
            sharedState[_resultingDataFrameName] = FetchData(minDate, maxDate);
        }

        return sharedState;
    }

    /// <summary>
    /// Resolves the effective min/max date range based on the configured mode.
    /// Returns null when mostRecentPrior/mostRecent finds no matching date.
    /// Internal for testability.
    /// </summary>
    internal (DateOnly min, DateOnly max)? ResolveDateRange(Dictionary<string, object> sharedState)
    {
        if (_mostRecentPrior)
        {
            var t0 = GetEtlEffectiveDate(sharedState);
            var priorDate = QueryMostRecentDate(t0, strict: true);
            return priorDate.HasValue ? (priorDate.Value, priorDate.Value) : null;
        }

        if (_mostRecent)
        {
            var t0 = GetEtlEffectiveDate(sharedState);
            var recentDate = QueryMostRecentDate(t0, strict: false);
            return recentDate.HasValue ? (recentDate.Value, recentDate.Value) : null;
        }

        if (_lookbackDays.HasValue)
        {
            var t0 = GetEtlEffectiveDate(sharedState);
            var min = t0.AddDays(-_lookbackDays.Value);
            return (min, t0);
        }

        // Static dates or __etlEffectiveDate fallback
        var minDate = _minEffectiveDate ?? GetEtlEffectiveDate(sharedState);
        var maxDate = _maxEffectiveDate ?? GetEtlEffectiveDate(sharedState);
        return (minDate, maxDate);
    }

    private DateOnly GetEtlEffectiveDate(Dictionary<string, object> sharedState)
    {
        if (sharedState.TryGetValue(EtlEffectiveDateKey, out var val) && val is DateOnly d)
            return d;

        throw new InvalidOperationException(
            $"DataSourcing '{_resultingDataFrameName}': no effective date available. " +
            $"Inject '{EtlEffectiveDateKey}' into shared state.");
    }

    /// <summary>
    /// Queries the datalake for the most recent ifw_effective_date.
    /// When strict is true, uses &lt; (strictly before). When false, uses &lt;= (on or before).
    /// </summary>
    private DateOnly? QueryMostRecentDate(DateOnly asOfDate, bool strict)
    {
        var op = strict ? "<" : "<=";
        var query = $@"
            SELECT MAX(ifw_effective_date)
            FROM ""{_schema}"".""{_tableName}""
            WHERE ifw_effective_date {op} @date";

        using var connection = new NpgsqlConnection(ConnectionHelper.GetConnectionString());
        connection.Open();

        using var command = new NpgsqlCommand(query, connection);
        command.Parameters.AddWithValue("@date", asOfDate.ToDateTime(TimeOnly.MinValue));

        var result = command.ExecuteScalar();

        if (result is null or DBNull)
            return null;

        return DateOnly.FromDateTime((DateTime)result);
    }

    private DataFrame FetchData(DateOnly minEffectiveDate, DateOnly maxEffectiveDate)
    {
        var includesAsOf = _columnNames.Contains("ifw_effective_date", StringComparer.OrdinalIgnoreCase);

        var columnList = string.Join(", ", _columnNames.Select(col => $"\"{col}\""));
        var selectClause = includesAsOf ? columnList : $"{columnList}, ifw_effective_date";

        var query = $@"
        SELECT {selectClause}
        FROM ""{_schema}"".""{_tableName}""
        WHERE ifw_effective_date >= @minDate
          AND ifw_effective_date <= @maxDate";

        if (!string.IsNullOrWhiteSpace(_additionalFilter))
        {
            query += $" AND ({_additionalFilter})";
        }

        query += " ORDER BY ifw_effective_date";

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
                rowData["ifw_effective_date"] = DateOnly.FromDateTime(reader.GetDateTime("ifw_effective_date"));
            }
            rows.Add(rowData);
        }

        // Preserve column schema even when query returns no rows
        if (rows.Count == 0)
        {
            var columns = includesAsOf
                ? _columnNames
                : _columnNames.Append("ifw_effective_date").ToArray();
            return new DataFrame(columns);
        }
        return new DataFrame(rows);
    }

}
