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
- **Automatic effective date management:** Effective dates are never hard-wired in job configuration files. Instead, `JobExecutorService` reads each job's last successful `max_effective_date` from `control.job_runs` and gap-fills forward one day at a time until today. The dates are injected into shared state before each pipeline run and picked up automatically by `DataSourcing`. On a job's very first run, the start date is read from a `firstEffectiveDate` field in the job conf. This keeps job confs generic and lets the executor handle all scheduling math.
- **run_date vs. effective date:** `run_date` in `control.job_runs` is the calendar date the executor actually ran (always today). `min_effective_date` / `max_effective_date` record which data dates that run processed. These are separate concerns: a single executor invocation on one `run_date` may produce many rows in `control.job_runs`, each covering a different effective date.

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
| `DataSourcing` | Queries a PostgreSQL data lake schema for a specified table, column list, and effective date range. Returns a single flat `DataFrame` with `as_of` appended as a column (skipped if the caller already includes it). Supports an optional `additionalFilter` clause. Effective dates may be supplied in the job conf JSON (`minEffectiveDate` / `maxEffectiveDate`) or omitted entirely, in which case the module reads the reserved shared-state keys `__minEffectiveDate` / `__maxEffectiveDate` injected by the executor at runtime. |
| `Transformation` | Opens an in-memory SQLite connection, registers every `DataFrame` in the current shared state as a SQLite table, executes user-supplied free-form SQL, and stores the result `DataFrame` back into shared state under a caller-specified result name. |
| `DataFrameWriter` | Writes a named `DataFrame` from shared state to a PostgreSQL curation schema. Auto-creates the target table if it does not exist (type inference from sample values). Supports `Overwrite` mode (truncate then insert) and `Append` mode (insert only). All writes are transaction-wrapped. |
| `External` | Loads a user-supplied .NET assembly from disk via reflection, locates a named type that implements `IExternalStep`, instantiates it, and delegates `Execute`. Allows teams to inject arbitrary C# logic into any job pipeline without modifying the framework. |
| `IExternalStep` | Interface that external assemblies must implement to be callable via the `External` module. |
| `ModuleFactory` | Static factory. Reads the `type` discriminator field from a `JsonElement` and instantiates the appropriate `IModule` implementation. Throws `InvalidOperationException` on unknown types and propagates `KeyNotFoundException` if the `type` field is absent. |

#### `Lib` (root)

| Class | Purpose |
|---|---|
| `ConnectionHelper` | Internal static helper that builds a Npgsql connection string. Decodes the Postgres password from a hex-encoded UTF-16 LE environment variable (`PGPASS`). |
| `JobConf` | JSON deserialization model. Contains the job name, an optional `firstEffectiveDate` (the bootstrap date used when the job has no prior successful runs), and an ordered `List<JsonElement>` of module configurations. |
| `JobRunner` | Deserializes a job conf from a JSON file path, iterates the module list, creates each module via `ModuleFactory`, and threads shared state through the pipeline. Accepts an optional `initialState` dictionary pre-populated by the executor (used to inject effective dates). Logs progress to the console. |

#### `Lib.Control`

Orchestration layer that sits above `JobRunner`. Reads job registrations and dependency graph from the PostgreSQL `control` schema, determines which jobs need to run, executes them in the correct order, and records the outcome of every run attempt.

| Class | Purpose |
|---|---|
| `JobRegistration` | Model for a `control.jobs` row — job ID, name, description, conf path, and active flag. |
| `JobDependency` | Model for a `control.job_dependencies` row — downstream job ID, upstream job ID, and dependency type (`SameDay` or `Latest`). |
| `ControlDb` | Internal static data-access layer for the control schema. Reads: `GetActiveJobs`, `GetAllDependencies`, `GetSucceededJobIds`, `GetEverSucceededJobIds`, `GetLastSucceededMaxEffectiveDate`, `GetNextAttemptNumber` (keyed by effective date range). Writes: `InsertRun` (records `run_date`, `min_effective_date`, `max_effective_date`), `MarkRunning`, `MarkSucceeded`, `MarkFailed`, `MarkSkipped`. |
| `ExecutionPlan` | Internal static class that applies Kahn's topological sort to produce an ordered run list. Only unsatisfied dependency edges are counted: a `SameDay` edge is satisfied if the upstream job already succeeded for this `run_date`; a `Latest` edge is satisfied if the upstream job has ever succeeded. Jobs that already succeeded today are excluded from the plan. Throws `InvalidOperationException` on cycle detection. |
| `JobExecutorService` | Public orchestrator. Loads jobs and dependencies, builds the execution plan, then for each job computes the pending effective date sequence (gap-fill from last succeeded `max_effective_date` + 1 day to today, using `firstEffectiveDate` from the job conf on first run). Injects dates into shared state and runs each pipeline through `JobRunner`. Records `Pending → Running → Succeeded / Failed` in `control.job_runs` per effective date. Failed jobs stop their own gap-fill sequence; their `SameDay` dependents are recorded as `Skipped`. Accepts an optional `effectiveDateOverride` (single-date backfill/rerun) and `specificJobName`. |

**Dependency types:**

| Type | Semantics |
|---|---|
| `SameDay` | The upstream job must have a `Succeeded` run with the same `run_date` (execution date) as the downstream job. Within a single executor invocation jobs run in topological order, so this is satisfied naturally when A precedes B in the plan. |
| `Latest` | The upstream job must have succeeded at least once for any `run_date`. Used for one-time setup jobs or slowly-changing reference data that only needs to be current, not date-aligned. |

---

### `JobExecutor` (Console Application)

Entry point for running jobs from the command line. Delegates to `JobExecutorService`.

```
JobExecutor                              # auto-advance all active jobs
JobExecutor <job_name>                   # auto-advance one specific job
JobExecutor <effective_date>             # backfill a specific date, all jobs
JobExecutor <effective_date> <job_name>  # backfill a specific date, one job
```

If the first argument parses as `yyyy-MM-dd` it is treated as an effective date override; otherwise it is treated as a job name and auto-advance mode is used. `run_date` is always set to today internally and is never a CLI argument.

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

Note that no `minEffectiveDate` / `maxEffectiveDate` fields appear in the `DataSourcing` modules — the executor injects those at runtime via shared state.

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
