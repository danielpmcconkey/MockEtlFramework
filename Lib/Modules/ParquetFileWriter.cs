using Lib.DataFrames;
using Parquet;
using Parquet.Schema;
using Parquet.Data;

namespace Lib.Modules;

/// <summary>
/// Writes a named DataFrame from shared state to date-partitioned Parquet files.
/// Output path: {outputDirectory}/{jobDirName}/{outputTableDirName}/{etl_effective_date}/{fileName}/part-N.parquet
/// Injects an etl_effective_date column into every row before writing.
/// Files are named part-00000.parquet, part-00001.parquet, etc.
/// Overwrite mode writes to today's partition; prior partitions are untouched.
/// </summary>
public class ParquetFileWriter : IModule
{
    private readonly string _sourceDataFrameName;
    private readonly string _outputDirectory;
    private readonly string _jobDirName;
    private readonly string _outputTableDirName;
    private readonly string _fileName;
    private readonly int _numParts;
    private readonly WriteMode _writeMode;

    public ParquetFileWriter(string sourceDataFrameName, string outputDirectory,
        string jobDirName, string outputTableDirName, string fileName,
        int numParts = 1, WriteMode writeMode = WriteMode.Overwrite)
    {
        _sourceDataFrameName = sourceDataFrameName ?? throw new ArgumentNullException(nameof(sourceDataFrameName));
        _outputDirectory = outputDirectory ?? throw new ArgumentNullException(nameof(outputDirectory));
        _jobDirName = jobDirName ?? throw new ArgumentNullException(nameof(jobDirName));
        _outputTableDirName = outputTableDirName ?? throw new ArgumentNullException(nameof(outputTableDirName));
        _fileName = fileName ?? throw new ArgumentNullException(nameof(fileName));
        _numParts = numParts < 1 ? 1 : numParts;
        _writeMode = writeMode;
    }

    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        if (!sharedState.TryGetValue(_sourceDataFrameName, out var value) || value is not DataFrame df)
            throw new KeyNotFoundException($"DataFrame '{_sourceDataFrameName}' not found in shared state.");

        if (!sharedState.TryGetValue(DataSourcing.EtlEffectiveDateKey, out var dateVal) || dateVal is not DateOnly effectiveDate)
            throw new InvalidOperationException(
                $"'{DataSourcing.EtlEffectiveDateKey}' not found or not a DateOnly in shared state.");

        var dateStr = effectiveDate.ToString("yyyy-MM-dd");
        var tableDir = Path.Combine(PathHelper.Resolve(_outputDirectory), _jobDirName, _outputTableDirName);

        // Append mode: union with prior partition's data
        if (_writeMode == WriteMode.Append)
        {
            var priorDate = DatePartitionHelper.FindLatestPartition(tableDir);
            if (priorDate != null)
            {
                var priorParquetDir = Path.Combine(tableDir, priorDate, _fileName);
                if (Directory.Exists(priorParquetDir))
                {
                    var priorDf = DataFrame.FromParquet(priorParquetDir);
                    priorDf = priorDf.Drop("etl_effective_date");
                    df = priorDf.Union(df);
                }
            }
        }

        // Inject etl_effective_date column
        df = df.WithColumn("etl_effective_date", _ => dateStr);

        // Build date-partitioned output path: {tableDir}/{date}/{fileName}/
        var parquetDir = Path.Combine(tableDir, dateStr, _fileName);

        // Overwrite: delete existing parquet files in this partition's output dir
        if (_writeMode == WriteMode.Overwrite && Directory.Exists(parquetDir))
        {
            foreach (var file in Directory.GetFiles(parquetDir, "*.parquet"))
                File.Delete(file);
        }

        Directory.CreateDirectory(parquetDir);

        var rows = df.Rows;
        var totalRows = rows.Count;

        if (totalRows == 0 || df.Columns.Count == 0)
        {
            Console.WriteLine($"[ParquetFileWriter] Skipping empty DataFrame '{_sourceDataFrameName}' — no rows to write.");
            return sharedState;
        }

        // Infer types once from ALL rows so every part file gets a consistent schema.
        var clrTypes = InferColumnTypes(df.Columns, rows);

        var partSize = totalRows / _numParts;
        var remainder = totalRows % _numParts;

        var offset = 0;
        for (int part = 0; part < _numParts; part++)
        {
            var count = partSize + (part < remainder ? 1 : 0);
            var partRows = rows.Skip(offset).Take(count).ToList();
            offset += count;

            var fileName = $"part-{part:D5}.parquet";
            var filePath = Path.Combine(parquetDir, fileName);

            WriteParquetFile(filePath, df.Columns, partRows, clrTypes);
        }

        return sharedState;
    }

    /// <summary>
    /// Infer CLR types for each column by scanning ALL rows. This ensures consistent
    /// schema across part files even when some parts have all-null values for a column.
    /// </summary>
    private static Type[] InferColumnTypes(IReadOnlyList<string> columns, IReadOnlyList<Row> rows)
    {
        return columns.Select(col =>
        {
            var sample = rows.Select(r => r[col]).FirstOrDefault(v => v != null);
            return GetParquetType(sample);
        }).ToArray();
    }

    private static void WriteParquetFile(string filePath, IReadOnlyList<string> columns,
        List<Row> rows, Type[] clrTypes)
    {
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
