# Overview

## Purpose

This project is a C# proof-of-concept that mirrors the behavior of a production ETL Framework built on PySpark/Python. The production framework is a core platform component of a large big data system. The goal is to replicate its execution model and module structure in C#, enabling further work (reverse engineering of production ETL jobs).

## Production ETL Framework

The production framework reads **job configuration files** (JSON). Each job conf contains a serialized, ordered list of **ETL modules** to execute in series. Modules communicate through a **shared state** -- a dictionary of named DataFrames that each module can read from and write to.

### Production Modules

| Module | Responsibility |
|---|---|
| **Data Sourcing** | Reads from the data lake. Users specify tables, columns, date ranges, and additional filters. Returns data as a PySpark DataFrame stored in shared state. |
| **Transformation** | Runs Spark SQL against DataFrames in shared state, producing a new transformed DataFrame. |
| **DataFrame Writer** | Writes a named DataFrame from shared state to a curation space. |
| **External** | Executes a custom user-supplied Python class for arbitrary logic. |

## C# Equivalents

| Production Concept | C# Equivalent |
|---|---|
| PySpark DataFrame | `Lib.DataFrames.DataFrame` |
| Shared state | `Dictionary<string, object>` passed through module chain |
| ETL module | Classes implementing `IModule` interface |
| Job configuration | JSON file deserialized into an ordered list of module configs |
| Spark SQL | In-memory SQLite connection (Microsoft.Data.Sqlite) |
| Framework executor | `JobRunner` reads the job conf and runs modules in sequence |

## Key Design Principles

- **Module chain execution:** Modules run in the order defined by the job conf. Each module receives the current shared state, performs its operation, and returns the updated state.
- **Shared state as the integration contract:** Modules are decoupled from one another. They communicate only through named entries in the shared state dictionary.
- **JSON-driven configuration:** Job behavior is defined externally in JSON, not in code.
- **DataFrame as a first-class citizen:** The custom `DataFrame` class provides PySpark-like operations so that transformation logic feels familiar to users of the production system.
- **Full-load temporal data:** The data lake follows a snapshot (full-load) pattern. Each day's load is a complete picture identified by an `ifw_effective_date` column.
- **Explicit effective date management:** The caller MUST supply an effective date for every invocation. No auto-advance or gap-fill logic. For batch processing across date ranges, use the task queue.
- **run_date vs. effective date:** `run_date` in `control.job_runs` is the calendar date the executor actually ran (always today). `min_effective_date` / `max_effective_date` record which data date that run processed. Separate concerns.

## Technology Choices

| Concern | Choice | Rationale |
|---|---|---|
| Language / runtime | C# / .NET 8 | Owner's primary language |
| PostgreSQL client | Npgsql 8 | Standard .NET Postgres driver |
| In-process SQL engine | Microsoft.Data.Sqlite 8 | Enables free-form SQL in Transformation without a running server |
| Test framework | xUnit | Industry standard for modern .NET |
| External assembly loading | `Assembly.LoadFrom` + reflection | Mirrors the production Python `importlib` pattern |
| Parquet file output | Parquet.Net 4.x | Pure managed C#, no native dependencies |

## Solution Structure

```
MockEtlFramework/
+-- Lib/                        # Framework library
|   +-- DataFrames/             # DataFrame, Row, GroupedDataFrame
|   +-- Modules/                # IModule, DataSourcing, Transformation,
|   |                           #   DataFrameWriter, CsvFileWriter, ParquetFileWriter,
|   |                           #   External, IExternalStep
|   +-- Control/                # JobExecutorService, TaskQueueService, ExecutionPlan,
|   |                           #   ControlDb, JobRegistration, JobDependency
|   +-- AppConfig.cs            # Configuration model (PathSettings + DatabaseSettings + TaskQueueSettings)
|   +-- ConnectionHelper.cs     # DB connection string builder
|   +-- DatePartitionHelper.cs  # Date-partitioned directory scanning
|   +-- ModuleFactory.cs        # Module instantiation from JSON config
|   +-- PathHelper.cs           # Path resolution and {TOKEN} expansion
|   +-- JobConf.cs              # JSON config model
|   +-- JobRunner.cs            # Runs module chain
+-- JobExecutor/                # Console app entry point
|   +-- appsettings.json        # Runtime config overrides
|   +-- Jobs/                   # Job configuration JSON files
+-- ExternalModules/            # User-supplied C# processors implementing IExternalStep
+-- Lib.Tests/                  # xUnit tests
+-- SQL/                        # SQL scripts (DDL, job registration)
+-- Documentation/              # Codebase docs
+-- Output/                     # File writer output (gitignored)
```
