using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

public class DailyTransactionSummaryV2Writer : IExternalStep
{
    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var df = (DataFrame)sharedState["daily_txn_summary"];
        DscWriterUtil.Write("daily_transaction_summary", false, df);
        sharedState["output"] = df;
        return sharedState;
    }
}
