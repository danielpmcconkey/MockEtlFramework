using System.Text;
using Lib.DataFrames;

namespace Lib.Modules;

/// <summary>
/// Writes a named DataFrame from shared state to a CSV file.
/// Supports optional trailer lines with token substitution.
/// Uses UTF-8 (no BOM), LF line endings, and RFC 4180 quoting.
/// </summary>
public class CsvFileWriter : IModule
{
    private readonly string _sourceDataFrameName;
    private readonly string _outputFilePath;
    private readonly bool _includeHeader;
    private readonly string? _trailerFormat;
    private readonly WriteMode _writeMode;

    public CsvFileWriter(string sourceDataFrameName, string outputFilePath,
        bool includeHeader = true, string? trailerFormat = null,
        WriteMode writeMode = WriteMode.Overwrite)
    {
        _sourceDataFrameName = sourceDataFrameName ?? throw new ArgumentNullException(nameof(sourceDataFrameName));
        _outputFilePath = outputFilePath ?? throw new ArgumentNullException(nameof(outputFilePath));
        _includeHeader = includeHeader;
        _trailerFormat = trailerFormat;
        _writeMode = writeMode;
    }

    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        if (!sharedState.TryGetValue(_sourceDataFrameName, out var value) || value is not DataFrame df)
            throw new KeyNotFoundException($"DataFrame '{_sourceDataFrameName}' not found in shared state.");

        var resolvedPath = PathHelper.Resolve(_outputFilePath);
        var parentDir = Path.GetDirectoryName(resolvedPath);
        if (parentDir != null)
            Directory.CreateDirectory(parentDir);

        var append = _writeMode == WriteMode.Append && File.Exists(resolvedPath);
        using var stream = new FileStream(resolvedPath, append ? FileMode.Append : FileMode.Create,
            FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\n" };

        if (_includeHeader && !append)
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
            var effectiveDate = sharedState.TryGetValue("__maxEffectiveDate", out var dateVal) && dateVal is DateOnly d
                ? d.ToString("yyyy-MM-dd")
                : "";
            var trailer = _trailerFormat
                .Replace("{row_count}", df.Count.ToString())
                .Replace("{date}", effectiveDate)
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
