namespace Lib.DataFrames;


// Helper class for grouped operations
public class GroupedDataFrame
{
    private readonly DataFrame _dataFrame;
    private readonly string[] _groupColumns;

    internal GroupedDataFrame(DataFrame dataFrame, string[] groupColumns)
    {
        _dataFrame = dataFrame;
        _groupColumns = groupColumns;
    }

    /// <summary>
    /// Count rows in each group
    /// </summary>
    public DataFrame Count()
    {
        var groups = _dataFrame.Rows.GroupBy(row => 
            string.Join("|", _groupColumns.Select(col => row[col]?.ToString() ?? "")));

        var resultColumns = _groupColumns.Concat(new[] { "count" }).ToList();
        var resultRows = groups.Select(group =>
        {
            var data = new Dictionary<string, object?>();
            var firstRow = group.First();
            foreach (var col in _groupColumns)
            {
                data[col] = firstRow[col];
            }
            data["count"] = group.Count();
            return new Row(data);
        });

        return new DataFrame(resultRows, resultColumns);
    }

    /// <summary>
    /// Apply aggregation functions
    /// </summary>
    public DataFrame Agg(Dictionary<string, string> aggregations)
    {
        var groups = _dataFrame.Rows.GroupBy(row => 
            string.Join("|", _groupColumns.Select(col => row[col]?.ToString() ?? "")));

        var resultColumns = _groupColumns.Concat(aggregations.Keys.Select(k => $"{aggregations[k]}({k})")).ToList();
        var resultRows = groups.Select(group =>
        {
            var data = new Dictionary<string, object?>();
            var firstRow = group.First();
            
            // Add group columns
            foreach (var col in _groupColumns)
            {
                data[col] = firstRow[col];
            }
            
            // Add aggregations
            foreach (var agg in aggregations)
            {
                var columnName = agg.Key;
                var aggFunction = agg.Value.ToLower();
                var columnValues = group.Select(r => r[columnName]).Where(v => v != null);
                
                object? result = aggFunction switch
                {
                    "sum" => columnValues.Select(v => Convert.ToDecimal(v)).Sum(),
                    "avg" or "mean" => columnValues.Select(v => Convert.ToDecimal(v)).Average(),
                    "min" => columnValues.Min(),
                    "max" => columnValues.Max(),
                    "count" => columnValues.Count(),
                    _ => throw new ArgumentException($"Unknown aggregation function: {aggFunction}")
                };
                
                data[$"{aggFunction}({columnName})"] = result;
            }
            
            return new Row(data);
        });

        return new DataFrame(resultRows, resultColumns);
    }
}