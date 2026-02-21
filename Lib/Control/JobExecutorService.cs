namespace Lib.Control;

/// <summary>
/// Orchestrates the execution of registered ETL jobs for a given run_date.
///
/// For each run_date:
///   1. Loads active jobs and dependency graph from control schema.
///   2. Excludes jobs that already have a Succeeded run for this run_date.
///   3. Produces a topologically sorted execution plan.
///   4. Runs each job in order, recording Pending → Running → Succeeded/Failed in control.job_runs.
///   5. Any job whose SameDay upstream dependency failed this invocation is recorded as Skipped.
/// </summary>
public class JobExecutorService
{
    public void Run(DateOnly runDate, string? specificJobName = null)
    {
        Console.WriteLine($"[JobExecutorService] run_date = {runDate:yyyy-MM-dd}" +
                          (specificJobName != null ? $", job = {specificJobName}" : ""));

        var allJobs        = ControlDb.GetActiveJobs();
        var allDeps        = ControlDb.GetAllDependencies();
        var succeededToday = ControlDb.GetSucceededJobIds(runDate);
        var everSucceeded  = ControlDb.GetEverSucceededJobIds();

        // Narrow to a single job when requested.
        List<JobRegistration> jobsToConsider;
        if (specificJobName != null)
        {
            var match = allJobs.FirstOrDefault(j =>
                j.JobName.Equals(specificJobName, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException(
                    $"No active job found with name '{specificJobName}'.");
            jobsToConsider = [match];
        }
        else
        {
            jobsToConsider = allJobs;
        }

        var plan = ExecutionPlan.Build(jobsToConsider, allDeps, succeededToday, everSucceeded);

        if (plan.Count == 0)
        {
            Console.WriteLine("[JobExecutorService] Nothing to run — all jobs already succeeded for this run_date.");
            return;
        }

        Console.WriteLine($"[JobExecutorService] {plan.Count} job(s) in plan.");

        // Track jobs that fail during this invocation so their SameDay dependents can be skipped.
        var failedThisRun = new HashSet<int>();

        foreach (var job in plan)
        {
            // Skip if any SameDay upstream failed during this invocation.
            bool upstreamFailed = allDeps
                .Where(d => d.JobId == job.JobId && d.DependencyType == "SameDay")
                .Any(d => failedThisRun.Contains(d.DependsOnJobId));

            if (upstreamFailed)
            {
                Console.WriteLine($"[JobExecutorService] Skipping '{job.JobName}' — SameDay upstream failed.");
                int skipAttempt = ControlDb.GetNextAttemptNumber(job.JobId, runDate);
                int skipRunId   = ControlDb.InsertRun(job.JobId, runDate, skipAttempt, "dependency");
                ControlDb.MarkSkipped(skipRunId);
                continue;
            }

            int attemptNum = ControlDb.GetNextAttemptNumber(job.JobId, runDate);
            int runId      = ControlDb.InsertRun(job.JobId, runDate, attemptNum, "scheduler");
            ControlDb.MarkRunning(runId);

            Console.WriteLine($"[JobExecutorService] Running '{job.JobName}' " +
                              $"(run_id={runId}, attempt={attemptNum})...");
            try
            {
                var runner     = new JobRunner();
                var finalState = runner.Run(job.JobConfPath);

                // rows_processed: count rows in any DataFrame present in final shared state.
                int? rowsProcessed = null;
                var frames = finalState.Values.OfType<DataFrames.DataFrame>().ToList();
                if (frames.Count > 0)
                    rowsProcessed = frames.Sum(f => f.Count);

                ControlDb.MarkSucceeded(runId, rowsProcessed);
                Console.WriteLine($"[JobExecutorService] '{job.JobName}' succeeded.");
            }
            catch (Exception ex)
            {
                ControlDb.MarkFailed(runId, ex.ToString());
                failedThisRun.Add(job.JobId);
                Console.WriteLine($"[JobExecutorService] '{job.JobName}' FAILED: {ex.Message}");
            }
        }

        int failures = failedThisRun.Count;
        Console.WriteLine(failures == 0
            ? "[JobExecutorService] All jobs completed successfully."
            : $"[JobExecutorService] Done. {failures} job(s) failed.");
    }
}
