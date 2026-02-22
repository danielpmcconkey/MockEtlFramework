namespace Lib;

public static class ConnectionHelper
{
    public static string GetConnectionString()
    {
        string? pgPassHex = Environment.GetEnvironmentVariable("PGPASS");
        if (pgPassHex == null) throw new InvalidDataException("PGPASS environment variable not found");
        var converted = Convert.FromHexString(pgPassHex);
        string password = System.Text.Encoding.Unicode.GetString(converted);
        return $"Host=localhost;Username=dansdev;Password='{password}';Database=atc;" +
               "Timeout=15;Command Timeout=300;";
    }
}
