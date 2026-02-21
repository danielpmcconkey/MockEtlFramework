using System.Text.Json;

namespace Lib.Control;

/// <summary>
/// Orchestrates the execution of registered ETL jobs.
///
/// Effective date handling:
///   - The executor computes effective dates automatically from each job's run history.
///   - On first run (no prior Succeeded rows), the start date is read from the job conf's
///     firstEffectiveDate field.
///   - On subsequent runs, the start date is (last Succeeded max_effective_date + 1 day).
///   - The end date is always today. The executor gap-fills one day at a time until caught up.
///   - Effective dates are injected into shared state before the pipeline runs, under the
///     reserved keys DataSourcing.MinDateKey and DataSourcing.MaxDateKey.
///
/// run_date in control.job_runs is always set to today's date (the actual execution date),
/// regardless of which effective date is being processed.
///
/// When effectiveDateOverride is supplied (backfill / rerun), only that single date is
/// executed and gap-fill is skipped.
/// </summary>
public class JobExecutorService
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public void Run(DateOnly? effectiveDateOverride = null, string? specificJobName = null)
    {
        DateOnly runDate = DateOnly.FromDateTime(DateTime.Today);

        Console.WriteLine($"[JobExecutorService] run_date = {runDate:yyyy-MM-dd}" +
                          (effectiveDateOverride.HasValue ? $", effective_date = {effectiveDateOverride:yyyy-MM-dd}" : " (auto-advance)") +
                          (specificJobName != null ? $", job = {specificJobName}" : ""));

        var allJobs       = ControlDb.GetActiveJobs();
        var allDeps       = ControlDb.GetAllDependencies();
        var succeededToday = ControlDb.GetSucceededJobIds(runDate);
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

        var plan = ExecutionPlan.Build(jobsToConsider, allDeps, succeededToday, everSucceeded);

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

            IEnumerable<DateOnly> effectiveDates = effectiveDateOverride.HasValue
                ? [effectiveDateOverride.Value]
                : GetPendingEffectiveDates(job);

            bool jobFailed = false;

            foreach (var effDate in effectiveDates)
            {
                if (jobFailed) break; // don't advance past a failure

                int attemptNum = ControlDb.GetNextAttemptNumber(job.JobId, effDate, effDate);
                int runId      = ControlDb.InsertRun(job.JobId, runDate, effDate, effDate, attemptNum, "scheduler");
                ControlDb.MarkRunning(runId);

                Console.WriteLine($"[JobExecutorService] Running '{job.JobName}' " +
                                  $"eff={effDate:yyyy-MM-dd} (run_id={runId}, attempt={attemptNum})...");
                try
                {
                    var initialState = new Dictionary<string, object>
                    {
                        [Modules.DataSourcing.MinDateKey] = effDate,
                        [Modules.DataSourcing.MaxDateKey] = effDate
                    };

                    var runner     = new JobRunner();
                    var finalState = runner.Run(job.JobConfPath, initialState);

                    int? rowsProcessed = null;
                    var frames = finalState.Values.OfType<DataFrames.DataFrame>().ToList();
                    if (frames.Count > 0)
                        rowsProcessed = frames.Sum(f => f.Count);

                    ControlDb.MarkSucceeded(runId, rowsProcessed);
                    Console.WriteLine($"[JobExecutorService] '{job.JobName}' {effDate:yyyy-MM-dd} succeeded.");
                }
                catch (Exception ex)
                {
                    ControlDb.MarkFailed(runId, ex.ToString());
                    jobFailed = true;
                    failedThisRun.Add(job.JobId);
                    Console.WriteLine($"[JobExecutorService] '{job.JobName}' {effDate:yyyy-MM-dd} FAILED: {ex.Message}");
                }
            }
        }

        int failures = failedThisRun.Count;
        Console.WriteLine(failures == 0
            ? "[JobExecutorService] All jobs completed successfully."
            : $"[JobExecutorService] Done. {failures} job(s) failed.");
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns the sequence of effective dates this job still needs to process,
    /// from (last Succeeded max_effective_date + 1 day) up to and including today.
    /// Uses firstEffectiveDate from the job conf when no prior successful runs exist.
    /// </summary>
    private static IEnumerable<DateOnly> GetPendingEffectiveDates(JobRegistration job)
    {
        var lastMax   = ControlDb.GetLastSucceededMaxEffectiveDate(job.JobId);
        DateOnly today = DateOnly.FromDateTime(DateTime.Today);

        DateOnly startDate;
        if (lastMax.HasValue)
        {
            startDate = lastMax.Value.AddDays(1);
        }
        else
        {
            var conf = ReadJobConf(job.JobConfPath);
            startDate = conf.FirstEffectiveDate
                ?? throw new InvalidOperationException(
                    $"Job '{job.JobName}' has no prior successful runs and no firstEffectiveDate " +
                    $"in its job conf. Add a firstEffectiveDate field to '{job.JobConfPath}'.");
        }

        if (startDate > today)
        {
            Console.WriteLine($"[JobExecutorService] '{job.JobName}' is already up to date (last max = {lastMax:yyyy-MM-dd}).");
            yield break;
        }

        for (var d = startDate; d <= today; d = d.AddDays(1))
            yield return d;
    }

    private static JobConf ReadJobConf(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<JobConf>(json, JsonOpts)
            ?? throw new InvalidOperationException($"Failed to deserialize job conf at '{path}'.");
    }
}
