using Lib;

namespace JobExecutor;

class Program
{
    static void Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.WriteLine("Usage: JobExecutor <path-to-job-conf.json>");
            return;
        }

        var jobConfPath = args[0];

        if (!File.Exists(jobConfPath))
        {
            Console.WriteLine($"Job conf file not found: '{jobConfPath}'");
            return;
        }

        var runner = new JobRunner();
        runner.Run(jobConfPath);
    }
}
