using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

public class AccountStatusSummaryV2Processor : IExternalStep
{
    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var outputColumns = new List<string>
        {
            "account_type", "account_status", "account_count", "as_of"
        };

        var accounts = sharedState.ContainsKey("accounts") ? sharedState["accounts"] as DataFrame : null;

        if (accounts == null || accounts.Count == 0)
        {
            sharedState["output"] = new DataFrame(new List<Row>(), outputColumns);
            return sharedState;
        }

        // Get as_of from first account row
        var asOf = accounts.Rows[0]["as_of"];

        // Build (account_type, account_status) -> count dictionary
        var counts = new Dictionary<(string type, string status), int>();
        foreach (var acctRow in accounts.Rows)
        {
            var accountType = acctRow["account_type"]?.ToString() ?? "";
            var accountStatus = acctRow["account_status"]?.ToString() ?? "";
            var key = (accountType, accountStatus);

            if (!counts.ContainsKey(key))
                counts[key] = 0;
            counts[key]++;
        }

        var outputRows = new List<Row>();
        foreach (var kvp in counts)
        {
            outputRows.Add(new Row(new Dictionary<string, object?>
            {
                ["account_type"] = kvp.Key.type,
                ["account_status"] = kvp.Key.status,
                ["account_count"] = kvp.Value,
                ["as_of"] = asOf
            }));
        }

        var df = new DataFrame(outputRows, outputColumns);
        DscWriterUtil.Write("account_status_summary", true, df);
        sharedState["output"] = df;
        return sharedState;
    }
}
