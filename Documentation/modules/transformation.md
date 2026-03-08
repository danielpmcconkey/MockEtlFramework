# Transformation

`Lib/Modules/Transformation.cs`

Opens an in-memory SQLite connection, registers every `DataFrame` in the current shared state as a SQLite table, executes user-supplied free-form SQL, and stores the result `DataFrame` back into shared state under a caller-specified result name.

## SQLite Table Registration

All DataFrames in shared state are registered as SQLite tables at execution time. Non-DataFrame entries in shared state are silently ignored. This means any DataFrame produced by an earlier module in the chain is available as a SQL table name.

## Empty DataFrame Handling

Empty DataFrames (those with columns but zero rows) are registered as schema-only SQLite tables -- the `CREATE TABLE` runs but no inserts are performed. Downstream SQL can still reference them in joins/subqueries without error.

## Empty Result Handling

When the transformation SQL itself returns zero rows, `BuildDataFrame` constructs the result DataFrame using the query's column names rather than passing an empty row list. This preserves column schema for subsequent modules.

## Config Properties

| JSON Property | Required | Description |
|---|---|---|
| `type` | Yes | `"Transformation"` |
| `resultName` | Yes | Name to store the result DataFrame under in shared state |
| `sql` | Yes | Free-form SQL to execute against registered DataFrames |

## Example

```json
{
  "type": "Transformation",
  "resultName": "customer_account_summary",
  "sql": "SELECT c.id AS customer_id, c.first_name, c.last_name, COUNT(a.account_id) AS account_count FROM customers c LEFT JOIN accounts a ON c.id = a.customer_id GROUP BY c.id, c.first_name, c.last_name ORDER BY c.id"
}
```
