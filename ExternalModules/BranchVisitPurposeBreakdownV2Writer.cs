using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

public class BranchVisitPurposeBreakdownV2Writer : IExternalStep
{
    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var df = (DataFrame)sharedState["purpose_breakdown"];
        DscWriterUtil.Write("branch_visit_purpose_breakdown", false, df);
        sharedState["output"] = df;
        return sharedState;
    }
}
