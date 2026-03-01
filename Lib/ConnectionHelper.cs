namespace Lib;

public static class ConnectionHelper
{
    public static string GetConnectionString()
    {
        return "Host=172.18.0.1;Username=claude;Password='claude';Database=atc;" +
               "Timeout=15;Command Timeout=300;";
    }
}
