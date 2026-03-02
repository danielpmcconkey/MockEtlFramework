using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

/// <summary>
/// V2 type-coercion module for CardExpirationWatch.
/// Contains ZERO business logic — all filtering, joining, and computation
/// is handled by the preceding SQL Transformation module.
///
/// This module exists solely because the Transformation module's SQLite backend
/// stores DateOnly as TEXT strings and returns integers as long (Int64).
/// The V1 Parquet output has DateOnly-typed columns (expiration_date, as_of)
/// and int-typed columns (customer_id, days_until_expiry). Byte-identical
/// Parquet output requires matching these CLR types exactly.
///
/// Anti-patterns eliminated:
///   AP3 — V1's full External module replaced; business logic moved to SQL Transformation.
///          This External module retained ONLY for type coercion (framework limitation).
///   AP6 — V1's row-by-row foreach with dictionary lookup replaced with SQL JOIN and WHERE.
/// </summary>
public class CardExpirationWatchV2Processor : IExternalStep
{
    /// <summary>
    /// Output column names matching V1's output schema order.
    /// </summary>
    private static readonly List<string> OutputColumns = new()
    {
        "card_id", "customer_id", "first_name", "last_name", "card_type",
        "expiration_date", "days_until_expiry", "as_of"
    };

    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var rawDf = (DataFrame)sharedState["output_raw"];

        // Type coercion only — no business logic.
        // The Transformation module's SQLite backend stores DateOnly as TEXT
        // and returns integers as long. The V1 Parquet schema uses DateOnly
        // and int types. This module restores the correct CLR types for
        // byte-identical Parquet output.
        var typedRows = new List<Row>();
        foreach (var row in rawDf.Rows)
        {
            typedRows.Add(new Row(new Dictionary<string, object?>
            {
                ["card_id"] = row["card_id"],
                ["customer_id"] = Convert.ToInt32(row["customer_id"]),
                ["first_name"] = row["first_name"]?.ToString() ?? "",
                ["last_name"] = row["last_name"]?.ToString() ?? "",
                ["card_type"] = row["card_type"],
                ["expiration_date"] = DateOnly.Parse(row["expiration_date"]!.ToString()!),
                ["days_until_expiry"] = Convert.ToInt32(row["days_until_expiry"]),
                ["as_of"] = DateOnly.Parse(row["as_of"]!.ToString()!)
            }));
        }

        sharedState["output"] = new DataFrame(typedRows, OutputColumns);
        return sharedState;
    }
}
