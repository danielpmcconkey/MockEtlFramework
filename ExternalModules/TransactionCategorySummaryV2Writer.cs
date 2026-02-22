using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

public class TransactionCategorySummaryV2Writer : IExternalStep
{
    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var df = (DataFrame)sharedState["txn_cat_summary"];
        DscWriterUtil.Write("transaction_category_summary", false, df);
        sharedState["output"] = df;
        return sharedState;
    }
}
