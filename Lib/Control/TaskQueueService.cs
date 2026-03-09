using Npgsql;

namespace Lib.Control;

/// <summary>
/// Long-running queue-based executor. Polls control.task_queue for pending tasks
/// and executes them across multiple threads using a claim-by-job model.
///
/// Threading model:
///   - N threads (configurable via AppConfig.TaskQueue.ThreadCount)
///   - Each thread claims ALL pending tasks for a single job using advisory locks
///   - Tasks within a job are processed sequentially in effective_date order
///   - Different jobs run in parallel across threads
///   - Each thread has its own DB connection (Npgsql not thread-safe)
///   - If a task fails, remaining tasks in the batch are marked Failed
///
/// Exit behavior:
///   - A watchdog thread checks once per minute whether the time since any
///     worker last found work exceeds IdleShutdownSeconds (default 8 hours).
///     If so, it signals shutdown.
///   - No SIGINT handler — die immediately on kill, try/finally marks Failed on way out
/// </summary>
public class TaskQueueService
{
    private readonly TaskQueueSettings _config;

    private volatile bool _shutdownRequested;
    private readonly int _totalThreadCount;
    private readonly object _activityLock = new();
    private DateTime _lastActivityUtc = DateTime.UtcNow;

    public TaskQueueService(AppConfig appConfig)
    {
        _config = appConfig.TaskQueue;
        _totalThreadCount = _config.ThreadCount;
    }

    /// <summary>
    /// Pre-loads the job name -> conf path mapping from control.jobs.
    /// Each thread can read this safely since it's populated once before threads start.
    /// </summary>
    private Dictionary<string, JobRegistration> _jobsByName = new(StringComparer.OrdinalIgnoreCase);

    public void Run()
    {
        Console.WriteLine("[TaskQueueService] Starting queue executor...");
        Console.WriteLine($"[TaskQueueService] {_totalThreadCount} thread(s)");

        // Load job registry once
        _jobsByName = ControlDb.GetActiveJobs()
            .ToDictionary(j => j.JobName, j => j, StringComparer.OrdinalIgnoreCase);

        Console.WriteLine($"[TaskQueueService] Loaded {_jobsByName.Count} active job(s) from registry.");

        var threads = new Thread[_totalThreadCount];

        try
        {
            for (int i = 0; i < _totalThreadCount; i++)
            {
                int threadIndex = i;
                threads[i] = new Thread(() => WorkerLoop($"W{threadIndex}"))
                {
                    Name = $"QueueWorker-W{threadIndex}",
                    IsBackground = true
                };
                threads[i].Start();
            }

            // Watchdog thread — checks idle timeout once per minute
            var watchdog = new Thread(() => WatchdogLoop())
            {
                Name = "IdleWatchdog",
                IsBackground = true
            };
            watchdog.Start();

            // Wait for all worker threads to finish
            foreach (var t in threads)
                t.Join();

            Console.WriteLine("[TaskQueueService] All threads finished. Queue empty. Shutting down.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[TaskQueueService] FATAL: {ex.Message}");
            _shutdownRequested = true;

            // Wait for threads to notice shutdown
            foreach (var t in threads)
                t?.Join(TimeSpan.FromSeconds(10));

            throw;
        }
    }

    private void RecordActivity()
    {
        lock (_activityLock)
        {
            _lastActivityUtc = DateTime.UtcNow;
        }
    }

    private void WatchdogLoop()
    {
        Console.WriteLine($"[Watchdog] Idle shutdown threshold: {_config.IdleShutdownSeconds}s");

        while (!_shutdownRequested)
        {
            Thread.Sleep(60_000);

            TimeSpan idle;
            lock (_activityLock)
            {
                idle = DateTime.UtcNow - _lastActivityUtc;
            }

            if (idle.TotalSeconds >= _config.IdleShutdownSeconds)
            {
                Console.WriteLine($"[Watchdog] No work found for {idle:h\\:mm\\:ss}. Signaling shutdown.");
                _shutdownRequested = true;
                return;
            }
        }
    }

    private void WorkerLoop(string threadLabel)
    {
        Console.WriteLine($"[{threadLabel}] Worker started");

        while (!_shutdownRequested)
        {
            try
            {
                var batch = ClaimNextJobBatch();

                if (batch == null || batch.Count == 0)
                {
                    Thread.Sleep(_config.PollIntervalMs);
                    continue;
                }

                RecordActivity();

                Console.WriteLine($"[{threadLabel}] Claimed {batch.Count} task(s) for job '{batch[0].JobName}'");

                ProcessBatch(batch, threadLabel);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{threadLabel}] ERROR (no task context): {ex.Message}");
            }
        }

        Console.WriteLine($"[{threadLabel}] Worker exiting.");
    }

    private void ProcessBatch(List<TaskQueueItem> batch, string threadLabel)
    {
        for (int i = 0; i < batch.Count; i++)
        {
            if (_shutdownRequested) return;

            var task = batch[i];
            try
            {
                Console.WriteLine($"[{threadLabel}] Running task {task.TaskId}: {task.JobName} @ {task.EffectiveDate:yyyy-MM-dd} ({i + 1}/{batch.Count})");

                ExecuteTask(task, threadLabel);

                MarkTaskSucceeded(task.TaskId);
                Console.WriteLine($"[{threadLabel}] Task {task.TaskId} SUCCEEDED: {task.JobName} @ {task.EffectiveDate:yyyy-MM-dd}");
            }
            catch (Exception ex)
            {
                MarkTaskFailed(task.TaskId, ex.ToString());
                Console.WriteLine($"[{threadLabel}] Task {task.TaskId} FAILED: {task.JobName} @ {task.EffectiveDate:yyyy-MM-dd} — {ex.Message}");

                // Fail remaining tasks in the batch — ordering matters for append-mode jobs
                for (int j = i + 1; j < batch.Count; j++)
                {
                    MarkTaskFailed(batch[j].TaskId,
                        $"Skipped: prior task {task.TaskId} ({task.JobName} @ {task.EffectiveDate:yyyy-MM-dd}) failed");
                    Console.WriteLine($"[{threadLabel}] Task {batch[j].TaskId} SKIPPED: {batch[j].JobName} @ {batch[j].EffectiveDate:yyyy-MM-dd} (prior failure)");
                }

                return;
            }
        }
    }

    private void ExecuteTask(TaskQueueItem task, string threadLabel)
    {
        if (!_jobsByName.TryGetValue(task.JobName, out var job))
        {
            // Job registered after startup — reload registry and retry once
            Console.WriteLine($"[{threadLabel}] Job '{task.JobName}' not in cache. Reloading job registry...");
            _jobsByName = ControlDb.GetActiveJobs()
                .ToDictionary(j => j.JobName, j => j, StringComparer.OrdinalIgnoreCase);
            Console.WriteLine($"[{threadLabel}] Reloaded {_jobsByName.Count} active job(s).");

            if (!_jobsByName.TryGetValue(task.JobName, out job))
                throw new InvalidOperationException($"No active job found with name '{task.JobName}'");
        }

        var initialState = new Dictionary<string, object>
        {
            [Modules.DataSourcing.EtlEffectiveDateKey] = task.EffectiveDate
        };

        DateOnly runDate = DateOnly.FromDateTime(DateTime.Today);
        int attemptNum = ControlDb.GetNextAttemptNumber(job.JobId, task.EffectiveDate, task.EffectiveDate);
        int runId = ControlDb.InsertRun(job.JobId, runDate, task.EffectiveDate, task.EffectiveDate, attemptNum, "queue");
        ControlDb.MarkRunning(runId);

        try
        {
            var runner = new JobRunner();
            var finalState = runner.Run(job.JobConfPath, initialState);

            int? rowsProcessed = null;
            var frames = finalState.Values.OfType<DataFrames.DataFrame>().ToList();
            if (frames.Count > 0)
                rowsProcessed = frames.Sum(f => f.Count);

            ControlDb.MarkSucceeded(runId, rowsProcessed);
        }
        catch (Exception)
        {
            ControlDb.MarkFailed(runId, $"See task_queue task_id={task.TaskId} for details");
            throw; // Re-throw so ProcessBatch catches it and fails remaining tasks
        }
    }

    // -------------------------------------------------------------------------
    // Queue operations — each uses its own connection
    // -------------------------------------------------------------------------

    /// <summary>
    /// Claims all pending tasks for a single job using advisory locks for
    /// job-level exclusivity. Returns null if no work is available.
    ///
    /// Flow:
    ///   1. Find distinct job names with pending tasks
    ///   2. Try pg_try_advisory_xact_lock(hashtext(job_name)) on each until one succeeds
    ///   3. Claim all pending rows for that job (FOR UPDATE SKIP LOCKED)
    ///   4. Commit (releases advisory lock — rows are now 'Running')
    ///   5. Return tasks sorted by effective_date
    /// </summary>
    private static List<TaskQueueItem>? ClaimNextJobBatch()
    {
        using var conn = new NpgsqlConnection(ConnectionHelper.GetConnectionString());
        conn.Open();
        using var txn = conn.BeginTransaction();

        // Find distinct job names with pending work
        var jobNames = new List<string>();
        using (var cmd = new NpgsqlCommand(@"
            SELECT DISTINCT job_name FROM control.task_queue
            WHERE status = 'Pending'
            ORDER BY job_name", conn, txn))
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
                jobNames.Add(reader.GetString(0));
        }

        if (jobNames.Count == 0)
        {
            txn.Commit();
            return null;
        }

        // Try advisory lock on each job until one succeeds.
        // Advisory locks prevent two threads from claiming the same job simultaneously.
        // The lock is transaction-scoped — released on commit.
        string? claimedJob = null;
        foreach (var jobName in jobNames)
        {
            using var lockCmd = new NpgsqlCommand(
                "SELECT pg_try_advisory_xact_lock(hashtext(@job))", conn, txn);
            lockCmd.Parameters.AddWithValue("job", jobName);
            bool gotLock = (bool)lockCmd.ExecuteScalar()!;
            if (gotLock)
            {
                claimedJob = jobName;
                break;
            }
        }

        if (claimedJob == null)
        {
            txn.Commit();
            return null; // All jobs are locked by other threads
        }

        // Claim all pending tasks for this job
        var tasks = new List<TaskQueueItem>();
        using (var claimCmd = new NpgsqlCommand(@"
            UPDATE control.task_queue
            SET status = 'Running', started_at = NOW()
            WHERE task_id IN (
                SELECT task_id FROM control.task_queue
                WHERE status = 'Pending' AND job_name = @job
                FOR UPDATE SKIP LOCKED
            )
            RETURNING task_id, job_name, effective_date;", conn, txn))
        {
            claimCmd.Parameters.AddWithValue("job", claimedJob);
            using var reader = claimCmd.ExecuteReader();
            while (reader.Read())
            {
                tasks.Add(new TaskQueueItem
                {
                    TaskId = reader.GetInt32(0),
                    JobName = reader.GetString(1),
                    EffectiveDate = DateOnly.FromDateTime(reader.GetDateTime(2))
                });
            }
        }

        txn.Commit();

        // Sort by effective date for sequential processing
        tasks.Sort((a, b) => a.EffectiveDate.CompareTo(b.EffectiveDate));
        return tasks;
    }

    private static void MarkTaskSucceeded(int taskId)
    {
        using var conn = new NpgsqlConnection(ConnectionHelper.GetConnectionString());
        conn.Open();
        using var cmd = new NpgsqlCommand(
            "UPDATE control.task_queue SET status = 'Succeeded', completed_at = NOW() WHERE task_id = @id",
            conn);
        cmd.Parameters.AddWithValue("id", taskId);
        cmd.ExecuteNonQuery();
    }

    private static void MarkTaskFailed(int taskId, string errorMessage)
    {
        using var conn = new NpgsqlConnection(ConnectionHelper.GetConnectionString());
        conn.Open();
        using var cmd = new NpgsqlCommand(
            "UPDATE control.task_queue SET status = 'Failed', completed_at = NOW(), error_message = @err WHERE task_id = @id",
            conn);
        cmd.Parameters.AddWithValue("err", errorMessage);
        cmd.Parameters.AddWithValue("id", taskId);
        cmd.ExecuteNonQuery();
    }
}

/// <summary>
/// Represents a claimed task from the queue.
/// </summary>
internal class TaskQueueItem
{
    public int TaskId { get; init; }
    public string JobName { get; init; } = "";
    public DateOnly EffectiveDate { get; init; }
}
