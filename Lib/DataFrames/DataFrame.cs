using System.Text.Json;

namespace Lib.DataFrames;

public class DataFrame
{
    private readonly List<Row> _rows;
    private readonly List<string> _columns;

    public DataFrame(IEnumerable<Row> rows, IEnumerable<string> columns)
    {
        _rows = rows.ToList();
        _columns = columns.ToList();
    }

    public DataFrame(IEnumerable<Dictionary<string, object?>> data)
    {
        var dataList = data.ToList();
        if (dataList.Count == 0)
        {
            _rows = new List<Row>();
            _columns = new List<string>();
        }
        else
        {
            _columns = dataList.First().Keys.ToList();
            _rows = dataList.Select(d => new Row(d)).ToList();
        }
    }

    // Properties
    public int Count => _rows.Count;
    public IReadOnlyList<string> Columns => _columns.AsReadOnly();
    public IReadOnlyList<Row> Rows => _rows.AsReadOnly();

    // Core DataFrames operations

    /// <summary>
    /// Select specific columns
    /// </summary>
    public DataFrame Select(params string[] columns)
    {
        var selectedColumns = columns.ToList();
        var newRows = _rows.Select(row => 
        {
            var newData = new Dictionary<string, object?>();
            foreach (var col in selectedColumns)
            {
                newData[col] = row[col];
            }
            return new Row(newData);
        });
        
        return new DataFrame(newRows, selectedColumns);
    }

    /// <summary>
    /// Filter rows based on a predicate
    /// </summary>
    public DataFrame Filter(Func<Row, bool> predicate)
    {
        return new DataFrame(_rows.Where(predicate), _columns);
    }

    /// <summary>
    /// Add or update a column
    /// </summary>
    public DataFrame WithColumn(string columnName, Func<Row, object?> valueGenerator)
    {
        var newColumns = _columns.ToList();
        if (!newColumns.Contains(columnName))
        {
            newColumns.Add(columnName);
        }

        var newRows = _rows.Select(row =>
        {
            var newData = row.ToDictionary();
            newData[columnName] = valueGenerator(row);
            return new Row(newData);
        });

        return new DataFrame(newRows, newColumns);
    }

    /// <summary>
    /// Remove a column
    /// </summary>
    public DataFrame Drop(string columnName)
    {
        var newColumns = _columns.Where(c => c != columnName).ToList();
        var newRows = _rows.Select(row =>
        {
            var newData = row.ToDictionary();
            newData.Remove(columnName);
            return new Row(newData);
        });

        return new DataFrame(newRows, newColumns);
    }

    /// <summary>
    /// Group by columns for aggregation
    /// </summary>
    public GroupedDataFrame GroupBy(params string[] columns)
    {
        return new GroupedDataFrame(this, columns);
    }

    /// <summary>
    /// Order by column
    /// </summary>
    public DataFrame OrderBy(string columnName, bool ascending = true)
    {
        var orderedRows = ascending 
            ? _rows.OrderBy(row => row[columnName])
            : _rows.OrderByDescending(row => row[columnName]);
        
        return new DataFrame(orderedRows, _columns);
    }

    /// <summary>
    /// Get first n rows
    /// </summary>
    public DataFrame Limit(int n)
    {
        return new DataFrame(_rows.Take(n), _columns);
    }

    /// <summary>
    /// Join with another DataFrames
    /// </summary>
    public DataFrame Join(DataFrame other, string onColumn, string joinType = "inner")
    {
        var joinedRows = new List<Row>();
        var allColumns = _columns.Union(other._columns.Where(c => c != onColumn)).ToList();

        foreach (var leftRow in _rows)
        {
            var matchingRightRows = other._rows.Where(rightRow => 
                Equals(leftRow[onColumn], rightRow[onColumn])).ToList();

            if (matchingRightRows.Any())
            {
                foreach (var rightRow in matchingRightRows)
                {
                    var joinedData = leftRow.ToDictionary();
                    foreach (var col in other._columns.Where(c => c != onColumn))
                    {
                        joinedData[col] = rightRow[col];
                    }
                    joinedRows.Add(new Row(joinedData));
                }
            }
            else if (joinType.ToLower() == "left")
            {
                var joinedData = leftRow.ToDictionary();
                foreach (var col in other._columns.Where(c => c != onColumn))
                {
                    joinedData[col] = null;
                }
                joinedRows.Add(new Row(joinedData));
            }
        }

        return new DataFrame(joinedRows, allColumns);
    }

    /// <summary>
    /// Union with another DataFrames
    /// </summary>
    public DataFrame Union(DataFrame other)
    {
        if (!_columns.SequenceEqual(other._columns))
        {
            throw new ArgumentException("DataFrames must have the same columns for union");
        }

        var allRows = _rows.Concat(other._rows);
        return new DataFrame(allRows, _columns);
    }

    /// <summary>
    /// Get distinct rows
    /// </summary>
    public DataFrame Distinct()
    {
        var distinctRows = _rows
            .GroupBy(row => string.Join("|", _columns.Select(col => row[col]?.ToString() ?? "")))
            .Select(g => g.First());
        
        return new DataFrame(distinctRows, _columns);
    }

    // Display and output methods

    /// <summary>
    /// Show the DataFrames content
    /// </summary>
    public void Show(int numRows = 20, bool truncate = true)
    {
        Console.WriteLine($"+{string.Join("+", _columns.Select(c => new string('-', Math.Max(c.Length, 10) + 2)))}+");
        Console.WriteLine($"|{string.Join("|", _columns.Select(c => $" {c.PadRight(Math.Max(c.Length, 10))} "))}|");
        Console.WriteLine($"+{string.Join("+", _columns.Select(c => new string('-', Math.Max(c.Length, 10) + 2)))}+");

        foreach (var row in _rows.Take(numRows))
        {
            var values = _columns.Select(col =>
            {
                var value = row[col]?.ToString() ?? "null";
                var maxLength = Math.Max(col.Length, 10);
                if (truncate && value.Length > maxLength)
                {
                    value = value.Substring(0, maxLength - 3) + "...";
                }
                return $" {value.PadRight(maxLength)} ";
            });
            Console.WriteLine($"|{string.Join("|", values)}|");
        }

        Console.WriteLine($"+{string.Join("+", _columns.Select(c => new string('-', Math.Max(c.Length, 10) + 2)))}+");
        
        if (_rows.Count > numRows)
        {
            Console.WriteLine($"only showing top {numRows} rows");
        }
    }

    /// <summary>
    /// Get schema information
    /// </summary>
    public void PrintSchema()
    {
        Console.WriteLine("root");
        foreach (var column in _columns)
        {
            var sampleValue = _rows.FirstOrDefault()?[column];
            var dataType = sampleValue?.GetType().Name ?? "null";
            Console.WriteLine($" |-- {column}: {dataType} (nullable = true)");
        }
    }

    /// <summary>
    /// Convert to JSON
    /// </summary>
    public string ToJson()
    {
        var data = _rows.Select(row => row.ToDictionary());
        return JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
    }

    // Static factory methods

    /// <summary>
    /// Create DataFrames from a list of objects
    /// </summary>
    public static DataFrame FromObjects<T>(IEnumerable<T> objects) where T : class
    {
        var objectsList = objects.ToList();
        if (!objectsList.Any())
        {
            return new DataFrame(new List<Row>(), new List<string>());
        }

        var properties = typeof(T).GetProperties();
        var columns = properties.Select(p => p.Name).ToList();
        
        var rows = objectsList.Select(obj =>
        {
            var data = new Dictionary<string, object?>();
            foreach (var prop in properties)
            {
                data[prop.Name] = prop.GetValue(obj);
            }
            return new Row(data);
        });

        return new DataFrame(rows, columns);
    }

    /// <summary>
    /// Create DataFrames from CSV file
    /// </summary>
    public static DataFrame FromCsv(string filePath, bool header = true, string separator = ",")
    {
        var lines = File.ReadAllLines(filePath);
        if (!lines.Any())
        {
            return new DataFrame(new List<Row>(), new List<string>());
        }

        var columns = header 
            ? lines[0].Split(separator)
            : Enumerable.Range(0, lines[0].Split(separator).Length).Select(i => $"col_{i}").ToArray();

        var dataLines = header ? lines.Skip(1) : lines;
        var rows = dataLines.Select(line =>
        {
            var values = line.Split(separator);
            var data = new Dictionary<string, object?>();
            for (int i = 0; i < columns.Length && i < values.Length; i++)
            {
                data[columns[i]] = values[i];
            }
            return new Row(data);
        });

        return new DataFrame(rows, columns);
    }
}