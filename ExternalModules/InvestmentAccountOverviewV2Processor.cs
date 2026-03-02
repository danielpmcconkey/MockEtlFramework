using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

/// <summary>
/// V2 replacement for InvestmentAccountOverviewBuilder.
/// Minimal Tier 2 External — implements Sunday skip guard, empty-input guard,
/// and LEFT JOIN of investments to customers via Dictionary lookup.
/// DataSourcing handles data retrieval; CsvFileWriter handles output.
///
/// Anti-patterns eliminated:
///   AP1 — advisor_id removed from investments DataSourcing (never used in output)
///   AP3 — partially eliminated: demoted from Tier 3 to Tier 2; DataSourcing handles
///          data retrieval, CsvFileWriter handles output
///   AP4 — prefix, suffix removed from customers DataSourcing; advisor_id removed
///          from investments DataSourcing (none appear in output)
///   AP6 — partially addressed: Dictionary-based hash-join is idiomatic C#;
///          full SQL elimination blocked by W1 requirement
///
/// Wrinkles reproduced:
///   W1 — Sunday skip: returns empty DataFrame when __maxEffectiveDate is Sunday
/// </summary>
public class InvestmentAccountOverviewV2Processor : IExternalStep
{
    private static readonly List<string> OutputColumns = new()
    {
        "investment_id", "customer_id", "first_name", "last_name",
        "account_type", "current_value", "risk_profile", "as_of"
    };

    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        // Read __maxEffectiveDate from shared state (fallback: today)
        var maxDate = sharedState.TryGetValue("__maxEffectiveDate", out var dateVal)
            ? (DateOnly)dateVal
            : DateOnly.FromDateTime(DateTime.Today);

        // V1 behavior: no output on Sundays (W1)
        if (maxDate.DayOfWeek == DayOfWeek.Sunday)
        {
            sharedState["output"] = new DataFrame(new List<Row>(), OutputColumns);
            return sharedState;
        }

        var investments = sharedState.TryGetValue("investments", out var invVal)
            ? invVal as DataFrame
            : null;

        var customers = sharedState.TryGetValue("customers", out var custVal)
            ? custVal as DataFrame
            : null;

        // BR-2: Empty input guard — if either source is null/empty, return empty output.
        // Note: V1 returns empty if investments OR customers is empty — not a LEFT JOIN
        // with empty names. This is V1's behavior, reproduced exactly.
        if (investments == null || investments.Count == 0 ||
            customers == null || customers.Count == 0)
        {
            sharedState["output"] = new DataFrame(new List<Row>(), OutputColumns);
            return sharedState;
        }

        // BR-3: Build customer lookup via Dictionary (hash-join).
        // AP6 partial fix: replaces V1's row-by-row dictionary build with same pattern
        // but cleaner code. Full SQL elimination blocked by W1.
        var customerLookup = new Dictionary<int, (string firstName, string lastName)>();
        foreach (var custRow in customers.Rows)
        {
            var custId = Convert.ToInt32(custRow["id"]);
            customerLookup[custId] = (
                custRow["first_name"]?.ToString() ?? "",
                custRow["last_name"]?.ToString() ?? ""
            );
        }

        // BR-4: 1:1 investment-to-output mapping
        var outputRows = new List<Row>();
        foreach (var row in investments.Rows)
        {
            var customerId = Convert.ToInt32(row["customer_id"]);
            var name = customerLookup.ContainsKey(customerId)
                ? customerLookup[customerId]
                : (firstName: "", lastName: "");

            outputRows.Add(new Row(new Dictionary<string, object?>
            {
                ["investment_id"] = Convert.ToInt32(row["investment_id"]),
                ["customer_id"] = customerId,
                ["first_name"] = name.firstName,
                ["last_name"] = name.lastName,
                ["account_type"] = row["account_type"]?.ToString() ?? "",
                ["current_value"] = Convert.ToDecimal(row["current_value"]),  // BR-6: no rounding
                ["risk_profile"] = row["risk_profile"]?.ToString() ?? "",
                ["as_of"] = row["as_of"]  // BR-5: row-level, NOT __maxEffectiveDate
            }));
        }

        sharedState["output"] = new DataFrame(outputRows, OutputColumns);
        return sharedState;
    }
}
