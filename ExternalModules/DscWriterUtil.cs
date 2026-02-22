using Npgsql;
using Lib;
using Lib.DataFrames;

namespace ExternalModules;

/// <summary>
/// Shared utility for writing DataFrames to the double_secret_curated schema.
/// Used by all V2 job processors since the framework's DataFrameWriter
/// is hardcoded to the curated schema.
/// </summary>
public static class DscWriterUtil
{
    private const string Schema = "double_secret_curated";

    /// <summary>
    /// Writes a DataFrame to a table in double_secret_curated.
    /// If overwrite is true, truncates the table first.
    /// </summary>
    public static void Write(string targetTable, bool overwrite, DataFrame df)
    {
        using var connection = new NpgsqlConnection(ConnectionHelper.GetConnectionString());
        connection.Open();

        if (overwrite)
        {
            using var deleteCmd = connection.CreateCommand();
            deleteCmd.CommandText = $"DELETE FROM \"{Schema}\".\"{targetTable}\"";
            deleteCmd.ExecuteNonQuery();
        }

        InsertRows(connection, targetTable, df);
    }

    private static void InsertRows(NpgsqlConnection connection, string table, DataFrame df)
    {
        if (!df.Rows.Any()) return;

        var colNames = string.Join(", ", df.Columns.Select(c => $"\"{c}\""));
        var paramNames = string.Join(", ", df.Columns.Select((_, i) => $"@p{i}"));

        using var transaction = connection.BeginTransaction();
        using var insertCmd = connection.CreateCommand();
        insertCmd.Transaction = transaction;
        insertCmd.CommandText = $"INSERT INTO \"{Schema}\".\"{table}\" ({colNames}) VALUES ({paramNames})";

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
}
