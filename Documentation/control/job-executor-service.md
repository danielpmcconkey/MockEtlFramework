# JobExecutorService

`Lib/Control/JobExecutorService.cs`

Public orchestrator for single-date job execution. Requires an explicit effective date -- no auto-advance or gap-fill.

## Execution Flow

1. Load active jobs and dependencies from `control` schema
2. Build topological execution plan via `ExecutionPlan`
3. For each job in plan order:
   a. Check dependency satisfaction (`SameDay` edges checked against today's succeeded runs; `Latest` edges checked against all-time succeeded jobs)
   b. Inject effective date into shared state as `__etlEffectiveDate`
   c. Run the pipeline through `JobRunner`
   d. Record status: `Pending -> Running -> Succeeded / Failed`
4. Failed jobs' `SameDay` dependents are recorded as `Skipped`

## Effective Date vs Run Date

- `run_date`: Calendar date the executor actually ran. Always today. Set internally.
- `min_effective_date` / `max_effective_date`: Which data date the run processed. Supplied by the caller.

These are separate concerns. A run on March 8 might process data for October 15.

## Usage

Called by `Program.cs` for non-service invocations:

```
JobExecutor 2024-10-15           # all active jobs
JobExecutor 2024-10-15 JobName   # one specific job
```

Accepts a required `effectiveDate` and optional `specificJobName`.
