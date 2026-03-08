using System.Diagnostics;
using Npgsql;
using Lib;

namespace QueueLoader;

/// <summary>
/// Loads control.task_queue in dependency-safe tiers.
///
/// Usage:
///   QueueLoader 2024-10-01 2024-12-31           — queue all active jobs for the date range
///   QueueLoader 2024-10-01 2024-12-31 --dry-run  — show tiers without inserting
///
/// Requires ETL_DB_PASSWORD env var.
/// </summary>
class Program
{
    static int Main(string[] args)
    {
        if (args.Length < 2
            || !DateOnly.TryParseExact(args[0], "yyyy-MM-dd", out var startDate)
            || !DateOnly.TryParseExact(args[1], "yyyy-MM-dd", out var endDate))
        {
            Console.Error.WriteLine("Usage: QueueLoader <start_date> <end_date> [--dry-run]");
            Console.Error.WriteLine("  Dates: yyyy-MM-dd");
            return 1;
        }

        if (endDate < startDate)
        {
            Console.Error.WriteLine("End date must be >= start date.");
            return 1;
        }

        bool dryRun = args.Length >= 3 && args[2] == "--dry-run";

        var config = new AppConfig();
        if (string.IsNullOrEmpty(config.Database.Password))
        {
            Console.Error.WriteLine("Error: Set ETL_DB_PASSWORD environment variable.");
            return 1;
        }

        ConnectionHelper.Initialize(config);
        var connStr = ConnectionHelper.GetConnectionString();

        // 1. Load active jobs
        var jobs = LoadActiveJobs(connStr);
        Console.WriteLine($"Active jobs: {jobs.Count}");

        // 2. Load dependencies
        var deps = LoadDependencies(connStr);
        Console.WriteLine($"Dependencies: {deps.Count}");

        // 3. Build tiers
        var tiers = BuildTiers(jobs, deps);
        Console.WriteLine($"Tiers: {tiers.Count}");
        Console.WriteLine();

        for (int i = 0; i < tiers.Count; i++)
        {
            var tier = tiers[i];
            Console.WriteLine($"--- Tier {i} ({tier.Count} jobs) ---");
            foreach (var j in tier.OrderBy(j => j.JobName))
                Console.WriteLine($"  {j.JobId,4}  {j.JobName}");
        }

        Console.WriteLine();

        if (dryRun)
        {
            Console.WriteLine("[Dry run] No tasks inserted.");
            return 0;
        }

        int totalDays = endDate.DayNumber - startDate.DayNumber + 1;
        var totalSw = Stopwatch.StartNew();

        for (int i = 0; i < tiers.Count; i++)
        {
            var tierSw = Stopwatch.StartNew();
            var tier = tiers[i];
            int tasksInserted = InsertTier(connStr, tier, startDate, endDate);
            Console.WriteLine($"Tier {i}: inserted {tasksInserted} tasks ({tier.Count} jobs x {totalDays} days). Waiting...");

            WaitForTier(connStr, tier, startDate, endDate);
            tierSw.Stop();

            var (succeeded, failed) = CountResults(connStr, tier, startDate, endDate);
            Console.WriteLine($"Tier {i} complete: {succeeded} succeeded, {failed} failed. [{tierSw.Elapsed:h\\:mm\\:ss}]");

            if (failed > 0)
            {
                Console.Error.WriteLine($"WARNING: {failed} failures in tier {i}. Continuing to next tier.");
            }

            Console.WriteLine();
        }

        totalSw.Stop();
        Console.WriteLine($"All tiers complete. Total elapsed: {totalSw.Elapsed:h\\:mm\\:ss}");
        return 0;
    }

    record Job(int JobId, string JobName);
    record Dep(int JobId, int DependsOnJobId);

    static List<Job> LoadActiveJobs(string connStr)
    {
        var jobs = new List<Job>();
        using var conn = new NpgsqlConnection(connStr);
        conn.Open();
        using var cmd = new NpgsqlCommand(
            "SELECT job_id, job_name FROM control.jobs WHERE is_active = true ORDER BY job_id", conn);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            jobs.Add(new Job(reader.GetInt32(0), reader.GetString(1)));
        return jobs;
    }

    static List<Dep> LoadDependencies(string connStr)
    {
        var deps = new List<Dep>();
        using var conn = new NpgsqlConnection(connStr);
        conn.Open();
        using var cmd = new NpgsqlCommand(
            "SELECT job_id, depends_on_job_id FROM control.job_dependencies", conn);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            deps.Add(new Dep(reader.GetInt32(0), reader.GetInt32(1)));
        return deps;
    }

    static List<List<Job>> BuildTiers(List<Job> jobs, List<Dep> deps)
    {
        var activeIds = new HashSet<int>(jobs.Select(j => j.JobId));
        var jobMap = jobs.ToDictionary(j => j.JobId);

        // Only keep deps where both sides are active
        var activeDeps = deps.Where(d => activeIds.Contains(d.JobId) && activeIds.Contains(d.DependsOnJobId)).ToList();

        // Build in-degree map
        var inDegree = jobs.ToDictionary(j => j.JobId, _ => 0);
        var dependents = new Dictionary<int, List<int>>();

        foreach (var d in activeDeps)
        {
            inDegree[d.JobId]++;
            if (!dependents.ContainsKey(d.DependsOnJobId))
                dependents[d.DependsOnJobId] = new List<int>();
            dependents[d.DependsOnJobId].Add(d.JobId);
        }

        var tiers = new List<List<Job>>();
        var remaining = new HashSet<int>(activeIds);

        while (remaining.Count > 0)
        {
            var tier = remaining.Where(id => inDegree[id] == 0).Select(id => jobMap[id]).ToList();

            if (tier.Count == 0)
                throw new InvalidOperationException("Circular dependency detected in job_dependencies.");

            tiers.Add(tier);

            foreach (var j in tier)
            {
                remaining.Remove(j.JobId);
                if (dependents.TryGetValue(j.JobId, out var children))
                {
                    foreach (var child in children)
                        inDegree[child]--;
                }
            }
        }

        return tiers;
    }

    static int InsertTier(string connStr, List<Job> tier, DateOnly startDate, DateOnly endDate)
    {
        using var conn = new NpgsqlConnection(connStr);
        conn.Open();

        var jobNames = tier.Select(j => j.JobName).ToArray();

        using var cmd = new NpgsqlCommand(@"
            INSERT INTO control.task_queue (job_name, effective_date, status)
            SELECT j.job_name, d.dt::date, 'Pending'
            FROM control.jobs j
            CROSS JOIN generate_series(@start::date, @end::date, '1 day') d(dt)
            WHERE j.job_name = ANY(@names)
              AND j.is_active = true
            ORDER BY d.dt, j.job_name", conn);

        cmd.Parameters.AddWithValue("start", startDate.ToDateTime(TimeOnly.MinValue));
        cmd.Parameters.AddWithValue("end", endDate.ToDateTime(TimeOnly.MinValue));
        cmd.Parameters.AddWithValue("names", jobNames);

        return cmd.ExecuteNonQuery();
    }

    static void WaitForTier(string connStr, List<Job> tier, DateOnly startDate, DateOnly endDate)
    {
        var jobNames = tier.Select(j => j.JobName).ToArray();
        int lastPending = -1;

        while (true)
        {
            using var conn = new NpgsqlConnection(connStr);
            conn.Open();

            using var cmd = new NpgsqlCommand(@"
                SELECT COUNT(*) FROM control.task_queue
                WHERE job_name = ANY(@names)
                  AND effective_date BETWEEN @start AND @end
                  AND status IN ('Pending', 'Running')", conn);

            cmd.Parameters.AddWithValue("names", jobNames);
            cmd.Parameters.AddWithValue("start", startDate.ToDateTime(TimeOnly.MinValue));
            cmd.Parameters.AddWithValue("end", endDate.ToDateTime(TimeOnly.MinValue));

            int remaining = Convert.ToInt32(cmd.ExecuteScalar());

            if (remaining == 0)
                return;

            if (remaining != lastPending)
            {
                Console.WriteLine($"  ... {remaining} tasks still pending/running");
                lastPending = remaining;
            }

            Thread.Sleep(10_000);
        }
    }

    static (int succeeded, int failed) CountResults(string connStr, List<Job> tier, DateOnly startDate, DateOnly endDate)
    {
        var jobNames = tier.Select(j => j.JobName).ToArray();

        using var conn = new NpgsqlConnection(connStr);
        conn.Open();

        using var cmd = new NpgsqlCommand(@"
            SELECT status, COUNT(*) FROM control.task_queue
            WHERE job_name = ANY(@names)
              AND effective_date BETWEEN @start AND @end
            GROUP BY status", conn);

        cmd.Parameters.AddWithValue("names", jobNames);
        cmd.Parameters.AddWithValue("start", startDate.ToDateTime(TimeOnly.MinValue));
        cmd.Parameters.AddWithValue("end", endDate.ToDateTime(TimeOnly.MinValue));

        int succeeded = 0, failed = 0;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var status = reader.GetString(0);
            var count = reader.GetInt32(1);
            if (status == "Succeeded") succeeded = count;
            else if (status == "Failed") failed = count;
        }

        return (succeeded, failed);
    }
}
