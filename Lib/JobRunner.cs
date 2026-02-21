using System.Text.Json;

namespace Lib;

/// <summary>
/// Reads a job configuration JSON file, builds the module pipeline via ModuleFactory,
/// and executes each module in series, threading shared state from one to the next.
/// </summary>
public class JobRunner
{
    public Dictionary<string, object> Run(string jobConfPath,
        Dictionary<string, object>? initialState = null)
    {
        var json = File.ReadAllText(jobConfPath);

        var jobConf = JsonSerializer.Deserialize<JobConf>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? throw new InvalidOperationException($"Failed to deserialize job conf at '{jobConfPath}'.");

        Console.WriteLine($"[JobRunner] Starting job: {jobConf.JobName}");

        var sharedState = initialState != null
            ? new Dictionary<string, object>(initialState)
            : new Dictionary<string, object>();

        foreach (var moduleElement in jobConf.Modules)
        {
            var type = moduleElement.GetProperty("type").GetString();
            Console.WriteLine($"[JobRunner]   Executing module: {type}");

            var module = ModuleFactory.Create(moduleElement);
            sharedState = module.Execute(sharedState);
        }

        Console.WriteLine($"[JobRunner] Job complete: {jobConf.JobName}");
        return sharedState;
    }
}
