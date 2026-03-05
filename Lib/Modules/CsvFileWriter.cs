using System.Text;
using Lib.DataFrames;

namespace Lib.Modules;

/// <summary>
/// Writes a named DataFrame from shared state to a date-partitioned CSV file.
/// Output path: {outputDirectory}/{jobDirName}/{etl_effective_date}/{fileName}
/// Injects an etl_effective_date column into every row before writing.
/// Supports optional trailer lines with token substitution.
/// Uses UTF-8 (no BOM), configurable line endings (LF or CRLF), and RFC 4180 quoting.
/// </summary>
public class CsvFileWriter : IModule
{
    private readonly string _sourceDataFrameName;
    private readonly string _outputDirectory;
    private readonly string _jobDirName;
    private readonly string _fileName;
    private readonly bool _includeHeader;
    private readonly string? _trailerFormat;
    private readonly WriteMode _writeMode;
    private readonly string _lineEnding;

    public CsvFileWriter(string sourceDataFrameName, string outputDirectory,
        string jobDirName, string fileName,
        bool includeHeader = true, string? trailerFormat = null,
        WriteMode writeMode = WriteMode.Overwrite, string lineEnding = "\n")
    {
        _sourceDataFrameName = sourceDataFrameName ?? throw new ArgumentNullException(nameof(sourceDataFrameName));
        _outputDirectory = outputDirectory ?? throw new ArgumentNullException(nameof(outputDirectory));
        _jobDirName = jobDirName ?? throw new ArgumentNullException(nameof(jobDirName));
        _fileName = fileName ?? throw new ArgumentNullException(nameof(fileName));
        _includeHeader = includeHeader;
        _trailerFormat = trailerFormat;
        _writeMode = writeMode;
        _lineEnding = lineEnding ?? "\n";
    }

    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        if (!sharedState.TryGetValue(_sourceDataFrameName, out var value) || value is not DataFrame df)
            throw new KeyNotFoundException($"DataFrame '{_sourceDataFrameName}' not found in shared state.");

        if (!sharedState.TryGetValue(DataSourcing.EtlEffectiveDateKey, out var dateVal) || dateVal is not DateOnly effectiveDate)
            throw new InvalidOperationException(
                $"'{DataSourcing.EtlEffectiveDateKey}' not found or not a DateOnly in shared state.");

        var dateStr = effectiveDate.ToString("yyyy-MM-dd");
        var jobDir = Path.Combine(PathHelper.Resolve(_outputDirectory), _jobDirName);

        // Append mode: union with prior partition's data
        if (_writeMode == WriteMode.Append)
        {
            var priorDate = DatePartitionHelper.FindLatestPartition(jobDir);
            if (priorDate != null)
            {
                var priorPath = Path.Combine(jobDir, priorDate, _fileName);
                if (File.Exists(priorPath))
                {
                    var lines = File.ReadAllLines(priorPath);
                    if (_trailerFormat != null && lines.Length > 1)
                    {
                        lines = lines[..^1];
                    }
                    var priorDf = DataFrame.FromCsvLines(lines);
                    priorDf = priorDf.Drop("etl_effective_date");
                    df = priorDf.Union(df);
                }
            }
        }

        // Inject etl_effective_date column
        df = df.WithColumn("etl_effective_date", _ => dateStr);

        // Build date-partitioned output path
        var partitionDir = Path.Combine(jobDir, dateStr);
        Directory.CreateDirectory(partitionDir);
        var outputPath = Path.Combine(partitionDir, _fileName);

        using var stream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = _lineEnding };

        if (_includeHeader)
        {
            writer.WriteLine(string.Join(",", df.Columns));
        }

        foreach (var row in df.Rows)
        {
            var fields = df.Columns.Select(col => FormatField(row[col]));
            writer.WriteLine(string.Join(",", fields));
        }

        if (_trailerFormat != null)
        {
            var trailer = _trailerFormat
                .Replace("{row_count}", df.Count.ToString())
                .Replace("{date}", dateStr)
                .Replace("{timestamp}", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"));
            writer.WriteLine(trailer);
        }

        return sharedState;
    }

    /// <summary>
    /// RFC 4180: quote fields that contain commas, double quotes, or newlines.
    /// Nulls render as empty (bare comma).
    /// </summary>
    private static string FormatField(object? val)
    {
        if (val is null) return "";
        var s = val.ToString() ?? "";
        if (s.Contains(',') || s.Contains('"') || s.Contains('\n') || s.Contains('\r'))
        {
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        }
        return s;
    }
}
