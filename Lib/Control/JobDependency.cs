namespace Lib.Control;

/// <summary>
/// Represents a row from control.job_dependencies — a directed edge in the job graph.
/// JobId is the downstream job; DependsOnJobId is the upstream job that must succeed first.
/// </summary>
public class JobDependency
{
    public int    JobId          { get; init; }
    public int    DependsOnJobId { get; init; }
    public string DependencyType { get; init; } = "SameDay";
}
