namespace Lib.Tests;

public class AppConfigTests
{
    // --- AppConfig defaults ---

    [Fact]
    public void AppConfig_Default_HasDatabaseSettings()
    {
        var config = new AppConfig();
        Assert.NotNull(config.Database);
    }

    [Fact]
    public void AppConfig_Default_HasTaskQueueSettings()
    {
        var config = new AppConfig();
        Assert.NotNull(config.TaskQueue);
    }

    // --- DatabaseSettings defaults ---

    [Fact]
    public void DatabaseSettings_Default_Host()
    {
        var db = new DatabaseSettings();
        Assert.Equal("localhost", db.Host);
    }

    [Fact]
    public void DatabaseSettings_Default_Username()
    {
        var db = new DatabaseSettings();
        Assert.Equal("claude", db.Username);
    }

    [Fact]
    public void DatabaseSettings_Default_Password_ReadsEnvVar()
    {
        var original = Environment.GetEnvironmentVariable("ETL_DB_PASSWORD");
        try
        {
            Environment.SetEnvironmentVariable("ETL_DB_PASSWORD", "test_pw");
            var db = new DatabaseSettings();
            Assert.Equal("test_pw", db.Password);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ETL_DB_PASSWORD", original);
        }
    }

    [Fact]
    public void DatabaseSettings_Default_Password_EmptyWhenEnvVarMissing()
    {
        var original = Environment.GetEnvironmentVariable("ETL_DB_PASSWORD");
        try
        {
            Environment.SetEnvironmentVariable("ETL_DB_PASSWORD", null);
            var db = new DatabaseSettings();
            Assert.Equal("", db.Password);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ETL_DB_PASSWORD", original);
        }
    }

    [Fact]
    public void DatabaseSettings_Default_DatabaseName()
    {
        var db = new DatabaseSettings();
        Assert.Equal("atc", db.DatabaseName);
    }

    [Fact]
    public void DatabaseSettings_Default_Timeout()
    {
        var db = new DatabaseSettings();
        Assert.Equal(15, db.Timeout);
    }

    [Fact]
    public void DatabaseSettings_Default_CommandTimeout()
    {
        var db = new DatabaseSettings();
        Assert.Equal(300, db.CommandTimeout);
    }

    // --- TaskQueueSettings defaults ---

    [Fact]
    public void TaskQueueSettings_Default_ThreadCount()
    {
        var tq = new TaskQueueSettings();
        Assert.Equal(5, tq.ThreadCount);
    }

    [Fact]
    public void TaskQueueSettings_Default_PollIntervalMs()
    {
        var tq = new TaskQueueSettings();
        Assert.Equal(5000, tq.PollIntervalMs);
    }

    [Fact]
    public void TaskQueueSettings_Default_IdleCheckIntervalMs()
    {
        var tq = new TaskQueueSettings();
        Assert.Equal(30_000, tq.IdleCheckIntervalMs);
    }

    [Fact]
    public void TaskQueueSettings_Default_MaxIdleCycles()
    {
        var tq = new TaskQueueSettings();
        Assert.Equal(960, tq.MaxIdleCycles);
    }

    [Fact]
    public void DatabaseSettings_Password_IgnoresAppsettingsJson()
    {
        var original = Environment.GetEnvironmentVariable("ETL_DB_PASSWORD");
        try
        {
            Environment.SetEnvironmentVariable("ETL_DB_PASSWORD", "from_env");

            var json = """{ "Database": { "Password": "from_json" } }""";
            var opts = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var config = System.Text.Json.JsonSerializer.Deserialize<AppConfig>(json, opts)!;

            Assert.Equal("from_env", config.Database.Password);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ETL_DB_PASSWORD", original);
        }
    }

    // --- ConnectionHelper ---

    [Fact]
    public void ConnectionHelper_Initialize_ProducesCorrectConnectionString()
    {
        var original = Environment.GetEnvironmentVariable("ETL_DB_PASSWORD");
        try
        {
            Environment.SetEnvironmentVariable("ETL_DB_PASSWORD", "s3cret");
            var config = new AppConfig
            {
                Database = new DatabaseSettings
                {
                    Host = "10.0.0.5",
                    Username = "testuser",
                    DatabaseName = "testdb",
                    Timeout = 10,
                    CommandTimeout = 120
                }
            };

            ConnectionHelper.Initialize(config);
            var cs = ConnectionHelper.GetConnectionString();

            Assert.Equal(
                "Host=10.0.0.5;Username=testuser;Password='s3cret';Database=testdb;Timeout=10;Command Timeout=120;",
                cs);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ETL_DB_PASSWORD", original);
        }
    }

    [Fact]
    public void ConnectionHelper_DefaultConfig_UsesDefaultValues()
    {
        var original = Environment.GetEnvironmentVariable("ETL_DB_PASSWORD");
        try
        {
            Environment.SetEnvironmentVariable("ETL_DB_PASSWORD", "required");
            var config = new AppConfig();

            ConnectionHelper.Initialize(config);
            var cs = ConnectionHelper.GetConnectionString();

            Assert.Contains("Host=localhost;", cs);
            Assert.Contains("Username=claude;", cs);
            Assert.Contains("Password='required';", cs);
            Assert.Contains("Database=atc;", cs);
            Assert.Contains("Timeout=15;", cs);
            Assert.Contains("Command Timeout=300;", cs);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ETL_DB_PASSWORD", original);
        }
    }

    [Fact]
    public void ConnectionHelper_PasswordWithSpecialChars_IsIncludedVerbatim()
    {
        var original = Environment.GetEnvironmentVariable("ETL_DB_PASSWORD");
        try
        {
            Environment.SetEnvironmentVariable("ETL_DB_PASSWORD", "p@ss'w0rd!");
            var config = new AppConfig();

            ConnectionHelper.Initialize(config);
            var cs = ConnectionHelper.GetConnectionString();

            Assert.Contains("Password='p@ss'w0rd!';", cs);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ETL_DB_PASSWORD", original);
        }
    }
}
