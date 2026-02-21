using Lib.Control;

namespace JobExecutor;

class Program
{
    static void Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.WriteLine("Usage: JobExecutor <run_date> [job_name]");
            Console.WriteLine("  run_date — yyyy-MM-dd");
            Console.WriteLine("  job_name — optional; runs only that job if supplied");
            return;
        }

        if (!DateOnly.TryParseExact(args[0], "yyyy-MM-dd",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None,
                out var runDate))
        {
            Console.WriteLine($"Invalid run_date '{args[0]}'. Expected format: yyyy-MM-dd");
            return;
        }

        string? jobName = args.Length >= 2 ? args[1] : null;

        var service = new JobExecutorService();
        service.Run(runDate, jobName);
    }
}
