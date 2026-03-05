namespace Lib;

/// <summary>
/// Shared utility for scanning date-partitioned directories.
/// Extracted from CsvFileWriter so all file writers can use it.
/// </summary>
public static class DatePartitionHelper
{
    /// <summary>
    /// Scans a job directory for date-named subdirectories and returns the latest one.
    /// </summary>
    public static string? FindLatestPartition(string jobDir)
    {
        if (!Directory.Exists(jobDir))
            return null;

        return Directory.GetDirectories(jobDir)
            .Select(d => Path.GetFileName(d))
            .Where(name => DateOnly.TryParseExact(name, "yyyy-MM-dd", out _))
            .OrderByDescending(name => name)
            .FirstOrDefault();
    }
}
