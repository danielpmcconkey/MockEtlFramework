using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

public class CustomerSegmentMapV2Writer : IExternalStep
{
    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var df = (DataFrame)sharedState["seg_map"];
        DscWriterUtil.Write("customer_segment_map", false, df);
        sharedState["output"] = df;
        return sharedState;
    }
}
