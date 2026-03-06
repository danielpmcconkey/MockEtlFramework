using System.Text.Json;

namespace Lib.Control;

/// <summary>
/// Orchestrates the execution of registered ETL jobs for a single effective date.
///
/// Effective date handling:
///   - The caller MUST supply an effective date. There is no auto-advance or gap-fill.
///   - The executor runs exactly one effective date per invocation.
///   - Effective dates are injected into shared state before the pipeline runs, under the
///     reserved key DataSourcing.EtlEffectiveDateKey.
///
/// run_date in control.job_runs is always set to today's date (the actual execution date),
/// regardless of which effective date is being processed.
/// </summary>
public class JobExecutorService
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public void Run(DateOnly effectiveDate, string? specificJobName = null)
    {
        DateOnly runDate = DateOnly.FromDateTime(DateTime.Today);

        Console.WriteLine($"[JobExecutorService] run_date = {runDate:yyyy-MM-dd}" +
                          $", effective_date = {effectiveDate:yyyy-MM-dd}" +
                          (specificJobName != null ? $", job = {specificJobName}" : ""));

        var allJobs       = ControlDb.GetActiveJobs();
        var allDeps       = ControlDb.GetAllDependencies();
        var everSucceeded  = ControlDb.GetEverSucceededJobIds();

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

        var plan = ExecutionPlan.Build(jobsToConsider, allDeps, everSucceeded);

        if (plan.Count == 0)
        {
            Console.WriteLine("[JobExecutorService] Nothing to run.");
            return;
        }

        Console.WriteLine($"[JobExecutorService] {plan.Count} job(s) in plan.");

        var failedThisRun = new HashSet<int>();

        foreach (var job in plan)
        {
            bool upstreamFailed = allDeps
                .Where(d => d.JobId == job.JobId && d.DependencyType == "SameDay")
                .Any(d => failedThisRun.Contains(d.DependsOnJobId));

            if (upstreamFailed)
            {
                Console.WriteLine($"[JobExecutorService] Skipping '{job.JobName}' — SameDay upstream failed.");
                int skipRunId = ControlDb.InsertRun(job.JobId, runDate, null, null, 1, "dependency");
                ControlDb.MarkSkipped(skipRunId);
                continue;
            }

            int attemptNum = ControlDb.GetNextAttemptNumber(job.JobId, effectiveDate, effectiveDate);
            int runId      = ControlDb.InsertRun(job.JobId, runDate, effectiveDate, effectiveDate, attemptNum, "scheduler");
            ControlDb.MarkRunning(runId);

            Console.WriteLine($"[JobExecutorService] Running '{job.JobName}' " +
                              $"eff={effectiveDate:yyyy-MM-dd} (run_id={runId}, attempt={attemptNum})...");
            try
            {
                var initialState = new Dictionary<string, object>
                {
                    [Modules.DataSourcing.EtlEffectiveDateKey] = effectiveDate
                };

                var runner     = new JobRunner();
                var finalState = runner.Run(job.JobConfPath, initialState);

                int? rowsProcessed = null;
                var frames = finalState.Values.OfType<DataFrames.DataFrame>().ToList();
                if (frames.Count > 0)
                    rowsProcessed = frames.Sum(f => f.Count);

                ControlDb.MarkSucceeded(runId, rowsProcessed);
                Console.WriteLine($"[JobExecutorService] '{job.JobName}' {effectiveDate:yyyy-MM-dd} succeeded.");
            }
            catch (Exception ex)
            {
                ControlDb.MarkFailed(runId, ex.ToString());
                failedThisRun.Add(job.JobId);
                Console.WriteLine($"[JobExecutorService] '{job.JobName}' {effectiveDate:yyyy-MM-dd} FAILED: {ex.Message}");
            }
        }

        int failures = failedThisRun.Count;
        Console.WriteLine(failures == 0
            ? "[JobExecutorService] All jobs completed successfully."
            : $"[JobExecutorService] Done. {failures} job(s) failed.");
    }
}
