namespace Lib;

public static class ConnectionHelper
{
    private static DatabaseSettings _settings = new();

    public static void Initialize(AppConfig config)
    {
        _settings = config.Database;
    }

    public static string GetConnectionString()
    {
        return $"Host={_settings.Host};Username={_settings.Username};Password='{_settings.Password}';" +
               $"Database={_settings.DatabaseName};Timeout={_settings.Timeout};Command Timeout={_settings.CommandTimeout};";
    }
}
