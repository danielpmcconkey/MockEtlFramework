using System.Diagnostics;
using Lib.Control;

namespace JobExecutor;

/// <summary>
/// Usage:
///   JobExecutor --service                 — long-running queue executor (polls control.task_queue)
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
        // --service mode: long-running queue executor
        if (args.Length >= 1 && args[0] == "--service")
        {
            var queueService = new TaskQueueService();
            var sw = Stopwatch.StartNew();
            queueService.Run();
            sw.Stop();
            Console.WriteLine($"Queue execution completed in {sw.ElapsedMilliseconds}ms.");
            return;
        }

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
        var sw2 = Stopwatch.StartNew();
        service.Run(effectiveDate, jobName);
        sw2.Stop();
        Console.WriteLine($"Job execution completed in {sw2.ElapsedMilliseconds}ms.");
    }
}
