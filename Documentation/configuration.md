# Configuration

All configuration is managed through `AppConfig` (`Lib/AppConfig.cs`). Consumers read properties without knowing whether the value is a compiled default, an `appsettings.json` override, or an environment variable.

## AppConfig Sections

### PathSettings

| Property | Source | Default |
|---|---|---|
| `EtlRoot` | `ETL_ROOT` env var | `""` |
| `EtlReOutput` | `ETL_RE_OUTPUT` env var | `""` |

Cannot be set via `appsettings.json`.

### DatabaseSettings

| Property | Source | Default |
|---|---|---|
| `Host` | `appsettings.json` | `"localhost"` |
| `Username` | `appsettings.json` | `"claude"` |
| `Password` | `ETL_DB_PASSWORD` env var | `""` |
| `DatabaseName` | `appsettings.json` | `"atc"` |
| `Timeout` | `appsettings.json` | `15` |
| `CommandTimeout` | `appsettings.json` | `300` |

`Password` **cannot** be set via `appsettings.json` -- any `Password` key in the file is silently ignored. The app fails fast if no password is available.

### TaskQueueSettings

| Property | Source | Default |
|---|---|---|
| `ThreadCount` | `appsettings.json` | `5` |
| `PollIntervalMs` | `appsettings.json` | `5000` |
| `IdleCheckIntervalMs` | `appsettings.json` | `30000` |
| `MaxIdleCycles` | `appsettings.json` | `960` (960 x 30s = 8 hours) |

## Environment Variables

| Variable | Required | Purpose |
|---|---|---|
| `ETL_DB_PASSWORD` | Yes | Database password. App exits if missing. |
| `ETL_ROOT` | No | Solution root path override. Used by `PathHelper` for path resolution and `{ETL_ROOT}` token expansion. Falls back to `.sln` walk if unset. |
| `ETL_RE_OUTPUT` | No | RE output directory path. Used via `{ETL_RE_OUTPUT}` token expansion in `PathHelper`. |

All env vars are read once at `AppConfig` construction and cached for the process lifetime.

## appsettings.json

Located at `JobExecutor/appsettings.json`. Only overridden values need to appear -- defaults come from `AppConfig`.

```json
{
  "Database": {
    "Host": "localhost"
  },
  "TaskQueue": {
    "ThreadCount": 5,
    "PollIntervalMs": 5000,
    "IdleCheckIntervalMs": 30000,
    "MaxIdleCycles": 960
  }
}
```

## ConnectionHelper

`Lib/ConnectionHelper.cs`. Internal static helper that builds an Npgsql connection string from `AppConfig.Database` settings. Initialized at startup via `ConnectionHelper.Initialize(appConfig)`.

## PathHelper

`Lib/PathHelper.cs`. Public static helper that resolves output paths against the solution root. Initialized at startup via `PathHelper.Initialize(appConfig)`.

- Supports `{TOKEN}` expansion in paths (e.g., `{ETL_ROOT}`, `{ETL_RE_OUTPUT}`)
- Known tokens are populated from `AppConfig.Paths` at initialization, not from direct env var lookups
- Solution root resolution: first checks `AppConfig.Paths.EtlRoot`, then walks up from `AppContext.BaseDirectory` to find the `.sln` file

## DatePartitionHelper

`Lib/DatePartitionHelper.cs`. Shared utility for scanning date-partitioned output directories. Used by both `CsvFileWriter` and `ParquetFileWriter` for append-mode prior-partition lookups.

- `FindLatestPartition(dir)` — returns the latest `yyyy-MM-dd`-named subdirectory within the given directory
- Called on the table-level directory that contains date partitions
