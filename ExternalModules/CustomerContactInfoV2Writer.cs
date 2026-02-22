using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

public class CustomerContactInfoV2Writer : IExternalStep
{
    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var df = (DataFrame)sharedState["contact_info"];
        DscWriterUtil.Write("customer_contact_info", false, df);
        sharedState["output"] = df;
        return sharedState;
    }
}
