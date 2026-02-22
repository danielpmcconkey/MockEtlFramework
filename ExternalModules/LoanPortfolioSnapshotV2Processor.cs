using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

public class LoanPortfolioSnapshotV2Processor : IExternalStep
{
    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var outputColumns = new List<string>
        {
            "loan_id", "customer_id", "loan_type", "original_amount",
            "current_balance", "interest_rate", "loan_status", "as_of"
        };

        var loanAccounts = sharedState.ContainsKey("loan_accounts") ? sharedState["loan_accounts"] as DataFrame : null;

        if (loanAccounts == null || loanAccounts.Count == 0)
        {
            var emptyDf = new DataFrame(new List<Row>(), outputColumns);
            DscWriterUtil.Write("loan_portfolio_snapshot", true, emptyDf);
            sharedState["output"] = emptyDf;
            return sharedState;
        }

        // Pass-through: copy loan rows, skipping origination_date and maturity_date
        var outputRows = new List<Row>();
        foreach (var row in loanAccounts.Rows)
        {
            outputRows.Add(new Row(new Dictionary<string, object?>
            {
                ["loan_id"] = row["loan_id"],
                ["customer_id"] = row["customer_id"],
                ["loan_type"] = row["loan_type"],
                ["original_amount"] = row["original_amount"],
                ["current_balance"] = row["current_balance"],
                ["interest_rate"] = row["interest_rate"],
                ["loan_status"] = row["loan_status"],
                ["as_of"] = row["as_of"]
            }));
        }

        var df = new DataFrame(outputRows, outputColumns);
        DscWriterUtil.Write("loan_portfolio_snapshot", true, df);
        sharedState["output"] = df;
        return sharedState;
    }
}
