using Npgsql;

namespace Lib.Control;

/// <summary>
/// Data access layer for the control schema. All methods open and close their own
/// connections — no connection pooling state is held between calls.
/// </summary>
internal static class ControlDb
{
    // -------------------------------------------------------------------------
    // Read
    // -------------------------------------------------------------------------

    internal static List<JobRegistration> GetActiveJobs()
    {
        var jobs = new List<JobRegistration>();
        using var conn = new NpgsqlConnection(ConnectionHelper.GetConnectionString());
        conn.Open();
        using var cmd = new NpgsqlCommand(
            "SELECT job_id, job_name, description, job_conf_path, is_active " +
            "FROM control.jobs WHERE is_active = true ORDER BY job_id",
            conn);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            jobs.Add(new JobRegistration
            {
                JobId       = reader.GetInt32(0),
                JobName     = reader.GetString(1),
                Description = reader.IsDBNull(2) ? null : reader.GetString(2),
                JobConfPath = reader.GetString(3),
                IsActive    = reader.GetBoolean(4)
            });
        }
        return jobs;
    }

    internal static List<JobDependency> GetAllDependencies()
    {
        var deps = new List<JobDependency>();
        using var conn = new NpgsqlConnection(ConnectionHelper.GetConnectionString());
        conn.Open();
        using var cmd = new NpgsqlCommand(
            "SELECT job_id, depends_on_job_id, dependency_type FROM control.job_dependencies",
            conn);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            deps.Add(new JobDependency
            {
                JobId          = reader.GetInt32(0),
                DependsOnJobId = reader.GetInt32(1),
                DependencyType = reader.GetString(2)
            });
        }
        return deps;
    }

    /// <summary>Returns job IDs that have a Succeeded run for the given run_date.</summary>
    internal static HashSet<int> GetSucceededJobIds(DateOnly runDate)
    {
        var ids = new HashSet<int>();
        using var conn = new NpgsqlConnection(ConnectionHelper.GetConnectionString());
        conn.Open();
        using var cmd = new NpgsqlCommand(
            "SELECT DISTINCT job_id FROM control.job_runs " +
            "WHERE run_date = @runDate AND status = 'Succeeded'",
            conn);
        cmd.Parameters.AddWithValue("runDate", runDate);
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) ids.Add(reader.GetInt32(0));
        return ids;
    }

    /// <summary>Returns job IDs that have ever had a Succeeded run for any run_date.</summary>
    internal static HashSet<int> GetEverSucceededJobIds()
    {
        var ids = new HashSet<int>();
        using var conn = new NpgsqlConnection(ConnectionHelper.GetConnectionString());
        conn.Open();
        using var cmd = new NpgsqlCommand(
            "SELECT DISTINCT job_id FROM control.job_runs WHERE status = 'Succeeded'",
            conn);
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) ids.Add(reader.GetInt32(0));
        return ids;
    }

    internal static int GetNextAttemptNumber(int jobId, DateOnly runDate)
    {
        using var conn = new NpgsqlConnection(ConnectionHelper.GetConnectionString());
        conn.Open();
        using var cmd = new NpgsqlCommand(
            "SELECT COALESCE(MAX(attempt_number), 0) + 1 FROM control.job_runs " +
            "WHERE job_id = @jobId AND run_date = @runDate",
            conn);
        cmd.Parameters.AddWithValue("jobId", jobId);
        cmd.Parameters.AddWithValue("runDate", runDate);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    // -------------------------------------------------------------------------
    // Write
    // -------------------------------------------------------------------------

    /// <summary>Inserts a Pending run record and returns the new run_id.</summary>
    internal static int InsertRun(int jobId, DateOnly runDate, int attemptNumber, string triggeredBy)
    {
        using var conn = new NpgsqlConnection(ConnectionHelper.GetConnectionString());
        conn.Open();
        using var cmd = new NpgsqlCommand(
            "INSERT INTO control.job_runs (job_id, run_date, attempt_number, status, triggered_by) " +
            "VALUES (@jobId, @runDate, @attemptNumber, 'Pending', @triggeredBy) RETURNING run_id",
            conn);
        cmd.Parameters.AddWithValue("jobId",         jobId);
        cmd.Parameters.AddWithValue("runDate",        runDate);
        cmd.Parameters.AddWithValue("attemptNumber",  attemptNumber);
        cmd.Parameters.AddWithValue("triggeredBy",    triggeredBy);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    internal static void MarkRunning(int runId)
    {
        using var conn = new NpgsqlConnection(ConnectionHelper.GetConnectionString());
        conn.Open();
        using var cmd = new NpgsqlCommand(
            "UPDATE control.job_runs SET status = 'Running', started_at = now() WHERE run_id = @runId",
            conn);
        cmd.Parameters.AddWithValue("runId", runId);
        cmd.ExecuteNonQuery();
    }

    internal static void MarkSucceeded(int runId, int? rowsProcessed)
    {
        using var conn = new NpgsqlConnection(ConnectionHelper.GetConnectionString());
        conn.Open();
        using var cmd = new NpgsqlCommand(
            "UPDATE control.job_runs " +
            "SET status = 'Succeeded', completed_at = now(), rows_processed = @rowsProcessed " +
            "WHERE run_id = @runId",
            conn);
        cmd.Parameters.AddWithValue("rowsProcessed", (object?)rowsProcessed ?? DBNull.Value);
        cmd.Parameters.AddWithValue("runId",          runId);
        cmd.ExecuteNonQuery();
    }

    internal static void MarkFailed(int runId, string errorMessage)
    {
        using var conn = new NpgsqlConnection(ConnectionHelper.GetConnectionString());
        conn.Open();
        using var cmd = new NpgsqlCommand(
            "UPDATE control.job_runs " +
            "SET status = 'Failed', completed_at = now(), error_message = @errorMessage " +
            "WHERE run_id = @runId",
            conn);
        cmd.Parameters.AddWithValue("errorMessage", errorMessage);
        cmd.Parameters.AddWithValue("runId",        runId);
        cmd.ExecuteNonQuery();
    }

    internal static void MarkSkipped(int runId)
    {
        using var conn = new NpgsqlConnection(ConnectionHelper.GetConnectionString());
        conn.Open();
        using var cmd = new NpgsqlCommand(
            "UPDATE control.job_runs SET status = 'Skipped', completed_at = now() WHERE run_id = @runId",
            conn);
        cmd.Parameters.AddWithValue("runId", runId);
        cmd.ExecuteNonQuery();
    }
}
