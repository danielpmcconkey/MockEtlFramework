namespace Lib.Control;

/// <summary>
/// Represents a row from control.jobs — a registered ETL job.
/// </summary>
public class JobRegistration
{
    public int     JobId       { get; init; }
    public string  JobName     { get; init; } = "";
    public string? Description { get; init; }
    public string  JobConfPath { get; init; } = "";
    public bool    IsActive    { get; init; }
}
