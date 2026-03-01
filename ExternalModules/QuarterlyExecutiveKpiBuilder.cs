using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

public class QuarterlyExecutiveKpiBuilder : IExternalStep
{
    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var outputColumns = new List<string>
        {
            "kpi_name", "kpi_value", "as_of"
        };

        var customers = sharedState.ContainsKey("customers") ? sharedState["customers"] as DataFrame : null;
        var accounts = sharedState.ContainsKey("accounts") ? sharedState["accounts"] as DataFrame : null;
        var transactions = sharedState.ContainsKey("transactions") ? sharedState["transactions"] as DataFrame : null;
        var investments = sharedState.ContainsKey("investments") ? sharedState["investments"] as DataFrame : null;
        var complianceEvents = sharedState.ContainsKey("compliance_events") ? sharedState["compliance_events"] as DataFrame : null;

        if (customers == null || customers.Count == 0)
        {
            sharedState["output"] = new DataFrame(new List<Row>(), outputColumns);
            return sharedState;
        }

        // W2: Weekend fallback to Friday
        var maxDate = (DateOnly)sharedState["__maxEffectiveDate"];
        DateOnly targetDate = maxDate;
        if (maxDate.DayOfWeek == DayOfWeek.Saturday) targetDate = maxDate.AddDays(-1);
        else if (maxDate.DayOfWeek == DayOfWeek.Sunday) targetDate = maxDate.AddDays(-2);

        // AP9: Misleading name — "quarterly" but actually produces daily KPIs
        // AP2: Duplicates logic from executive_dashboard and other summary jobs

        // total_customers
        var totalCustomers = (decimal)customers.Count;

        // total_accounts + total_balance
        decimal totalAccounts = 0m;
        decimal totalBalance = 0m;
        if (accounts != null)
        {
            foreach (var row in accounts.Rows)
            {
                totalAccounts++;
                totalBalance += Convert.ToDecimal(row["current_balance"]);
            }
        }

        // total_transactions + total_txn_amount
        decimal totalTransactions = 0m;
        decimal totalTxnAmount = 0m;
        if (transactions != null)
        {
            foreach (var row in transactions.Rows)
            {
                totalTransactions++;
                totalTxnAmount += Convert.ToDecimal(row["amount"]);
            }
        }

        // total_investments + total_investment_value
        decimal totalInvestments = 0m;
        decimal totalInvestmentValue = 0m;
        if (investments != null)
        {
            foreach (var row in investments.Rows)
            {
                totalInvestments++;
                totalInvestmentValue += Convert.ToDecimal(row["current_value"]);
            }
        }

        // compliance_events_count
        decimal complianceCount = complianceEvents?.Count ?? 0;

        // Build KPI rows
        var kpis = new List<(string name, decimal value)>
        {
            ("total_customers", Math.Round(totalCustomers, 2)),
            ("total_accounts", Math.Round(totalAccounts, 2)),
            ("total_balance", Math.Round(totalBalance, 2)),
            ("total_transactions", Math.Round(totalTransactions, 2)),
            ("total_txn_amount", Math.Round(totalTxnAmount, 2)),
            ("total_investments", Math.Round(totalInvestments, 2)),
            ("total_investment_value", Math.Round(totalInvestmentValue, 2)),
            ("compliance_events", Math.Round(complianceCount, 2))
        };

        var outputRows = new List<Row>();
        foreach (var (name, value) in kpis)
        {
            outputRows.Add(new Row(new Dictionary<string, object?>
            {
                ["kpi_name"] = name,
                ["kpi_value"] = value,
                ["as_of"] = targetDate
            }));
        }

        sharedState["output"] = new DataFrame(outputRows, outputColumns);
        return sharedState;
    }
}
