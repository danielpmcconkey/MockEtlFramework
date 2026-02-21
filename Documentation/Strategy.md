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
- **Full-load temporal data:** The data lake follows a snapshot (full-load) pattern. Each day's load is a complete picture of a table's state at that point in time, identified by an `as_of` date column. `DataSourcing` returns a flat DataFrame that includes the `as_of` column, allowing consumers to work across date ranges in a single DataFrame without requiring special date-loop logic in the framework itself.

---

## Solution Structure

### `Lib` (Class Library)

The core framework library. Referenced by both the job executor and the test project.

#### `Lib.DataFrames`

| Class | Purpose |
|---|---|
| `DataFrame` | Immutable, in-memory tabular data structure with a PySpark-inspired fluent API. Constructed via `DataFrame.FromObjects<T>()` or from raw column/row data. |
| `Row` | Dictionary-backed row abstraction used by `DataFrame`. Values are `object?`, accessed by column name. |
| `GroupedDataFrame` | Intermediate result of `DataFrame.GroupBy(...)`. Supports aggregation operations (`Count()`, with room to add `Sum`, `Avg`, etc.). |

#### `Lib.Modules`

| Class | Purpose |
|---|---|
| `IModule` | Core module interface: `Execute(Dictionary<string, object> sharedState) → Dictionary<string, object>`. All modules implement this contract. |
| `DataSourcing` | Queries a PostgreSQL data lake schema for a specified table, column list, and effective date range. Returns a single flat `DataFrame` with `as_of` appended as a column (skipped if the caller already includes it). Supports an optional `additionalFilter` clause passed directly into the SQL `WHERE` predicate. |
| `Transformation` | Opens an in-memory SQLite connection, registers every `DataFrame` in the current shared state as a SQLite table, executes user-supplied free-form SQL, and stores the result `DataFrame` back into shared state under a caller-specified result name. |
| `DataFrameWriter` | Writes a named `DataFrame` from shared state to a PostgreSQL curation schema. Auto-creates the target table if it does not exist (type inference from sample values). Supports `Overwrite` mode (truncate then insert) and `Append` mode (insert only). All writes are transaction-wrapped. |
| `External` | Loads a user-supplied .NET assembly from disk via reflection, locates a named type that implements `IExternalStep`, instantiates it, and delegates `Execute`. Allows teams to inject arbitrary C# logic into any job pipeline without modifying the framework. |
| `IExternalStep` | Interface that external assemblies must implement to be callable via the `External` module. |
| `ModuleFactory` | Static factory. Reads the `type` discriminator field from a `JsonElement` and instantiates the appropriate `IModule` implementation. Throws `InvalidOperationException` on unknown types and propagates `KeyNotFoundException` if the `type` field is absent. |

#### `Lib` (root)

| Class | Purpose |
|---|---|
| `ConnectionHelper` | Internal static helper that builds a Npgsql connection string. Decodes the Postgres password from a hex-encoded UTF-16 LE environment variable (`PGPASS`). |
| `JobConf` | JSON deserialization model. Contains the job name and an ordered `List<JsonElement>` of module configurations. |
| `JobRunner` | Deserializes a job conf from a JSON file path, iterates the module list, creates each module via `ModuleFactory`, and threads shared state through the pipeline. Logs progress to the console. |

#### `Lib.Control`

Orchestration layer that sits above `JobRunner`. Reads job registrations and dependency graph from the PostgreSQL `control` schema, determines which jobs need to run, executes them in the correct order, and records the outcome of every run attempt.

| Class | Purpose |
|---|---|
| `JobRegistration` | Model for a `control.jobs` row — job ID, name, description, conf path, and active flag. |
| `JobDependency` | Model for a `control.job_dependencies` row — downstream job ID, upstream job ID, and dependency type (`SameDay` or `Latest`). |
| `ControlDb` | Internal static data-access layer for the control schema. Provides reads (`GetActiveJobs`, `GetAllDependencies`, `GetSucceededJobIds`, `GetEverSucceededJobIds`, `GetNextAttemptNumber`) and writes (`InsertRun`, `MarkRunning`, `MarkSucceeded`, `MarkFailed`, `MarkSkipped`). |
| `ExecutionPlan` | Internal static class that applies Kahn's topological sort to produce an ordered run list. Only unsatisfied dependency edges are counted: a `SameDay` edge is satisfied if the upstream job already succeeded for this `run_date`; a `Latest` edge is satisfied if the upstream job has ever succeeded. Jobs that already succeeded today are excluded from the plan. Throws `InvalidOperationException` on cycle detection. |
| `JobExecutorService` | Public orchestrator. Loads jobs and dependencies, builds the execution plan, runs each job through `JobRunner`, and records `Pending → Running → Succeeded / Failed` transitions in `control.job_runs`. Any job whose `SameDay` upstream dependency failed during the current invocation is recorded as `Skipped`. Accepts an optional `specificJobName` to run a single job instead of the full active set. |

**Dependency types:**

| Type | Semantics |
|---|---|
| `SameDay` | The upstream job must have a `Succeeded` run for the **same** `run_date` as the downstream job. The common case for day-over-day pipelines where B needs A's output for the same business date. |
| `Latest` | The upstream job must have succeeded at least once for **any** `run_date`. Used for one-time setup jobs or slowly-changing reference data that only needs to be current, not date-aligned. |

---

### `JobExecutor` (Console Application)

Entry point for running jobs from the command line. Accepts a `run_date` (`yyyy-MM-dd`) and an optional job name, then delegates to `JobExecutorService`.

```
JobExecutor <run_date> [job_name]
```

**Sample job configuration** (registered in `control.jobs`, file stored at the path in `job_conf_path`):
```json
{
  "jobName": "CustomerAccountSummary",
  "modules": [
    {
      "type": "DataSourcing",
      "resultName": "customers",
      "schema": "datalake",
      "table": "customers",
      "columns": ["id", "first_name", "last_name"],
      "minEffectiveDate": "2024-10-01",
      "maxEffectiveDate": "2024-10-31"
    },
    {
      "type": "Transformation",
      "resultName": "summary",
      "sql": "SELECT c.id, c.first_name, c.last_name, c.as_of FROM customers c"
    },
    {
      "type": "DataFrameWriter",
      "source": "summary",
      "targetTable": "customer_summary",
      "writeMode": "Overwrite"
    }
  ]
}
```

---

### `Lib.Tests` (xUnit Test Project)

Unit test coverage for framework components. Tests do not require a live database — all DataFrame and Transformation tests operate entirely in memory.

| Test Class | Coverage |
|---|---|
| `DataFrameTests` | `DataFrame` API — Count, Columns, Select, Filter, WithColumn, Drop, OrderBy, Limit, Union, Distinct, Join (inner + left), GroupBy/Count |
| `TransformationTests` | `Transformation` module — basic SELECT, WHERE, column projection, JOIN across two DataFrames, GROUP BY aggregation, shared state preservation, non-DataFrame entries in state are silently ignored |
| `ModuleFactoryTests` | `ModuleFactory` — all four module types, optional `additionalFilter` field, both write modes, unknown type error, missing type field error |

---

## Technology Choices

| Concern | Choice | Rationale |
|---|---|---|
| Language / runtime | C# / .NET 8 | Owner's primary language |
| PostgreSQL client | Npgsql 8 | Standard .NET Postgres driver |
| In-process SQL engine | Microsoft.Data.Sqlite 8 | Enables free-form SQL in `Transformation` without a running server; well-audited, Microsoft-supported |
| Test framework | xUnit | Industry standard for modern .NET; no test runner installation required |
| External assembly loading | `Assembly.LoadFrom` + reflection | Mirrors the production Python `importlib` pattern for user-supplied modules |

---

*This document will be updated as architectural decisions are made.*
