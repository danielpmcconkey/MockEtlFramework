# MockEtlFramework — Solution Strategy

## Purpose

This project is a C# proof-of-concept that mirrors the behavior of a production ETL Framework built on PySpark/Python. The production framework is a core platform component of a large big data system. The goal here is to replicate its execution model and module structure in C#, enabling further work that will be described once the core framework is established.

---

## Production ETL Framework — Background

The production ETL Framework operates by reading **job configuration files** (JSON). Each job conf contains a serialized, ordered list of **ETL modules** to execute in series. Modules communicate with one another through a **shared state** — a dictionary of named DataFrames that each module can read from and write to.

### Production Modules

| Module | Responsibility |
|---|---|
| **Data Sourcing** | Reads from the data lake. Users specify tables, columns, date ranges, and additional filters. Returns data as a PySpark DataFrame stored in shared state. |
| **Transformation** | Runs Spark SQL against DataFrames already in shared state, producing a new transformed DataFrame that is added back to shared state. |
| **DataFrame Writer** | Writes a named DataFrame from shared state out to a curation space for downstream consumers. |
| **External** | Executes a custom user-supplied Python class, allowing teams to perform any logic not covered by the standard modules. |

---

## This Project's Approach

Since the owner of this project is more comfortable in C# than Python, the ETL Framework execution model is being reproduced in C#. The core concepts map as follows:

| Production Concept | C# Equivalent |
|---|---|
| PySpark DataFrame | Custom `DataFrame` class (`Lib.DataFrames.DataFrame`) |
| Shared state | `Dictionary<string, object>` passed through module chain |
| ETL module | Classes implementing `IModule` interface |
| Job configuration | JSON file deserialized into an ordered list of module configs |
| Spark SQL | In-memory SQLite connection (Microsoft.Data.Sqlite) |
| Framework executor | `JobRunner` reads the job conf and runs modules in sequence |

### Key Design Principles

- **Module chain execution:** Modules run in the order defined by the job conf. Each module receives the current shared state, performs its operation, and returns the updated state.
- **Shared state as the integration contract:** Modules are decoupled from one another. They communicate only through named entries in the shared state dictionary.
- **JSON-driven configuration:** Job behavior is defined externally in JSON, not in code. This keeps the framework generic and reusable across many different jobs.
- **DataFrame as a first-class citizen:** The custom `DataFrame` class provides PySpark-like operations (Select, Filter, WithColumn, GroupBy, Join, Union, Distinct, OrderBy, Limit, etc.) so that transformation logic feels familiar to users of the production system.
- **Full-load temporal data:** The data lake follows a snapshot (full-load) pattern. Each day's load is a complete picture of a table's state at that point in time, identified by an `ifw_effective_date` date column. `DataSourcing` returns a flat DataFrame that includes the `ifw_effective_date` column, allowing consumers to work across date ranges in a single DataFrame without requiring special date-loop logic in the framework itself.
- **Explicit effective date management:** The caller MUST supply an effective date for every invocation. There is no auto-advance or gap-fill logic — the executor runs exactly one effective date per invocation. Effective dates are injected into shared state before the pipeline runs and picked up automatically by `DataSourcing`. For batch processing across date ranges, use the task queue (`control.task_queue`) with one row per (job, date) pair.
- **run_date vs. effective date:** `run_date` in `control.job_runs` is the calendar date the executor actually ran (always today). `min_effective_date` / `max_effective_date` record which data date that run processed. These are separate concerns.

---

## Solution Structure

### `Lib` (Class Library)

The core framework library. Referenced by both the job executor and the test project.

#### `Lib.DataFrames`

| Class | Purpose |
|---|---|
| `DataFrame` | Immutable, in-memory tabular data structure with a PySpark-inspired fluent API. Constructed via `DataFrame.FromObjects<T>()`, from raw column/row data, or from files via static factories: `FromCsv(filePath)` (reads a CSV file), `FromCsvLines(string[] lines)` (parses pre-read CSV lines — used when callers need to strip trailers before parsing), and `FromParquet(directoryPath)` (reads all `*.parquet` files in a directory). An empty DataFrame can be created with schema only via `new DataFrame(IEnumerable<string> columns)`. |
| `Row` | Dictionary-backed row abstraction used by `DataFrame`. Values are `object?`, accessed by column name. |
| `GroupedDataFrame` | Intermediate result of `DataFrame.GroupBy(...)`. Supports aggregation operations (`Count()`, with room to add `Sum`, `Avg`, etc.). |

#### `Lib.Modules`

| Class | Purpose |
|---|---|
| `IModule` | Core module interface: `Execute(Dictionary<string, object> sharedState) → Dictionary<string, object>`. All modules implement this contract. |
| `DataSourcing` | Queries a PostgreSQL data lake schema for a specified table, column list, and effective date range. Returns a single flat `DataFrame` with `ifw_effective_date` appended as a column (skipped if the caller already includes it). Supports an optional `additionalFilter` clause. Date resolution supports five mutually exclusive modes (validated at construction): (1) static dates via `minEffectiveDate`/`maxEffectiveDate` in config, (2) `lookbackDays: N` — pulls T-N through T-0 relative to `__etlEffectiveDate`, (3) `mostRecentPrior: true` — queries the datalake for the latest `ifw_effective_date` strictly before T-0 (handles weekends/gaps), (4) `mostRecent: true` — queries the datalake for the latest `ifw_effective_date` on or before T-0 (inclusive), (5) no date fields — both min and max fall back to `__etlEffectiveDate`. For modes 3 and 4, if the datalake query finds no matching date, `Execute` returns an empty `DataFrame` with the correct column schema (including `ifw_effective_date`) instead of throwing. |
| `Transformation` | Opens an in-memory SQLite connection, registers every `DataFrame` in the current shared state as a SQLite table, executes user-supplied free-form SQL, and stores the result `DataFrame` back into shared state under a caller-specified result name. Empty DataFrames (those with columns but zero rows) are registered as schema-only SQLite tables — the `CREATE TABLE` runs but no inserts are performed, so downstream SQL can still reference them in joins/subqueries without error. |
| `DataFrameWriter` | Writes a named `DataFrame` from shared state to a PostgreSQL curation schema. Auto-creates the target table if it does not exist (type inference from sample values). Supports `Overwrite` mode (truncate then insert) and `Append` mode (insert only). All writes are transaction-wrapped. |
| `External` | Loads a user-supplied .NET assembly from disk via reflection, locates a named type that implements `IExternalStep`, instantiates it, and delegates `Execute`. Allows teams to inject arbitrary C# logic into any job pipeline without modifying the framework. |
| `ParquetFileWriter` | Writes a named `DataFrame` from shared state to a directory of Parquet files (`part-00000.parquet`, `part-00001.parquet`, etc.). Output path: `{outputDirectory}/{jobDirName}/{outputTableDirName}/{etl_effective_date}/{fileName}/part-N.parquet`. Supports configurable part count and `Overwrite`/`Append` write modes. Uses Parquet.Net (pure managed C#). Output paths are relative to the solution root. |
| `CsvFileWriter` | Writes a named `DataFrame` from shared state to a date-partitioned CSV file. Output path: `{outputDirectory}/{jobDirName}/{outputTableDirName}/{etl_effective_date}/{fileName}`. Injects an `etl_effective_date` column (the current effective date string) into every row before writing. UTF-8 (no BOM), configurable line endings (LF or CRLF), RFC 4180 quoting. Supports optional trailer lines with token substitution (`{row_count}`, `{date}`, `{timestamp}`). In **Append** mode, the writer reads the prior partition's CSV (located via `DatePartitionHelper`), strips the trailing record when `trailerFormat` is set (using `File.ReadAllLines` + `lines[..^1]` + `DataFrame.FromCsvLines` instead of `DataFrame.FromCsv` to prevent the prior trailer from being parsed as a data row), drops the prior `etl_effective_date` column, and unions the prior data with the current DataFrame before writing. Output paths are relative to the solution root. |
| `IExternalStep` | Interface that external assemblies must implement to be callable via the `External` module. |
| `ModuleFactory` | Static factory. Reads the `type` discriminator field from a `JsonElement` and instantiates the appropriate `IModule` implementation. Throws `InvalidOperationException` on unknown types and propagates `KeyNotFoundException` if the `type` field is absent. |

#### `Lib` (root)

| Class | Purpose |
|---|---|
| `ConnectionHelper` | Internal static helper that builds a Npgsql connection string. Decodes the Postgres password from a hex-encoded UTF-16 LE environment variable (`PGPASS`). |
| `DatePartitionHelper` | Shared utility for scanning date-partitioned output directories. `FindLatestPartition(dir)` returns the latest `yyyy-MM-dd`-named subdirectory. Used by both `CsvFileWriter` and `ParquetFileWriter` for append-mode prior-partition lookups (called on the table-level directory that contains date partitions). |
| `PathHelper` | Internal static helper that resolves relative output paths against the solution root directory. Walks up from `AppContext.BaseDirectory` to find the `.sln` file. Used by file writer modules. |
| `JobConf` | JSON deserialization model. Contains the job name, an optional `firstEffectiveDate` (metadata — not used by the executor), and an ordered `List<JsonElement>` of module configurations. |
| `JobRunner` | Deserializes a job conf from a JSON file path, iterates the module list, creates each module via `ModuleFactory`, and threads shared state through the pipeline. Accepts an optional `initialState` dictionary pre-populated by the executor (used to inject effective dates). Logs progress to the console. |

#### `Lib.Control`

Orchestration layer that sits above `JobRunner`. Reads job registrations and dependency graph from the PostgreSQL `control` schema, determines which jobs need to run, executes them in the correct order, and records the outcome of every run attempt.

| Class | Purpose |
|---|---|
| `JobRegistration` | Model for a `control.jobs` row — job ID, name, description, conf path, and active flag. |
| `JobDependency` | Model for a `control.job_dependencies` row — downstream job ID, upstream job ID, and dependency type (`SameDay` or `Latest`). |
| `ControlDb` | Internal static data-access layer for the control schema. Reads: `GetActiveJobs`, `GetAllDependencies`, `GetSucceededJobIds`, `GetEverSucceededJobIds`, `GetLastSucceededMaxEffectiveDate`, `GetNextAttemptNumber` (keyed by effective date range). Writes: `InsertRun` (records `run_date`, `min_effective_date`, `max_effective_date`), `MarkRunning`, `MarkSucceeded`, `MarkFailed`, `MarkSkipped`. |
| `ExecutionPlan` | Internal static class that applies Kahn's topological sort to produce an ordered run list. Only unsatisfied dependency edges are counted: `SameDay` edges are always treated as unsatisfied (checked at execution time); a `Latest` edge is satisfied if the upstream job has ever succeeded. Throws `InvalidOperationException` on cycle detection. |
| `JobExecutorService` | Public orchestrator. Requires an explicit effective date — no auto-advance or gap-fill. Loads jobs and dependencies, builds the execution plan, injects the effective date into shared state, and runs each pipeline through `JobRunner`. Records `Pending -> Running -> Succeeded / Failed` in `control.job_runs`. Failed jobs' `SameDay` dependents are recorded as `Skipped`. Accepts a required `effectiveDate` and optional `specificJobName`. |
| `TaskQueueService` | Long-running queue-based executor. Polls `control.task_queue` for pending tasks and executes them across 5 threads (4 parallel + 1 serial). Each thread has its own DB connection. Task claim uses `FOR UPDATE SKIP LOCKED` to prevent races. Exits when all threads find an empty queue. No SIGINT handler — on kill, `try/finally` marks in-flight tasks as Failed. |
| `TaskQueueItem` | Internal model for a claimed task from the queue — task ID, job name, effective date, execution mode. |

**Dependency types:**

| Type | Semantics |
|---|---|
| `SameDay` | The upstream job must have a `Succeeded` run with the same `run_date` (execution date) as the downstream job. Within a single executor invocation jobs run in topological order, so this is satisfied naturally when A precedes B in the plan. |
| `Latest` | The upstream job must have succeeded at least once for any `run_date`. Used for one-time setup jobs or slowly-changing reference data that only needs to be current, not date-aligned. |

---

### `JobExecutor` (Console Application)

Entry point for running jobs from the command line.

```
JobExecutor --service                         # long-running queue executor (polls control.task_queue)
JobExecutor <effective_date>                  # run all active jobs for that date
JobExecutor <effective_date> <job_name>       # run one job for that date
```

An effective date argument (format: `yyyy-MM-dd`) is **required** for non-service invocations. **`--service` mode** delegates to `TaskQueueService`. All other modes delegate to `JobExecutorService`. `run_date` is always set to today internally and is never a CLI argument.

#### Queue Executor (`--service`)

The queue executor is a long-running process that polls `control.task_queue` for pending tasks. It eliminates dotnet startup overhead by paying the JIT cost once, and parallelizes work across 5 threads.

**Threading model:**
- 4 threads poll for `execution_mode = 'parallel'` tasks
- 1 thread polls for `execution_mode = 'serial'` tasks
- Each thread has its own DB connection (Npgsql is not thread-safe)
- Task claim uses `FOR UPDATE SKIP LOCKED` (Postgres row-level locking)

**Lifecycle:** Start the executor, populate the queue via SQL, executor picks up work automatically. When all threads find an empty queue, the service exits.

**Queue population example:**
```sql
INSERT INTO control.task_queue (job_name, effective_date, execution_mode)
SELECT j.job_name, d.dt::date, 'parallel'
FROM control.jobs j
CROSS JOIN generate_series('2024-10-01'::date, '2024-12-31'::date, '1 day') d(dt)
WHERE j.is_active = true
ORDER BY d.dt, j.job_name;
```

**Monitoring:**
```sql
SELECT status, COUNT(*) FROM control.task_queue GROUP BY status;
```

**Sample job configuration** (`JobExecutor/Jobs/customer_account_summary.json`, registered in `control.jobs`):
```json
{
  "jobName": "CustomerAccountSummary",
  "firstEffectiveDate": "2024-10-01",
  "modules": [
    {
      "type": "DataSourcing",
      "resultName": "customers",
      "schema": "datalake",
      "table": "customers",
      "columns": ["id", "first_name", "last_name"]
    },
    {
      "type": "DataSourcing",
      "resultName": "accounts",
      "schema": "datalake",
      "table": "accounts",
      "columns": ["account_id", "customer_id", "account_type", "account_status", "current_balance"]
    },
    {
      "type": "Transformation",
      "resultName": "customer_account_summary",
      "sql": "SELECT c.id AS customer_id, c.first_name, c.last_name, COUNT(a.account_id) AS account_count, ROUND(SUM(CASE WHEN a.account_status = 'Active' THEN a.current_balance ELSE 0 END), 2) AS active_balance FROM customers c LEFT JOIN accounts a ON c.id = a.customer_id GROUP BY c.id, c.first_name, c.last_name ORDER BY c.id"
    },
    {
      "type": "DataFrameWriter",
      "source": "customer_account_summary",
      "targetTable": "customer_account_summary",
      "writeMode": "Overwrite"
    }
  ]
}
```

Note that no date fields appear in the `DataSourcing` modules above — the executor injects `__etlEffectiveDate` at runtime via shared state, and both min and max default to that value. Other date modes are available:

```json
// Lookback: pull T-3 through T-0 (4 days inclusive)
{ "type": "DataSourcing", "lookbackDays": 3, ... }

// Most recent prior: query datalake for latest date strictly before T-0
{ "type": "DataSourcing", "mostRecentPrior": true, ... }

// Most recent: query datalake for latest date on or before T-0
{ "type": "DataSourcing", "mostRecent": true, ... }
```

Date modes are mutually exclusive — mixing `lookbackDays`, `mostRecentPrior`, `mostRecent`, or static dates throws at construction time. For `mostRecentPrior` and `mostRecent`, if no matching date exists in the datalake, the module stores an empty DataFrame with the correct column schema rather than throwing.

---

### `Lib.Tests` (xUnit Test Project)

Unit test coverage for framework components. Tests do not require a live database — all DataFrame and Transformation tests operate entirely in memory.

| Test Class | Coverage |
|---|---|
| `DataFrameTests` | `DataFrame` API — Count, Columns, Select, Filter, WithColumn, Drop, OrderBy, Limit, Union, Distinct, Join (inner + left), GroupBy/Count |
| `TransformationTests` | `Transformation` module — basic SELECT, WHERE, column projection, JOIN across two DataFrames, GROUP BY aggregation, shared state preservation, non-DataFrame entries in state are silently ignored |
| `ModuleFactoryTests` | `ModuleFactory` — all module types, optional fields (`additionalFilter`, `lookbackDays`, `mostRecentPrior`), both write modes, mutually exclusive date mode validation (lookback+mostRecentPrior, lookback+static dates), unknown type error, missing type field error |
| `DataSourcingTests` | `DataSourcing` date resolution — lookback range calculation, zero-day lookback, static date passthrough, `__etlEffectiveDate` fallback, missing effective date errors, mutually exclusive mode validation (all conflict combinations), negative lookbackDays |

---

## Technology Choices

| Concern | Choice | Rationale |
|---|---|---|
| Language / runtime | C# / .NET 8 | Owner's primary language |
| PostgreSQL client | Npgsql 8 | Standard .NET Postgres driver |
| In-process SQL engine | Microsoft.Data.Sqlite 8 | Enables free-form SQL in `Transformation` without a running server; well-audited, Microsoft-supported |
| Test framework | xUnit | Industry standard for modern .NET; no test runner installation required |
| External assembly loading | `Assembly.LoadFrom` + reflection | Mirrors the production Python `importlib` pattern for user-supplied modules |
| Parquet file output | Parquet.Net 4.x | Pure managed C#, no native dependencies; simple column-oriented API maps cleanly to DataFrame |

---

## File Writer Modules

In addition to `DataFrameWriter` (which writes to PostgreSQL), the framework supports writing DataFrame output to files for file-to-file comparison workflows.

### Output Directory Convention

```
Output/
├── curated/                                 # V1 job outputs (legacy flat structure)
│   ├── some_job/
│   │   ├── part-00000.parquet
│   │   └── part-00001.parquet
│   └── ...
└── double_secret_curated/                   # V2 job outputs (date-partitioned)
    └── {jobDirName}/
        └── {outputTableDirName}/
            └── {etl_effective_date}/
                ├── output.csv               # CSV output
                └── output/                  # Parquet output dir
                    ├── part-00000.parquet
                    └── part-00001.parquet
```

The `Output/` directory is gitignored. All output paths in job configs are relative to the solution root.

### ParquetFileWriter

Writes a DataFrame to one or more Parquet part files in a directory. Uses Parquet.Net (pure managed C#, no native dependencies).

| JSON Property | Required | Default | Description |
|---|---|---|---|
| `type` | Yes | — | `"ParquetFileWriter"` |
| `source` | Yes | — | Name of the DataFrame in shared state |
| `outputDirectory` | Yes | — | Base directory path (relative to solution root) |
| `jobDirName` | Yes | — | Subdirectory name for the job |
| `outputTableDirName` | Yes | — | Subdirectory name for the output table (within the job directory) |
| `fileName` | Yes | — | Name of the parquet output directory within the date partition |
| `numParts` | No | `1` | Number of part files to split output across |
| `writeMode` | Yes | — | `"Overwrite"` or `"Append"` |

**Example:**
```json
{
  "type": "ParquetFileWriter",
  "source": "output",
  "outputDirectory": "Output/double_secret_curated",
  "jobDirName": "account_balance_snapshot",
  "outputTableDirName": "account_balance_snapshot",
  "fileName": "account_balance_snapshot",
  "numParts": 3,
  "writeMode": "Overwrite"
}
```

### CsvFileWriter

Writes a DataFrame to a date-partitioned CSV file. Output path: `{outputDirectory}/{jobDirName}/{outputTableDirName}/{etl_effective_date}/{fileName}`. Injects an `etl_effective_date` column into every row. UTF-8 encoding (no BOM), configurable line endings (LF or CRLF), RFC 4180 quoting rules.

| JSON Property | Required | Default | Description |
|---|---|---|---|
| `type` | Yes | — | `"CsvFileWriter"` |
| `source` | Yes | — | Name of the DataFrame in shared state |
| `outputDirectory` | Yes | — | Base directory (relative to solution root) |
| `jobDirName` | Yes | — | Subdirectory name under the base directory |
| `outputTableDirName` | Yes | — | Subdirectory name for the output table (within the job directory) |
| `fileName` | Yes | — | Name of the CSV file within the date partition |
| `includeHeader` | No | `true` | Whether to write a header row |
| `trailerFormat` | No | `null` | Trailer line format string (see below) |
| `writeMode` | Yes | — | `"Overwrite"` or `"Append"` |
| `lineEnding` | No | `"LF"` | Line ending style: `"LF"` or `"CRLF"` |

**Trailer tokens:** `{row_count}` (data rows, excluding header/trailer), `{date}` (effective date from `__etlEffectiveDate` in shared state), `{timestamp}` (UTC now, ISO 8601).

**Append mode trailer stripping:** When `writeMode` is `Append` and `trailerFormat` is set, the writer reads the prior partition's CSV as raw lines (`File.ReadAllLines`), strips the last line (the prior trailer) via `lines[..^1]`, and parses the remaining lines with `DataFrame.FromCsvLines` instead of `DataFrame.FromCsv`. This prevents the prior trailer from being carried forward as a data row. The prior partition's `etl_effective_date` column is dropped before unioning with the current DataFrame (since the column is re-injected with the current date).

**Vanilla CSV example:**
```json
{
  "type": "CsvFileWriter",
  "source": "output",
  "outputDirectory": "Output/double_secret_curated",
  "jobDirName": "customer_contact_info",
  "outputTableDirName": "customer_contact_info",
  "fileName": "customer_contact_info.csv",
  "writeMode": "Overwrite"
}
```

**CSV with trailer example:**
```json
{
  "type": "CsvFileWriter",
  "source": "output",
  "outputDirectory": "Output/double_secret_curated",
  "jobDirName": "daily_txn_summary",
  "outputTableDirName": "daily_txn_summary",
  "fileName": "daily_txn_summary.csv",
  "trailerFormat": "TRAILER|{row_count}|{date}",
  "writeMode": "Overwrite"
}
```

---

*This document will be updated as architectural decisions are made.*
