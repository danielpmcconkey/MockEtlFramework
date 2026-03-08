using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

/// <summary>
/// V4 External module for PeakTransactionTimes.
/// Justified: The framework's CsvFileWriter {row_count} placeholder uses OUTPUT row count
/// (number of hourly buckets), but V1 trailer uses INPUT transaction count. An External module
/// is required to produce the correct trailer. The aggregation logic itself is handled by the
/// upstream SQL Transformation (AP6 eliminated).
/// </summary>
public class PeakTransactionTimesWriterV4 : IExternalStep
{
    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var hourlyAggregation = sharedState.ContainsKey("hourly_aggregation")
            ? sharedState["hourly_aggregation"] as DataFrame
            : null;

        var transactions = sharedState.ContainsKey("transactions")
            ? sharedState["transactions"] as DataFrame
            : null;

        var maxDate = (DateOnly)sharedState["__etlEffectiveDate"];
        var dateStr = maxDate.ToString("yyyy-MM-dd");

        // Input count for trailer (pre-aggregation transaction count)
        int inputCount = transactions?.Count ?? 0;

        // Build output rows from the SQL-aggregated data, adding ifw_effective_date
        var outputColumns = new List<string> { "hour_of_day", "txn_count", "total_amount", "ifw_effective_date" };
        var outputRows = new List<Row>();

        if (hourlyAggregation != null)
        {
            foreach (var row in hourlyAggregation.Rows)
            {
                outputRows.Add(new Row(new Dictionary<string, object?>
                {
                    ["hour_of_day"] = row["hour_of_day"],
                    ["txn_count"] = row["txn_count"],
                    // Store as decimal and format to 2dp when writing
                    ["total_amount"] = Convert.ToDecimal(row["total_amount"]),
                    ["ifw_effective_date"] = dateStr
                }));
            }
        }

        // Write CSV directly with correct trailer
        WriteCsv(outputRows, outputColumns, inputCount, dateStr);

        // Return empty DataFrame to framework (mirrors V1 behavior)
        sharedState["output"] = new DataFrame(new List<Row>(), outputColumns);
        return sharedState;
    }

    private static void WriteCsv(List<Row> rows, List<string> columns, int inputCount, string dateStr)
    {
        var solutionRoot = GetSolutionRoot();
        var outputPath = Path.Combine(solutionRoot, "Output", "double_secret_curated",
            "peak_transaction_times", "peak_transaction_times", dateStr, "peak_transaction_times.csv");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        using var writer = new StreamWriter(outputPath, append: false);
        writer.NewLine = "\n";

        // Header
        writer.WriteLine(string.Join(",", columns));

        // Data rows
        foreach (var row in rows)
        {
            var values = columns.Select(c => FormatField(c, row[c])).ToArray();
            writer.WriteLine(string.Join(",", values));
        }

        // Trailer with INPUT count (not output row count)
        writer.WriteLine($"TRAILER|{inputCount}|{dateStr}");
    }

    private static string FormatField(string columnName, object? value)
    {
        if (value == null) return "";

        // total_amount must be formatted with exactly 2 decimal places to match V1
        if (columnName == "total_amount" && value is decimal d)
        {
            return d.ToString("F2");
        }

        var s = value.ToString() ?? "";
        // RFC 4180: quote fields containing commas, double quotes, or newlines
        if (s.Contains(',') || s.Contains('"') || s.Contains('\n') || s.Contains('\r'))
        {
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        }
        return s;
    }

    private static string GetSolutionRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (dir.GetFiles("*.sln").Length > 0) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Solution root not found");
    }
}
