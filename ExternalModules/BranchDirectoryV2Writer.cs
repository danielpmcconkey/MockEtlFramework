using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

public class BranchDirectoryV2Writer : IExternalStep
{
    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var df = (DataFrame)sharedState["branch_dir"];
        DscWriterUtil.Write("branch_directory", true, df);
        sharedState["output"] = df;
        return sharedState;
    }
}
