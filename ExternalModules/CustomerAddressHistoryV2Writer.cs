using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

public class CustomerAddressHistoryV2Writer : IExternalStep
{
    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var df = (DataFrame)sharedState["addr_history"];
        DscWriterUtil.Write("customer_address_history", false, df);
        sharedState["output"] = df;
        return sharedState;
    }
}
