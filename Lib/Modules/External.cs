using System.Reflection;

namespace Lib.Modules;

/// <summary>
/// Loads a user-supplied class from an external assembly at runtime and executes it.
/// The target class must implement IExternalStep and have a public parameterless constructor.
/// This allows teams to run arbitrary custom logic that the built-in modules don't cover.
/// </summary>
public class External : IModule
{
    private readonly string _assemblyPath;
    private readonly string _typeName;

    /// <param name="assemblyPath">Full file path to the external DLL.</param>
    /// <param name="typeName">Fully-qualified type name, e.g. "MyCompany.MyJob.CustomStep".</param>
    public External(string assemblyPath, string typeName)
    {
        _assemblyPath = assemblyPath ?? throw new ArgumentNullException(nameof(assemblyPath));
        _typeName = typeName ?? throw new ArgumentNullException(nameof(typeName));
    }

    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        if (!File.Exists(_assemblyPath))
            throw new FileNotFoundException($"External assembly not found: '{_assemblyPath}'.");

        var assembly = Assembly.LoadFrom(_assemblyPath);

        var type = assembly.GetType(_typeName)
            ?? throw new TypeLoadException(
                $"Type '{_typeName}' was not found in assembly '{_assemblyPath}'.");

        if (!typeof(IExternalStep).IsAssignableFrom(type))
            throw new InvalidOperationException(
                $"Type '{_typeName}' does not implement IExternalStep.");

        var instance = Activator.CreateInstance(type) as IExternalStep
            ?? throw new InvalidOperationException(
                $"Failed to create an instance of '{_typeName}'. Ensure it has a public parameterless constructor.");

        return instance.Execute(sharedState);
    }
}
