using Lib.DataFrames;
using Parquet;
using Parquet.Schema;
using Parquet.Data;

namespace Lib.Modules;

/// <summary>
/// Writes a named DataFrame from shared state to one or more Parquet files in a directory.
/// Files are named part-00000.parquet, part-00001.parquet, etc.
/// Overwrite mode deletes existing .parquet files in the directory before writing.
/// </summary>
public class ParquetFileWriter : IModule
{
    private readonly string _sourceDataFrameName;
    private readonly string _outputDirectory;
    private readonly int _numParts;
    private readonly WriteMode _writeMode;

    public ParquetFileWriter(string sourceDataFrameName, string outputDirectory,
        int numParts = 1, WriteMode writeMode = WriteMode.Overwrite)
    {
        _sourceDataFrameName = sourceDataFrameName ?? throw new ArgumentNullException(nameof(sourceDataFrameName));
        _outputDirectory = outputDirectory ?? throw new ArgumentNullException(nameof(outputDirectory));
        _numParts = numParts < 1 ? 1 : numParts;
        _writeMode = writeMode;
    }

    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        if (!sharedState.TryGetValue(_sourceDataFrameName, out var value) || value is not DataFrame df)
            throw new KeyNotFoundException($"DataFrame '{_sourceDataFrameName}' not found in shared state.");

        var resolvedDir = PathHelper.Resolve(_outputDirectory);

        if (_writeMode == WriteMode.Overwrite && Directory.Exists(resolvedDir))
        {
            foreach (var file in Directory.GetFiles(resolvedDir, "*.parquet"))
                File.Delete(file);
        }

        Directory.CreateDirectory(resolvedDir);

        var rows = df.Rows;
        var totalRows = rows.Count;
        var partSize = totalRows / _numParts;
        var remainder = totalRows % _numParts;

        var offset = 0;
        for (int part = 0; part < _numParts; part++)
        {
            var count = partSize + (part < remainder ? 1 : 0);
            var partRows = rows.Skip(offset).Take(count).ToList();
            offset += count;

            var fileName = $"part-{part:D5}.parquet";
            var filePath = Path.Combine(resolvedDir, fileName);

            WriteParquetFile(filePath, df.Columns, partRows);
        }

        return sharedState;
    }

    private static void WriteParquetFile(string filePath, IReadOnlyList<string> columns,
        List<Row> rows)
    {
        // Build schema by inspecting first non-null value per column
        var clrTypes = columns.Select(col =>
        {
            var sample = rows.Select(r => r[col]).FirstOrDefault(v => v != null);
            return GetParquetType(sample);
        }).ToArray();

        var fields = columns.Select((col, i) =>
            new DataField(col, clrTypes[i], isNullable: true)).ToArray();

        var schema = new ParquetSchema(fields);

        using var stream = File.Create(filePath);
        using var writer = ParquetWriter.CreateAsync(schema, stream).Result;
        using var groupWriter = writer.CreateRowGroup();

        for (int c = 0; c < columns.Count; c++)
        {
            var typedArray = BuildTypedArray(clrTypes[c], rows, columns[c]);

            var dataColumn = new DataColumn(fields[c], typedArray);
            groupWriter.WriteColumnAsync(dataColumn).Wait();
        }
    }

    private static Type GetParquetType(object? sample) => sample switch
    {
        int or short or byte => typeof(int?),
        long => typeof(long?),
        double or float => typeof(double?),
        decimal => typeof(decimal?),
        bool => typeof(bool?),
        DateOnly => typeof(DateOnly?),
        DateTime => typeof(DateTime?),
        _ => typeof(string)
    };

    /// <summary>
    /// Parquet.Net requires strongly-typed arrays (e.g. int?[], string[]), not object[].
    /// This builds the correct array type for each column based on the DataField's CLR type.
    /// </summary>
    private static Array BuildTypedArray(Type clrType, List<Row> rows, string col)
    {
        if (clrType == typeof(int?))
            return rows.Select(r => r[col] is null ? (int?)null : Convert.ToInt32(r[col])).ToArray();
        if (clrType == typeof(long?))
            return rows.Select(r => r[col] is null ? (long?)null : Convert.ToInt64(r[col])).ToArray();
        if (clrType == typeof(double?))
            return rows.Select(r => r[col] is null ? (double?)null : Convert.ToDouble(r[col])).ToArray();
        if (clrType == typeof(decimal?))
            return rows.Select(r => r[col] is null ? (decimal?)null : Convert.ToDecimal(r[col])).ToArray();
        if (clrType == typeof(bool?))
            return rows.Select(r => r[col] is null ? (bool?)null : Convert.ToBoolean(r[col])).ToArray();
        if (clrType == typeof(DateOnly?))
            return rows.Select(r => r[col] is DateOnly d ? (DateOnly?)d : null).ToArray();
        if (clrType == typeof(DateTime?))
            return rows.Select(r => r[col] is DateTime dt ? (DateTime?)dt : null).ToArray();

        // string (default) — nulls become null strings
        return rows.Select(r => r[col]?.ToString()).ToArray();
    }
}
