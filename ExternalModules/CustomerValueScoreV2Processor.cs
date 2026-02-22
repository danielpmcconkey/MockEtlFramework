using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

public class CustomerValueScoreV2Processor : IExternalStep
{
    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var outputColumns = new List<string>
        {
            "customer_id", "first_name", "last_name",
            "transaction_score", "balance_score", "visit_score", "composite_score", "as_of"
        };

        var customers = sharedState.ContainsKey("customers") ? sharedState["customers"] as DataFrame : null;
        var accounts = sharedState.ContainsKey("accounts") ? sharedState["accounts"] as DataFrame : null;
        var transactions = sharedState.ContainsKey("transactions") ? sharedState["transactions"] as DataFrame : null;
        var branchVisits = sharedState.ContainsKey("branch_visits") ? sharedState["branch_visits"] as DataFrame : null;

        // Weekend guard on customers or accounts empty
        if (customers == null || customers.Count == 0 || accounts == null || accounts.Count == 0)
        {
            sharedState["output"] = new DataFrame(new List<Row>(), outputColumns);
            return sharedState;
        }

        // Scoring weights
        const decimal transactionWeight = 0.4m;
        const decimal balanceWeight = 0.35m;
        const decimal visitWeight = 0.25m;

        // Build account_id -> customer_id lookup
        var accountToCustomer = new Dictionary<int, int>();
        foreach (var acctRow in accounts.Rows)
        {
            var accountId = Convert.ToInt32(acctRow["account_id"]);
            var customerId = Convert.ToInt32(acctRow["customer_id"]);
            accountToCustomer[accountId] = customerId;
        }

        // Compute per-customer transaction counts (via account lookup)
        var txnCountByCustomer = new Dictionary<int, int>();
        if (transactions != null)
        {
            foreach (var txnRow in transactions.Rows)
            {
                var accountId = Convert.ToInt32(txnRow["account_id"]);
                var customerId = accountToCustomer.GetValueOrDefault(accountId, 0);
                if (customerId == 0) continue;

                if (!txnCountByCustomer.ContainsKey(customerId))
                    txnCountByCustomer[customerId] = 0;
                txnCountByCustomer[customerId]++;
            }
        }

        // Compute per-customer total account balance
        var balanceByCustomer = new Dictionary<int, decimal>();
        foreach (var acctRow in accounts.Rows)
        {
            var customerId = Convert.ToInt32(acctRow["customer_id"]);
            var balance = Convert.ToDecimal(acctRow["current_balance"]);

            if (!balanceByCustomer.ContainsKey(customerId))
                balanceByCustomer[customerId] = 0m;
            balanceByCustomer[customerId] += balance;
        }

        // Compute per-customer branch visit counts
        var visitCountByCustomer = new Dictionary<int, int>();
        if (branchVisits != null)
        {
            foreach (var visitRow in branchVisits.Rows)
            {
                var customerId = Convert.ToInt32(visitRow["customer_id"]);

                if (!visitCountByCustomer.ContainsKey(customerId))
                    visitCountByCustomer[customerId] = 0;
                visitCountByCustomer[customerId]++;
            }
        }

        // Iterate customers, compute scores
        var outputRows = new List<Row>();
        foreach (var custRow in customers.Rows)
        {
            var customerId = Convert.ToInt32(custRow["id"]);
            var firstName = custRow["first_name"]?.ToString() ?? "";
            var lastName = custRow["last_name"]?.ToString() ?? "";

            // transaction_score: count * 10.0, capped at 1000
            var txnCount = txnCountByCustomer.GetValueOrDefault(customerId, 0);
            var transactionScore = Math.Min(txnCount * 10.0m, 1000m);

            // balance_score: total balance / 1000.0, capped at 1000
            var totalBalance = balanceByCustomer.GetValueOrDefault(customerId, 0m);
            var balanceScore = Math.Min(totalBalance / 1000.0m, 1000m);

            // visit_score: count * 50.0, capped at 1000
            var visitCount = visitCountByCustomer.GetValueOrDefault(customerId, 0);
            var visitScore = Math.Min(visitCount * 50.0m, 1000m);

            // composite_score
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

        var df = new DataFrame(outputRows, outputColumns);
        DscWriterUtil.Write("customer_value_score", true, df);
        sharedState["output"] = df;
        return sharedState;
    }
}
