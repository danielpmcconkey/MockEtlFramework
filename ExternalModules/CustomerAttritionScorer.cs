using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

public class CustomerAttritionScorer : IExternalStep
{
    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var outputColumns = new List<string>
        {
            "customer_id", "first_name", "last_name",
            "account_count", "txn_count", "avg_balance",
            "attrition_score", "risk_level", "as_of"
        };

        var customers = sharedState.ContainsKey("customers") ? sharedState["customers"] as DataFrame : null;
        var accounts = sharedState.ContainsKey("accounts") ? sharedState["accounts"] as DataFrame : null;
        var transactions = sharedState.ContainsKey("transactions") ? sharedState["transactions"] as DataFrame : null;

        if (customers == null || customers.Count == 0)
        {
            sharedState["output"] = new DataFrame(new List<Row>(), outputColumns);
            return sharedState;
        }

        var maxDate = (DateOnly)sharedState["__maxEffectiveDate"];

        // Build per-customer account counts and balances
        var accountCountByCustomer = new Dictionary<int, int>();
        var balanceByCustomer = new Dictionary<int, decimal>();
        if (accounts != null)
        {
            foreach (var row in accounts.Rows)
            {
                var custId = Convert.ToInt32(row["customer_id"]);
                accountCountByCustomer[custId] = accountCountByCustomer.GetValueOrDefault(custId, 0) + 1;
                balanceByCustomer[custId] = balanceByCustomer.GetValueOrDefault(custId, 0m) + Convert.ToDecimal(row["current_balance"]);
            }
        }

        // Build account_id -> customer_id lookup
        var accountToCustomer = new Dictionary<int, int>();
        if (accounts != null)
        {
            foreach (var row in accounts.Rows)
            {
                accountToCustomer[Convert.ToInt32(row["account_id"])] = Convert.ToInt32(row["customer_id"]);
            }
        }

        // Build per-customer transaction counts
        var txnCountByCustomer = new Dictionary<int, int>();
        if (transactions != null)
        {
            foreach (var row in transactions.Rows)
            {
                var acctId = Convert.ToInt32(row["account_id"]);
                var custId = accountToCustomer.GetValueOrDefault(acctId, 0);
                if (custId == 0) continue;
                txnCountByCustomer[custId] = txnCountByCustomer.GetValueOrDefault(custId, 0) + 1;
            }
        }

        // AP6: Row-by-row iteration computing attrition score
        var outputRows = new List<Row>();
        foreach (var custRow in customers.Rows)
        {
            var customerId = Convert.ToInt32(custRow["id"]);
            var acctCount = accountCountByCustomer.GetValueOrDefault(customerId, 0);
            var txnCount = txnCountByCustomer.GetValueOrDefault(customerId, 0);
            var totalBalance = balanceByCustomer.GetValueOrDefault(customerId, 0m);
            var avgBalance = acctCount > 0 ? totalBalance / acctCount : 0m;

            // W6: Double epsilon — use double instead of decimal for score accumulation
            double dormancyFactor = acctCount == 0 ? 1.0 : 0.0;
            // AP7: Magic threshold — txn_count < 3 = "declining"
            double decliningTxnFactor = txnCount < 3 ? 1.0 : 0.0;
            // AP7: Magic threshold — balance < 100 = "low"
            double lowBalanceFactor = (double)avgBalance < 100.0 ? 1.0 : 0.0;

            // W6: Double accumulation with floating-point errors
            double attritionScore = 0.0;
            attritionScore += dormancyFactor * 40.0;
            attritionScore += decliningTxnFactor * 35.0;
            attritionScore += lowBalanceFactor * 25.0;

            string riskLevel;
            if (attritionScore >= 75.0) riskLevel = "High";
            else if (attritionScore >= 40.0) riskLevel = "Medium";
            else riskLevel = "Low";

            outputRows.Add(new Row(new Dictionary<string, object?>
            {
                ["customer_id"] = customerId,
                ["first_name"] = custRow["first_name"]?.ToString() ?? "",
                ["last_name"] = custRow["last_name"]?.ToString() ?? "",
                ["account_count"] = acctCount,
                ["txn_count"] = txnCount,
                ["avg_balance"] = Math.Round(avgBalance, 2),
                ["attrition_score"] = attritionScore,
                ["risk_level"] = riskLevel,
                ["as_of"] = maxDate
            }));
        }

        sharedState["output"] = new DataFrame(outputRows, outputColumns);
        return sharedState;
    }
}
