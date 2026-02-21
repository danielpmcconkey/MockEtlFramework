using Lib.Control;

namespace JobExecutor;

/// <summary>
/// Usage:
///   JobExecutor                           — auto-advance all active jobs
///   JobExecutor &lt;job_name&gt;               — auto-advance one specific job
///   JobExecutor &lt;effective_date&gt;          — run exactly that date, all jobs  (backfill)
///   JobExecutor &lt;effective_date&gt; &lt;job_name&gt; — run exactly that date, one job (backfill)
///
/// effective_date format: yyyy-MM-dd
/// If the first argument parses as a date it is treated as an effective_date override;
/// otherwise it is treated as a job name and auto-advance mode is used.
/// </summary>
class Program
{
    static void Main(string[] args)
    {
        DateOnly? effectiveDate = null;
        string?   jobName      = null;

        if (args.Length >= 1)
        {
            if (DateOnly.TryParseExact(args[0], "yyyy-MM-dd",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None,
                    out var parsed))
            {
                effectiveDate = parsed;
                jobName = args.Length >= 2 ? args[1] : null;
            }
            else
            {
                // First arg is not a date — treat it as a job name.
                jobName = args[0];
            }
        }

        var service = new JobExecutorService();
        service.Run(effectiveDate, jobName);
    }
}
