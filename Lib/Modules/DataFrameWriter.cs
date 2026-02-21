using Npgsql;
using Lib.DataFrames;

namespace Lib.Modules;

public enum WriteMode
{
    Overwrite,
    Append
}

/// <summary>
/// Writes a named DataFrame from shared state into a table in the curated schema.
/// The target table is created automatically if it does not exist, with column types
/// inferred from the DataFrame's data. Write mode controls whether existing data
/// is replaced (Overwrite) or added to (Append).
/// </summary>
public class DataFrameWriter : IModule
{
    private readonly string _sourceDataFrameName;
    private readonly string _targetTableName;
    private readonly WriteMode _writeMode;
    private const string TargetSchema = "curated";

    public DataFrameWriter(string sourceDataFrameName, string targetTableName, WriteMode writeMode)
    {
        _sourceDataFrameName = sourceDataFrameName ?? throw new ArgumentNullException(nameof(sourceDataFrameName));
        _targetTableName = targetTableName ?? throw new ArgumentNullException(nameof(targetTableName));
        _writeMode = writeMode;
    }

    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        if (!sharedState.TryGetValue(_sourceDataFrameName, out var value) || value is not DataFrame df)
            throw new KeyNotFoundException($"DataFrame '{_sourceDataFrameName}' not found in shared state.");

        using var connection = new NpgsqlConnection(ConnectionHelper.GetConnectionString());
        connection.Open();

        EnsureTableExists(connection, df);

        if (_writeMode == WriteMode.Overwrite)
        {
            using var truncateCmd = connection.CreateCommand();
            truncateCmd.CommandText = $"TRUNCATE TABLE \"{TargetSchema}\".\"{_targetTableName}\"";
            truncateCmd.ExecuteNonQuery();
        }

        InsertRows(connection, df);

        return sharedState;
    }

    private void EnsureTableExists(NpgsqlConnection connection, DataFrame df)
    {
        var columnDefs = df.Columns.Select(col =>
        {
            var sampleValue = df.Rows.Select(r => r[col]).FirstOrDefault(v => v != null);
            return $"\"{col}\" {GetPostgresType(sampleValue)}";
        });

        using var cmd = connection.CreateCommand();
        cmd.CommandText = $@"
            CREATE TABLE IF NOT EXISTS ""{TargetSchema}"".""{_targetTableName}"" (
                {string.Join(",\n                ", columnDefs)}
            )";
        cmd.ExecuteNonQuery();
    }

    private void InsertRows(NpgsqlConnection connection, DataFrame df)
    {
        if (!df.Rows.Any()) return;

        var colNames = string.Join(", ", df.Columns.Select(c => $"\"{c}\""));
        var paramNames = string.Join(", ", df.Columns.Select((_, i) => $"@p{i}"));

        using var transaction = connection.BeginTransaction();
        using var insertCmd = connection.CreateCommand();
        insertCmd.Transaction = transaction;
        insertCmd.CommandText = $"INSERT INTO \"{TargetSchema}\".\"{_targetTableName}\" ({colNames}) VALUES ({paramNames})";

        foreach (var row in df.Rows)
        {
            insertCmd.Parameters.Clear();
            for (int i = 0; i < df.Columns.Count; i++)
            {
                var val = row[df.Columns[i]];
                insertCmd.Parameters.AddWithValue($"@p{i}", CoerceValue(val));
            }
            insertCmd.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>
    /// Coerces values that lost their original .NET type during SQLite round-tripping.
    /// Dates are stored as TEXT in SQLite and come back as strings; Npgsql needs them
    /// as DateOnly / DateTime so it sends the correct OID to PostgreSQL.
    /// </summary>
    private static object CoerceValue(object? val)
    {
        if (val is null) return DBNull.Value;
        if (val is string s)
        {
            if (DateOnly.TryParseExact(s, "yyyy-MM-dd", null,
                    System.Globalization.DateTimeStyles.None, out var d))
                return d;
            if (DateTime.TryParseExact(s, "yyyy-MM-dd HH:mm:ss", null,
                    System.Globalization.DateTimeStyles.None, out var dt))
                return dt;
        }
        return val;
    }

    private static string GetPostgresType(object? sampleValue) => sampleValue switch
    {
        int or short or byte  => "INTEGER",
        long                  => "BIGINT",
        double or float       => "DOUBLE PRECISION",
        decimal               => "NUMERIC",
        bool                  => "BOOLEAN",
        DateOnly              => "DATE",
        DateTime              => "TIMESTAMP",
        _                     => "TEXT"
    };
}
