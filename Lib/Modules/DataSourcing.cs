using System.Data;
using Npgsql;
using Lib.DataFrames;

namespace Lib.Modules;

/// <summary>
/// Users provide table, column, and date range information. The module queries the source table
/// across the full date range and stores a single DataFrame (with as_of column included) in shared state.
/// </summary>
public class DataSourcing : IModule
{
    private string _resultingDataFrameName;
    private string _schema;
    private string _tableName;
    private string[] _columnNames;
    private DateOnly _minEffectiveDate; 
    private DateOnly _maxEffectiveDate;
    private string _additionalFilter;
    
    /// <summary>
    /// Initializes a new instance of the DataSourcing class with the specified parameters
    /// </summary>
    /// <param name="resultingDataFrameName">Name for the resulting DataFrames</param>
    /// <param name="schema">Database schema name</param>
    /// <param name="tableName">Name of the source table</param>
    /// <param name="columnNames">Array of column names to retrieve</param>
    /// <param name="minEffectiveDate">Minimum effective date for data retrieval</param>
    /// <param name="maxEffectiveDate">Maximum effective date for data retrieval</param>
    /// <param name="additionalFilter">Additional filter criteria (optional)</param>
    public DataSourcing(
        string resultingDataFrameName,
        string schema,
        string tableName,
        string[] columnNames,
        DateOnly minEffectiveDate,
        DateOnly maxEffectiveDate,
        string additionalFilter = "")
    {
        this._resultingDataFrameName = resultingDataFrameName ?? throw new ArgumentNullException(nameof(resultingDataFrameName));
        this._schema = schema ?? throw new ArgumentNullException(nameof(schema));
        this._tableName = tableName ?? throw new ArgumentNullException(nameof(tableName));
        this._columnNames = columnNames ?? throw new ArgumentNullException(nameof(columnNames));
        this._minEffectiveDate = minEffectiveDate;
        this._maxEffectiveDate = maxEffectiveDate;
        this._additionalFilter = additionalFilter ?? "";
    }

    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        sharedState[_resultingDataFrameName] = FetchData();
        return sharedState;
    }

    /// <summary>
    /// Fetches data from the database across the full date range and returns it as a single DataFrame.
    /// The as_of column is always included so transformation steps can filter or group by date as needed.
    /// </summary>
    private DataFrame FetchData()
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
        command.Parameters.AddWithValue("@minDate", _minEffectiveDate.ToDateTime(TimeOnly.MinValue));
        command.Parameters.AddWithValue("@maxDate", _maxEffectiveDate.ToDateTime(TimeOnly.MinValue));

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