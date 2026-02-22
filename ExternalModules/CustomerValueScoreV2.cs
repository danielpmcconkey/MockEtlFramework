using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

/// <summary>
/// V2 of CustomerValueScore: computes composite value scores for customers based on
/// transactions, account balances, and branch visits.
/// Uses C# decimal arithmetic for banker's rounding consistency with original.
/// </summary>
public class CustomerValueScoreV2 : IExternalStep
{
    private static readonly List<string> OutputColumns = new()
    {
        "customer_id", "first_name", "last_name", "transaction_score", "balance_score",
        "visit_score", "composite_score", "as_of"
    };

    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var customers = sharedState.ContainsKey("customers")
            ? sharedState["customers"] as DataFrame
            : null;

        if (customers == null || customers.Count == 0)
        {
            sharedState["score_output"] = new DataFrame(new List<Row>(), OutputColumns);
            return sharedState;
        }

        var accounts = sharedState.ContainsKey("accounts")
            ? sharedState["accounts"] as DataFrame
            : null;
        var transactions = sharedState.ContainsKey("transactions")
            ? sharedState["transactions"] as DataFrame
            : null;
        var branchVisits = sharedState.ContainsKey("branch_visits")
            ? sharedState["branch_visits"] as DataFrame
            : null;

        // Build account_id -> customer_id lookup
        var accountToCustomer = new Dictionary<int, int>();
        if (accounts != null)
        {
            foreach (var acctRow in accounts.Rows)
            {
                var accountId = Convert.ToInt32(acctRow["account_id"]);
                var customerId = Convert.ToInt32(acctRow["customer_id"]);
                accountToCustomer[accountId] = customerId;
            }
        }

        // Transaction counts per customer
        var txnCountByCustomer = new Dictionary<int, int>();
        if (transactions != null)
        {
            foreach (var txnRow in transactions.Rows)
            {
                var accountId = Convert.ToInt32(txnRow["account_id"]);
                var customerId = accountToCustomer.GetValueOrDefault(accountId, 0);
                if (customerId == 0) continue;
                txnCountByCustomer.TryGetValue(customerId, out var count);
                txnCountByCustomer[customerId] = count + 1;
            }
        }

        // Total balance per customer
        var balanceByCustomer = new Dictionary<int, decimal>();
        if (accounts != null)
        {
            foreach (var acctRow in accounts.Rows)
            {
                var customerId = Convert.ToInt32(acctRow["customer_id"]);
                var balance = Convert.ToDecimal(acctRow["current_balance"]);
                balanceByCustomer.TryGetValue(customerId, out var total);
                balanceByCustomer[customerId] = total + balance;
            }
        }

        // Visit counts per customer
        var visitCountByCustomer = new Dictionary<int, int>();
        if (branchVisits != null)
        {
            foreach (var visitRow in branchVisits.Rows)
            {
                var customerId = Convert.ToInt32(visitRow["customer_id"]);
                visitCountByCustomer.TryGetValue(customerId, out var count);
                visitCountByCustomer[customerId] = count + 1;
            }
        }

        // Scoring weights (decimal for exact arithmetic)
        const decimal transactionWeight = 0.4m;
        const decimal balanceWeight = 0.35m;
        const decimal visitWeight = 0.25m;

        var outputRows = new List<Row>();
        foreach (var custRow in customers.Rows)
        {
            var customerId = Convert.ToInt32(custRow["id"]);
            var firstName = custRow["first_name"]?.ToString() ?? "";
            var lastName = custRow["last_name"]?.ToString() ?? "";

            var txnCount = txnCountByCustomer.GetValueOrDefault(customerId, 0);
            var transactionScore = Math.Min(txnCount * 10.0m, 1000m);

            var totalBalance = balanceByCustomer.GetValueOrDefault(customerId, 0m);
            var balanceScore = Math.Min(totalBalance / 1000.0m, 1000m);

            var visitCount = visitCountByCustomer.GetValueOrDefault(customerId, 0);
            var visitScore = Math.Min(visitCount * 50.0m, 1000m);

            var compositeScore = transactionScore * transactionWeight
                               + balanceScore * balanceWeight
                               + visitScore * visitWeight;

            outputRows.Add(new Row(new Dictionary<string, object?>
            {
                ["customer_id"] = customerId,
                ["first_name"] = firstName,
                ["last_name"] = lastName,
                ["transaction_score"] = Math.Round(transactionScore, 2),
                ["balance_score"] = Math.Round(balanceScore, 2),
                ["visit_score"] = Math.Round(visitScore, 2),
                ["composite_score"] = Math.Round(compositeScore, 2),
                ["as_of"] = custRow["as_of"]
            }));
        }

        sharedState["score_output"] = new DataFrame(outputRows, OutputColumns);
        return sharedState;
    }
}
