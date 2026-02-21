using Microsoft.Data.Sqlite;
using Lib.DataFrames;

namespace Lib.Modules;

/// <summary>
/// Executes a user-supplied SQL string against DataFrames in shared state.
/// Each DataFrame in shared state is registered as an in-memory SQLite table,
/// keyed by its shared state name, so SQL can reference them directly by name.
/// The result is stored in shared state under the configured output name.
/// </summary>
public class Transformation : IModule
{
    private readonly string _resultingDataFrameName;
    private readonly string _sql;

    public Transformation(string resultingDataFrameName, string sql)
    {
        _resultingDataFrameName = resultingDataFrameName ?? throw new ArgumentNullException(nameof(resultingDataFrameName));
        _sql = sql ?? throw new ArgumentNullException(nameof(sql));
    }

    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        foreach (var (key, value) in sharedState)
        {
            if (value is DataFrame df)
            {
                RegisterTable(connection, key, df);
            }
        }

        using var command = connection.CreateCommand();
        command.CommandText = _sql;
        using var reader = command.ExecuteReader();

        sharedState[_resultingDataFrameName] = ReaderToDataFrame(reader);
        return sharedState;
    }

    private static void RegisterTable(SqliteConnection connection, string tableName, DataFrame df)
    {
        if (!df.Rows.Any()) return;

        var columnTypes = df.Columns.ToDictionary(
            col => col,
            col => GetSqliteType(df.Rows.Select(r => r[col]).FirstOrDefault(v => v != null))
        );

        var columnDefs = df.Columns.Select(col => $"\"{col}\" {columnTypes[col]}");
        using var createCmd = connection.CreateCommand();
        createCmd.CommandText = $"CREATE TABLE \"{tableName}\" ({string.Join(", ", columnDefs)})";
        createCmd.ExecuteNonQuery();

        var colNames = string.Join(", ", df.Columns.Select(c => $"\"{c}\""));
        var paramNames = string.Join(", ", df.Columns.Select((_, i) => $"@p{i}"));

        using var insertCmd = connection.CreateCommand();
        insertCmd.CommandText = $"INSERT INTO \"{tableName}\" ({colNames}) VALUES ({paramNames})";

        using var transaction = connection.BeginTransaction();
        insertCmd.Transaction = transaction;

        foreach (var row in df.Rows)
        {
            insertCmd.Parameters.Clear();
            for (int i = 0; i < df.Columns.Count; i++)
            {
                insertCmd.Parameters.AddWithValue($"@p{i}", ToSqliteValue(row[df.Columns[i]]));
            }
            insertCmd.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private static DataFrame ReaderToDataFrame(SqliteDataReader reader)
    {
        var columns = Enumerable.Range(0, reader.FieldCount).Select(i => reader.GetName(i)).ToList();
        var rows = new List<Dictionary<string, object?>>();

        while (reader.Read())
        {
            var rowData = new Dictionary<string, object?>();
            for (int i = 0; i < reader.FieldCount; i++)
            {
                rowData[columns[i]] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }
            rows.Add(rowData);
        }

        return new DataFrame(rows);
    }

    private static string GetSqliteType(object? sampleValue) => sampleValue switch
    {
        int or long or short or byte => "INTEGER",
        double or float or decimal => "REAL",
        bool => "INTEGER",
        _ => "TEXT"
    };

    private static object ToSqliteValue(object? value) => value switch
    {
        null => DBNull.Value,
        bool b => b ? 1 : 0,
        DateOnly d => d.ToString("yyyy-MM-dd"),
        DateTime dt => dt.ToString("yyyy-MM-ddTHH:mm:ss"),
        _ => value
    };
}
