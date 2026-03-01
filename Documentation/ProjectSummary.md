# MockEtlFramework -- Codebase Summary

**Purpose:** Orientation doc for understanding how this codebase works. For programme-level context (why this exists, POC results, strategy), see the AtcStrategy repo.

---

## Framework Architecture

The mock framework mirrors a production PySpark ETL system. Key concepts:

| Production Concept | C# Equivalent |
|---|---|
| PySpark DataFrame | `Lib.DataFrames.DataFrame` |
| Shared state (dict of DataFrames) | `Dictionary<string, object>` threaded through module chain |
| ETL modules | Classes implementing `IModule` |
| Job configuration | JSON files with ordered list of module configs |
| Spark SQL | In-memory SQLite via `Microsoft.Data.Sqlite` |
| Framework executor | `JobRunner` reads config, runs modules in sequence |

### Module Types

| Module | What It Does |
|---|---|
| **DataSourcing** | Reads from PostgreSQL datalake schema; injects effective dates via shared state keys `__minEffectiveDate` / `__maxEffectiveDate` |
| **Transformation** | Registers all DataFrames as SQLite tables, runs user-supplied SQL, stores result back in shared state |
| **DataFrameWriter** | Writes a DataFrame to a PostgreSQL curated schema; supports `Overwrite` (truncate+insert) and `Append` modes; configurable `targetSchema` parameter |
| **CsvFileWriter** | Writes DataFrame to CSV file; optional trailer (`{row_count}`, `{date}`, `{timestamp}`); configurable line endings (LF/CRLF) |
| **ParquetFileWriter** | Writes DataFrame to partitioned Parquet directory; configurable `numParts` |
| **External** | Loads a .NET assembly via reflection, instantiates a class implementing `IExternalStep`, delegates execution -- allows arbitrary C# logic |

### Execution Model

- `JobExecutorService` reads job registrations from `control.jobs`, builds a topological execution plan based on dependencies, and auto-advances each job from its last succeeded date forward to today
- Effective dates are injected into shared state automatically -- job configs never hardcode dates
- Dependency types: `SameDay` (upstream must succeed same run_date) and `Latest` (upstream must have ever succeeded)
- Data lake uses a full-load snapshot pattern: each day's load is a complete picture with an `as_of` column

### Database Layout

- **PostgreSQL** at localhost, user `dansdev`, database `atc`
- Password: hex-encoded UTF-16 LE in env var `PGPASS`; decode with `echo "$PGPASS" | xxd -r -p | iconv -f UTF-16LE -t UTF-8`
- **Schemas:** `datalake` (source, NEVER modify), `curated` (job output), `double_secret_curated` (agent rebuild output), `control` (job metadata and run history)

### Build & Run

```bash
dotnet build                                    # compile
dotnet test                                     # run xUnit tests
dotnet run --project JobExecutor                # auto-advance all active jobs
dotnet run --project JobExecutor -- JobName     # auto-advance one job
dotnet run --project JobExecutor -- 2024-10-15  # backfill specific date, all jobs
dotnet run --project JobExecutor -- 2024-10-15 JobName  # backfill specific date, one job
```

**Detailed architecture:** `Documentation/Architecture.md`

---

## Project Structure

```
MockEtlFramework/
├── Lib/                        # Framework library
│   ├── DataFrames/             # DataFrame, Row, GroupedDataFrame
│   ├── Modules/                # IModule, DataSourcing, Transformation,
│   │                           #   DataFrameWriter, CsvFileWriter, ParquetFileWriter,
│   │                           #   External, IExternalStep, ModuleFactory
│   ├── Control/                # JobExecutorService, ExecutionPlan,
│   │                           #   ControlDb, JobRegistration, JobDependency
│   ├── ConnectionHelper.cs     # DB connection (decodes PGPASS)
│   ├── PathHelper.cs           # Resolves relative paths from solution root
│   ├── JobConf.cs              # JSON config model
│   └── JobRunner.cs            # Runs module chain
├── JobExecutor/                # Console app entry point
│   └── Jobs/                   # Job configuration JSON files (~100 jobs)
├── ExternalModules/            # User-supplied C# processors implementing IExternalStep
├── Lib.Tests/                  # xUnit tests
├── SQL/                        # SQL scripts (DDL, job registration)
├── Documentation/              # Codebase docs (THIS directory)
└── Output/                     # File writer output (CSV, Parquet)
    └── curated/                # Job output files
```

### PostgreSQL Connection Pattern

```bash
export PGPASSWORD=$(echo "$PGPASS" | xxd -r -p | iconv -f UTF-16LE -t UTF-8) && \
  psql -h localhost -U dansdev -d atc -c "SELECT ..."
```

### Job Registration

```sql
INSERT INTO control.jobs (job_name, description, job_conf_path, is_active)
VALUES ('SomeJob', 'Description', 'JobExecutor/Jobs/some_job.json', true)
ON CONFLICT (job_name) DO NOTHING;
```


