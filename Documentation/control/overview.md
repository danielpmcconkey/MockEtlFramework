# Control Layer

Orchestration layer in `Lib/Control/`. Sits above `JobRunner`. Reads job registrations and dependency graph from the PostgreSQL `control` schema, determines which jobs need to run, executes them in the correct order, and records the outcome of every run attempt.

## Database Schema

The `control` schema contains:

- `control.jobs` -- Job registrations (ID, name, description, conf path, active flag)
- `control.job_runs` -- Run history (run_date, effective dates, status, attempt number)
- `control.job_dependencies` -- Dependency graph (downstream job, upstream job, dependency type)
- `control.task_queue` -- Queue for long-running batch execution

## Models

| Class | File | Purpose |
|---|---|---|
| `JobRegistration` | `JobRegistration.cs` | Model for a `control.jobs` row -- job ID, name, description, conf path, active flag |
| `JobDependency` | `JobDependency.cs` | Model for a `control.job_dependencies` row -- downstream job ID, upstream job ID, dependency type |

## ControlDb

`ControlDb.cs`. Internal static data-access layer for the control schema.

**Reads:**
- `GetActiveJobs` -- all active job registrations
- `GetAllDependencies` -- full dependency graph
- `GetSucceededJobIds` -- jobs that succeeded for a given run_date
- `GetEverSucceededJobIds` -- jobs that have ever succeeded
- `GetLastSucceededMaxEffectiveDate` -- latest successful effective date for a job
- `GetNextAttemptNumber` -- next attempt number for a (job, effective date range) pair

**Writes:**
- `InsertRun` -- records run_date, effective date range, attempt number, source
- `MarkRunning`, `MarkSucceeded`, `MarkFailed`, `MarkSkipped` -- status transitions

## ExecutionPlan

`ExecutionPlan.cs`. Internal static class that applies Kahn's topological sort to produce an ordered run list. Only unsatisfied dependency edges are counted: `SameDay` edges are always treated as unsatisfied during sorting (deferred to execution-time checking); a `Latest` edge is considered satisfied — and removed from the graph — if the upstream job has ever succeeded. Throws `InvalidOperationException` on cycle detection.

## Dependency Types

| Type | Semantics |
|---|---|
| `SameDay` | The upstream job must have a `Succeeded` run with the same `run_date` as the downstream job. Within a single executor invocation, jobs run in topological order, so this is satisfied naturally when A precedes B in the plan. |
| `Latest` | The upstream job must have succeeded at least once for any `run_date`. Used for one-time setup jobs or slowly-changing reference data. |

## Job Registration

```sql
INSERT INTO control.jobs (job_name, description, job_conf_path, is_active)
VALUES ('SomeJob', 'Description', 'JobExecutor/Jobs/some_job.json', true)
ON CONFLICT (job_name) DO NOTHING;
```

## Orchestrator Reference

| Service | Doc |
|---|---|
| JobExecutorService (single-date runs) | [job-executor-service.md](job-executor-service.md) |
| TaskQueueService (long-running queue) | [task-queue-service.md](task-queue-service.md) |
