using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

public class DailyTransactionVolumeV2Writer : IExternalStep
{
    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var df = (DataFrame)sharedState["daily_vol"];
        DscWriterUtil.Write("daily_transaction_volume", false, df);
        sharedState["output"] = df;
        return sharedState;
    }
}
