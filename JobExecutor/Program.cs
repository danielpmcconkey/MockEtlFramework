using System.Diagnostics;
using Lib.Control;

namespace JobExecutor;

/// <summary>
/// Usage:
///   JobExecutor --service                         — long-running queue executor (polls control.task_queue)
///   JobExecutor &lt;effective_date&gt;                  — run all active jobs for that date
///   JobExecutor &lt;effective_date&gt; &lt;job_name&gt;       — run one job for that date
///
/// effective_date format: yyyy-MM-dd
/// A date argument is REQUIRED for non-service invocations.
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

        if (args.Length < 1 || !DateOnly.TryParseExact(args[0], "yyyy-MM-dd",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None,
                out var effectiveDate))
        {
            Console.Error.WriteLine("Error: effective date is required.");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Usage:");
            Console.Error.WriteLine("  JobExecutor --service                         — queue executor");
            Console.Error.WriteLine("  JobExecutor <effective_date>                  — all jobs for date");
            Console.Error.WriteLine("  JobExecutor <effective_date> <job_name>       — one job for date");
            Console.Error.WriteLine();
            Console.Error.WriteLine("  effective_date format: yyyy-MM-dd");
            Environment.Exit(1);
            return; // unreachable, but keeps the compiler happy
        }

        string? jobName = args.Length >= 2 ? args[1] : null;

        var service = new JobExecutorService();
        var sw2 = Stopwatch.StartNew();
        service.Run(effectiveDate, jobName);
        sw2.Stop();
        Console.WriteLine($"Job execution completed in {sw2.ElapsedMilliseconds}ms.");
    }
}
