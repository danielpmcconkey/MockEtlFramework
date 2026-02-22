using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

public class MonthlyTransactionTrendV2Writer : IExternalStep
{
    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var df = (DataFrame)sharedState["monthly_trend"];
        DscWriterUtil.Write("monthly_transaction_trend", false, df);
        sharedState["output"] = df;
        return sharedState;
    }
}
