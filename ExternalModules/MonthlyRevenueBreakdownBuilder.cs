using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

public class MonthlyRevenueBreakdownBuilder : IExternalStep
{
    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var outputColumns = new List<string>
        {
            "revenue_source", "total_revenue", "transaction_count", "as_of"
        };

        var overdraftEvents = sharedState.ContainsKey("overdraft_events") ? sharedState["overdraft_events"] as DataFrame : null;
        var transactions = sharedState.ContainsKey("transactions") ? sharedState["transactions"] as DataFrame : null;

        var maxDate = (DateOnly)sharedState["__maxEffectiveDate"];

        // Compute daily overdraft fee revenue (charged, not waived)
        decimal overdraftRevenue = 0m;
        int overdraftCount = 0;
        if (overdraftEvents != null)
        {
            foreach (var row in overdraftEvents.Rows)
            {
                var feeWaived = Convert.ToBoolean(row["fee_waived"]);
                if (!feeWaived)
                {
                    overdraftRevenue += Convert.ToDecimal(row["fee_amount"]);
                    overdraftCount++;
                }
            }
        }

        // Compute daily credit transaction revenue as proxy for interest
        decimal creditRevenue = 0m;
        int creditCount = 0;
        if (transactions != null)
        {
            foreach (var row in transactions.Rows)
            {
                var txnType = row["txn_type"]?.ToString() ?? "";
                if (txnType == "Credit")
                {
                    creditRevenue += Convert.ToDecimal(row["amount"]);
                    creditCount++;
                }
            }
        }

        // W5: Banker's rounding
        var outputRows = new List<Row>
        {
            new Row(new Dictionary<string, object?>
            {
                ["revenue_source"] = "overdraft_fees",
                ["total_revenue"] = Math.Round(overdraftRevenue, 2, MidpointRounding.ToEven),
                ["transaction_count"] = overdraftCount,
                ["as_of"] = maxDate
            }),
            new Row(new Dictionary<string, object?>
            {
                ["revenue_source"] = "credit_interest_proxy",
                ["total_revenue"] = Math.Round(creditRevenue, 2, MidpointRounding.ToEven),
                ["transaction_count"] = creditCount,
                ["as_of"] = maxDate
            })
        };

        // W3c: End-of-quarter boundary — append quarterly summary rows on Oct 31
        // Fiscal quarter boundary: Q4 starts Nov 1, so Oct 31 is the last day of Q3
        if (maxDate.Month == 10 && maxDate.Day == 31)
        {
            decimal qOverdraftRevenue = overdraftRevenue;
            int qOverdraftCount = overdraftCount;
            decimal qCreditRevenue = creditRevenue;
            int qCreditCount = creditCount;

            outputRows.Add(new Row(new Dictionary<string, object?>
            {
                ["revenue_source"] = "QUARTERLY_TOTAL_overdraft_fees",
                ["total_revenue"] = Math.Round(qOverdraftRevenue, 2, MidpointRounding.ToEven),
                ["transaction_count"] = qOverdraftCount,
                ["as_of"] = maxDate
            }));
            outputRows.Add(new Row(new Dictionary<string, object?>
            {
                ["revenue_source"] = "QUARTERLY_TOTAL_credit_interest_proxy",
                ["total_revenue"] = Math.Round(qCreditRevenue, 2, MidpointRounding.ToEven),
                ["transaction_count"] = qCreditCount,
                ["as_of"] = maxDate
            }));
        }

        sharedState["output"] = new DataFrame(outputRows, outputColumns);
        return sharedState;
    }
}
