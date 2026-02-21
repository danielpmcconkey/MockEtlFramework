namespace Lib.Modules;

/// <summary>
/// Interface for custom logic classes loaded by the External module.
/// Teams implement this in their own assembly to extend the ETL framework
/// with any behavior not covered by the built-in modules.
/// The class must have a public parameterless constructor.
/// </summary>
public interface IExternalStep
{
    /// <summary>
    /// Execute custom logic against the shared state.
    /// Read from and write to sharedState as needed, then return it.
    /// </summary>
    Dictionary<string, object> Execute(Dictionary<string, object> sharedState);
}
