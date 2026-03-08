# ParquetFileWriter

`Lib/Modules/ParquetFileWriter.cs`

Writes a named `DataFrame` from shared state to a directory of Parquet files. Uses Parquet.Net (pure managed C#, no native dependencies).

## Output Path

```
{outputDirectory}/{jobDirName}/{outputTableDirName}/{etl_effective_date}/{fileName}/part-NNNNN.parquet
```

Paths are relative to the solution root (resolved via `PathHelper`).

## Write Modes

| Mode | Behavior |
|---|---|
| `Overwrite` | Deletes existing Parquet files in the partition, then writes |
| `Append` | Reads prior partition via `DatePartitionHelper`, unions with current data, writes |

## Config Properties

| JSON Property | Required | Default | Description |
|---|---|---|---|
| `type` | Yes | -- | `"ParquetFileWriter"` |
| `source` | Yes | -- | Name of the DataFrame in shared state |
| `outputDirectory` | Yes | -- | Base directory (relative to solution root) |
| `jobDirName` | Yes | -- | Subdirectory name for the job |
| `outputTableDirName` | Yes | -- | Subdirectory name for the output table |
| `fileName` | Yes | -- | Name of the Parquet output directory within the date partition |
| `numParts` | No | `1` | Number of part files to split output across |
| `writeMode` | Yes | -- | `"Overwrite"` or `"Append"` |

## Example

```json
{
  "type": "ParquetFileWriter",
  "source": "output",
  "outputDirectory": "Output/poc4",
  "jobDirName": "account_balance_snapshot",
  "outputTableDirName": "account_balance_snapshot",
  "fileName": "account_balance_snapshot",
  "numParts": 3,
  "writeMode": "Overwrite"
}
```
