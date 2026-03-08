# DataFrameWriter

`Lib/Modules/DataFrameWriter.cs`

Writes a named `DataFrame` from shared state to a PostgreSQL curation schema. Auto-creates the target table if it does not exist (type inference from sample values). All writes are transaction-wrapped.

## Write Modes

| Mode | Behavior |
|---|---|
| `Overwrite` | Truncate then insert |
| `Append` | Insert only |

## Config Properties

| JSON Property | Required | Default | Description |
|---|---|---|---|
| `type` | Yes | -- | `"DataFrameWriter"` |
| `source` | Yes | -- | Name of the DataFrame in shared state |
| `targetTable` | Yes | -- | PostgreSQL table name |
| `targetSchema` | No | `"curated"` | PostgreSQL schema name |
| `writeMode` | Yes | -- | `"Overwrite"` or `"Append"` |

## Example

```json
{
  "type": "DataFrameWriter",
  "source": "customer_account_summary",
  "targetTable": "customer_account_summary",
  "writeMode": "Overwrite"
}
```
