using System.Text.Json;

namespace Lib;

public class JobConf
{
    public string       JobName            { get; set; } = "";
    public List<JsonElement> Modules       { get; set; } = new();
    /// <summary>
    /// The effective date used on the very first run when control.job_runs has no prior
    /// Succeeded rows for this job. Format: yyyy-MM-dd.
    /// </summary>
    public DateOnly?    FirstEffectiveDate { get; set; }
}
