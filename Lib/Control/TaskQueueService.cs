using Npgsql;

namespace Lib.Control;

/// <summary>
/// Long-running queue-based executor. Polls control.task_queue for pending tasks
/// and executes them across multiple threads.
///
/// Threading model:
///   - 4 threads poll for execution_mode = 'parallel'
///   - 1 thread polls for execution_mode = 'serial'
///   - Each thread has its own DB connection (EF Core / Npgsql not thread-safe)
///   - Task claim uses FOR UPDATE SKIP LOCKED to prevent races
///
/// Exit behavior:
///   - When ALL threads find empty queue, service exits
///   - No SIGINT handler — die immediately on kill, try/finally marks Failed on way out
/// </summary>
public class TaskQueueService
{
    private const int ParallelThreadCount = 4;
    private const int PollIntervalMs = 5000;
    private const int IdleCheckIntervalMs = 30000; // 30 seconds when all threads idle
    private const int MaxIdleCycles = 30; // 30 cycles = 15 minutes

    private volatile bool _shutdownRequested;
    private readonly int _totalThreadCount = ParallelThreadCount + 1; // +1 for serial
    private readonly bool[] _threadIdle; // per-thread idle flag
    private volatile int _allIdleCycleCount = 0; // Counter for consecutive idle cycles


    public TaskQueueService()
    {
        _threadIdle = new bool[_totalThreadCount];
    }

    /// <summary>
    /// Pre-loads the job name → conf path mapping from control.jobs.
    /// Each thread can read this safely since it's populated once before threads start.
    /// </summary>
    private Dictionary<string, JobRegistration> _jobsByName = new(StringComparer.OrdinalIgnoreCase);

    public void Run()
    {
        Console.WriteLine("[TaskQueueService] Starting queue executor...");
        Console.WriteLine($"[TaskQueueService] {ParallelThreadCount} parallel thread(s) + 1 serial thread");

        // Load job registry once
        _jobsByName = ControlDb.GetActiveJobs()
            .ToDictionary(j => j.JobName, j => j, StringComparer.OrdinalIgnoreCase);

        Console.WriteLine($"[TaskQueueService] Loaded {_jobsByName.Count} active job(s) from registry.");

        var threads = new Thread[_totalThreadCount];

        try
        {
            // Start parallel threads
            for (int i = 0; i < ParallelThreadCount; i++)
            {
                int threadIndex = i;
                threads[i] = new Thread(() => WorkerLoop("parallel", $"P{threadIndex}", threadIndex))
                {
                    Name = $"QueueWorker-P{threadIndex}",
                    IsBackground = true
                };
                threads[i].Start();
            }

            // Start serial thread
            threads[ParallelThreadCount] = new Thread(() => WorkerLoop("serial", "S0", ParallelThreadCount))
            {
                Name = "QueueWorker-S0",
                IsBackground = true
            };
            threads[ParallelThreadCount].Start();

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

    private void WorkerLoop(string executionMode, string threadLabel, int threadIndex)
    {
        Console.WriteLine($"[{threadLabel}] Worker started (mode={executionMode})");

        while (!_shutdownRequested)
        {
            TaskQueueItem? task = null;
            try
            {
                task = ClaimNextTask(executionMode);

                if (task == null)
                {
                    // Mark this thread as idle
                    _threadIdle[threadIndex] = true;

                    // Check if ALL threads are idle
                    if (_threadIdle.All(idle => idle))
                    {
                        _allIdleCycleCount++;
                        Console.WriteLine($"[{threadLabel}] All threads idle (cycle {_allIdleCycleCount}/{MaxIdleCycles}). Sleeping {IdleCheckIntervalMs}ms...");
                        
                        if (_allIdleCycleCount >= MaxIdleCycles)
                        {
                            Console.WriteLine($"[{threadLabel}] Reached maximum idle cycles ({MaxIdleCycles}). Signaling shutdown after 5 minutes of no work.");
                            _shutdownRequested = true;
                            return;
                        }
                        
                        Thread.Sleep(IdleCheckIntervalMs); // Sleep for 30 seconds
                        continue;
                    }

                    Console.WriteLine($"[{threadLabel}] Queue empty. Sleeping {PollIntervalMs}ms...");
                    Thread.Sleep(PollIntervalMs);
                    continue;
                }

                // Got work — mark this thread as active and reset idle counter
                _threadIdle[threadIndex] = false;
                _allIdleCycleCount = 0; // Reset counter when work is found

                Console.WriteLine($"[{threadLabel}] Claimed task {task.TaskId}: {task.JobName} @ {task.EffectiveDate:yyyy-MM-dd}");

                ExecuteTask(task, threadLabel);

                MarkTaskSucceeded(task.TaskId);
                Console.WriteLine($"[{threadLabel}] Task {task.TaskId} SUCCEEDED: {task.JobName} @ {task.EffectiveDate:yyyy-MM-dd}");
            }
            catch (Exception ex) when (task != null)
            {
                MarkTaskFailed(task.TaskId, ex.ToString());
                Console.WriteLine($"[{threadLabel}] Task {task.TaskId} FAILED: {task.JobName} @ {task.EffectiveDate:yyyy-MM-dd} — {ex.Message}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{threadLabel}] ERROR (no task context): {ex.Message}");
            }
        }

        Console.WriteLine($"[{threadLabel}] Worker exiting.");
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
            throw; // Re-throw so the worker loop catches it and marks the queue task failed
        }
    }

    // -------------------------------------------------------------------------
    // Queue operations — each uses its own connection
    // -------------------------------------------------------------------------

    private static TaskQueueItem? ClaimNextTask(string executionMode)
    {
        using var conn = new NpgsqlConnection(ConnectionHelper.GetConnectionString());
        conn.Open();
        using var cmd = new NpgsqlCommand(@"
            UPDATE control.task_queue
            SET status = 'Running', started_at = NOW()
            WHERE task_id = (
                SELECT task_id FROM control.task_queue
                WHERE status = 'Pending' AND execution_mode = @mode
                ORDER BY task_id
                FOR UPDATE SKIP LOCKED
                LIMIT 1
            )
            RETURNING task_id, job_name, effective_date, execution_mode;", conn);
        cmd.Parameters.AddWithValue("mode", executionMode);

        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return null;

        return new TaskQueueItem
        {
            TaskId = reader.GetInt32(0),
            JobName = reader.GetString(1),
            EffectiveDate = DateOnly.FromDateTime(reader.GetDateTime(2)),
            ExecutionMode = reader.GetString(3)
        };
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
    public string ExecutionMode { get; init; } = "";
}