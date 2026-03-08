# DataFrames

Located in `Lib/DataFrames/`.

## DataFrame

`Lib/DataFrames/DataFrame.cs`

Immutable, in-memory tabular data structure with a PySpark-inspired fluent API.

### Construction

- `DataFrame.FromObjects<T>()` -- from a collection of typed objects
- `DataFrame.FromCsv(filePath)` -- reads a CSV file
- `DataFrame.FromCsvLines(string[] lines)` -- parses pre-read CSV lines (used when callers need to strip trailers before parsing)
- `DataFrame.FromParquet(directoryPath)` -- reads all `*.parquet` files in a directory
- `new DataFrame(IEnumerable<string> columns)` -- empty DataFrame with schema only
- Raw column/row data constructor

### Operations

| Method | Description |
|---|---|
| `Select(columns)` | Column projection |
| `Filter(predicate)` | Row filtering |
| `WithColumn(name, func)` | Add or replace a column |
| `Drop(column)` | Remove a column |
| `OrderBy(column)` | Sort rows |
| `Limit(n)` | Take first N rows |
| `Union(other)` | Combine two DataFrames |
| `Distinct()` | Remove duplicate rows |
| `Join(other, ..., type)` | Inner or left join |
| `GroupBy(columns)` | Returns a `GroupedDataFrame` |
| `Count` | Row count property |
| `Columns` | Column names |

## Row

`Lib/DataFrames/Row.cs`

Dictionary-backed row abstraction. Values are `object?`, accessed by column name.

## GroupedDataFrame

`Lib/DataFrames/GroupedDataFrame.cs`

Intermediate result of `DataFrame.GroupBy(...)`. Supports `Count()` aggregation (with room to add `Sum`, `Avg`, etc.).
