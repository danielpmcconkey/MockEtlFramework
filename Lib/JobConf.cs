using System.Text.Json;

namespace Lib;

public class JobConf
{
    public string JobName { get; set; } = "";
    public List<JsonElement> Modules { get; set; } = new();
}
