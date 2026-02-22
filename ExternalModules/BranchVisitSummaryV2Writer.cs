using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

public class BranchVisitSummaryV2Writer : IExternalStep
{
    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var df = (DataFrame)sharedState["visit_summary"];
        DscWriterUtil.Write("branch_visit_summary", false, df);
        sharedState["output"] = df;
        return sharedState;
    }
}
