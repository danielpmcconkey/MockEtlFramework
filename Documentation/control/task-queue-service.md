# TaskQueueService

`Lib/Control/TaskQueueService.cs`

Long-running queue-based executor. Polls `control.task_queue` for pending tasks and processes them across multiple threads using a claim-by-job model. Eliminates dotnet startup overhead by paying the JIT cost once, then parallelizes work across configurable threads.

## Threading Model

- **N threads**, all identical (configurable via `TaskQueueSettings.ThreadCount`, default 5)
- Each thread claims ALL pending tasks for a single job using PostgreSQL advisory locks
- Tasks within a job are processed sequentially in effective-date order
- Different jobs run in parallel across threads
- Each thread has its own DB connection (Npgsql is not thread-safe)

### Why Claim-by-Job

The model guarantees:

- Oct 1 Job A and Oct 2 Job B can run in parallel (different threads)
- Oct 1 Job A and Oct 2 Job A **never** run in parallel (same thread owns all of Job A's dates)
- Append-mode writes and CDC ordering are safe without any special per-job config

### Claim Flow

1. Query distinct job names with `status = 'Pending'`
2. Try `pg_try_advisory_xact_lock(hashtext(job_name))` on each until one succeeds
3. Claim all pending rows for that job (`FOR UPDATE SKIP LOCKED`)
4. Commit transaction (releases advisory lock -- rows are now `Running`)
5. Return tasks sorted by effective_date

### Batch Failure

If a task fails, all remaining tasks in the batch are marked `Failed`. This preserves ordering safety for append-mode jobs where later dates depend on earlier dates completing successfully.

## Idle & Shutdown

- Each thread tracks its own idle state
- When ALL threads are idle simultaneously, a global idle counter increments
- When any thread finds work, the counter resets to 0
- After `MaxIdleCycles` consecutive all-idle checks, shutdown is signaled
- Default: 960 cycles x 30s = 8 hours

No SIGINT handler. On kill, `try/finally` marks in-flight tasks as `Failed`.

### Internal Model

`TaskQueueItem` (internal class in `TaskQueueService.cs`) represents a claimed task: task ID, job name, effective date.

## Queue Population

```sql
INSERT INTO control.task_queue (job_name, effective_date, execution_mode)
SELECT j.job_name, d.dt::date, 'parallel' -- execution_mode is no longer used by the service but the column may require a value
FROM control.jobs j
CROSS JOIN generate_series('2024-10-01'::date, '2024-12-31'::date, '1 day') d(dt)
WHERE j.is_active = true
ORDER BY d.dt, j.job_name;
```

## Monitoring

```sql
SELECT status, COUNT(*) FROM control.task_queue GROUP BY status;
```

## Lifecycle

1. Start the executor: `dotnet run --project JobExecutor -- --service`
2. Populate the queue via SQL
3. Executor picks up work automatically
4. Exits after idle timeout (default 8 hours)
