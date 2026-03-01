using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

public class DormantAccountDetector : IExternalStep
{
    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var outputColumns = new List<string>
        {
            "account_id", "customer_id", "first_name", "last_name",
            "account_type", "current_balance", "as_of"
        };

        var accounts = sharedState.ContainsKey("accounts") ? sharedState["accounts"] as DataFrame : null;
        var transactions = sharedState.ContainsKey("transactions") ? sharedState["transactions"] as DataFrame : null;
        var customers = sharedState.ContainsKey("customers") ? sharedState["customers"] as DataFrame : null;

        if (accounts == null || accounts.Count == 0)
        {
            sharedState["output"] = new DataFrame(new List<Row>(), outputColumns);
            return sharedState;
        }

        // W2: Weekend fallback to Friday
        var maxDate = (DateOnly)sharedState["__maxEffectiveDate"];
        DateOnly targetDate = maxDate;
        if (maxDate.DayOfWeek == DayOfWeek.Saturday) targetDate = maxDate.AddDays(-1);
        else if (maxDate.DayOfWeek == DayOfWeek.Sunday) targetDate = maxDate.AddDays(-2);

        // Build set of account_ids that have transactions on the target date
        var activeAccounts = new HashSet<int>();
        if (transactions != null)
        {
            // AP6: Row-by-row iteration where SQL set operation would do
            foreach (var txnRow in transactions.Rows)
            {
                var asOf = (DateOnly)txnRow["as_of"];
                if (asOf == targetDate)
                {
                    var accountId = Convert.ToInt32(txnRow["account_id"]);
                    activeAccounts.Add(accountId);
                }
            }
        }

        // Build customer lookup
        var customerLookup = new Dictionary<int, (string firstName, string lastName)>();
        if (customers != null)
        {
            foreach (var custRow in customers.Rows)
            {
                var custId = Convert.ToInt32(custRow["id"]);
                customerLookup[custId] = (
                    custRow["first_name"]?.ToString() ?? "",
                    custRow["last_name"]?.ToString() ?? ""
                );
            }
        }

        // AP6: Row-by-row iteration to find dormant accounts
        var outputRows = new List<Row>();
        foreach (var acctRow in accounts.Rows)
        {
            var accountId = Convert.ToInt32(acctRow["account_id"]);
            var customerId = Convert.ToInt32(acctRow["customer_id"]);

            // Account is dormant if it has zero transactions on the target date
            if (!activeAccounts.Contains(accountId))
            {
                var (firstName, lastName) = customerLookup.GetValueOrDefault(customerId, ("", ""));

                outputRows.Add(new Row(new Dictionary<string, object?>
                {
                    ["account_id"] = accountId,
                    ["customer_id"] = customerId,
                    ["first_name"] = firstName,
                    ["last_name"] = lastName,
                    ["account_type"] = acctRow["account_type"],
                    ["current_balance"] = acctRow["current_balance"],
                    ["as_of"] = targetDate.ToString("yyyy-MM-dd")
                }));
            }
        }

        sharedState["output"] = new DataFrame(outputRows, outputColumns);
        return sharedState;
    }
}
