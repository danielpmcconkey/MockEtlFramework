using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

public class TopBranchesV2Writer : IExternalStep
{
    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var df = (DataFrame)sharedState["top_branches"];
        DscWriterUtil.Write("top_branches", true, df);
        sharedState["output"] = df;
        return sharedState;
    }
}
