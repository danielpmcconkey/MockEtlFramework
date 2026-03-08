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
///   - When ALL threads are idle and idle cycles are exhausted, service exits
///   - No SIGINT handler — die immediately on kill, try/finally marks Failed on way out
/// </summary>
public class TaskQueueService
{
    private readonly TaskQueueSettings _config;

    private volatile bool _shutdownRequested;
    private readonly int _totalThreadCount;
    private readonly bool[] _threadIdle;
    private volatile int _allIdleCycleCount = 0;

    public TaskQueueService(AppConfig appConfig)
    {
        _config = appConfig.TaskQueue;
        _totalThreadCount = _config.ThreadCount;
        _threadIdle = new bool[_totalThreadCount];
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
                threads[i] = new Thread(() => WorkerLoop($"W{threadIndex}", threadIndex))
                {
                    Name = $"QueueWorker-W{threadIndex}",
                    IsBackground = true
                };
                threads[i].Start();
            }

            // Wait for all threads to finish
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

    private void WorkerLoop(string threadLabel, int threadIndex)
    {
        Console.WriteLine($"[{threadLabel}] Worker started");

        while (!_shutdownRequested)
        {
            List<TaskQueueItem>? batch = null;
            try
            {
                batch = ClaimNextJobBatch();

                if (batch == null || batch.Count == 0)
                {
                    // Mark this thread as idle
                    _threadIdle[threadIndex] = true;

                    // Check if ALL threads are idle
                    if (_threadIdle.All(idle => idle))
                    {
                        _allIdleCycleCount++;
                        Console.WriteLine($"[{threadLabel}] All threads idle (cycle {_allIdleCycleCount}/{_config.MaxIdleCycles}). Sleeping {_config.IdleCheckIntervalMs}ms...");

                        if (_allIdleCycleCount >= _config.MaxIdleCycles)
                        {
                            Console.WriteLine($"[{threadLabel}] Reached maximum idle cycles ({_config.MaxIdleCycles}). Signaling shutdown.");
                            _shutdownRequested = true;
                            return;
                        }

                        Thread.Sleep(_config.IdleCheckIntervalMs);
                        continue;
                    }

                    Console.WriteLine($"[{threadLabel}] No work available. Sleeping {_config.PollIntervalMs}ms...");
                    Thread.Sleep(_config.PollIntervalMs);
                    continue;
                }

                // Got work — mark this thread as active and reset idle counter
                _threadIdle[threadIndex] = false;
                _allIdleCycleCount = 0;

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
            throw new InvalidOperationException($"No active job found with name '{task.JobName}'");

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
