namespace Lib.Modules;

/// <summary>
/// Interface for modules that can execute operations on shared state
/// </summary>

public interface IModule
{
    /// <summary>
    /// Execute the module's operation on the provided shared state
    /// </summary>
    /// <param name="sharedState">Array of objects representing the current shared state</param>
    /// <returns>Array of objects representing the new state after execution</returns>
    Dictionary<string, object> Execute(Dictionary<string, object> sharedState);

    
}