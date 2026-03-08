namespace Lib;

/// <summary>
/// Application-level configuration for the ETL framework.
///
/// This is the single source of truth for all config values. Consumers read
/// properties without knowing whether the value is a compiled default, an
/// appsettings.json override, or an environment variable. The sourcing
/// strategy is encapsulated here:
///
///   - Compiled default:  property initializer (e.g. Timeout = 15)
///   - appsettings.json:  deserialized into init-only properties at startup
///   - Environment var:   read once at construction, cached for the process lifetime
///
/// All values are immutable after construction. Env vars are read once and held
/// in memory — no repeated lookups.
/// </summary>
public class AppConfig
{
    public PathSettings Paths { get; init; } = new();
    public DatabaseSettings Database { get; init; } = new();
    public TaskQueueSettings TaskQueue { get; init; } = new();
}

public class PathSettings
{
    private readonly string _etlRoot = Environment.GetEnvironmentVariable("ETL_ROOT") ?? "";
    private readonly string _etlReOutput = Environment.GetEnvironmentVariable("ETL_RE_OUTPUT") ?? "";

    public string EtlRoot => _etlRoot;
    public string EtlReOutput => _etlReOutput;
}

public class DatabaseSettings
{
    private readonly string _password = Environment.GetEnvironmentVariable("ETL_DB_PASSWORD") ?? "";

    public string Host { get; init; } = "localhost";
    public string Username { get; init; } = "claude";
    public string Password => _password;
    public string DatabaseName { get; init; } = "atc";
    public int Timeout { get; init; } = 15;
    public int CommandTimeout { get; init; } = 300;
}

public class TaskQueueSettings
{
    public int ThreadCount { get; init; } = 5;
    public int PollIntervalMs { get; init; } = 5000;
    public int IdleCheckIntervalMs { get; init; } = 30_000;
    public int MaxIdleCycles { get; init; } = 960; // 960 × 30s = 8 hours
}
