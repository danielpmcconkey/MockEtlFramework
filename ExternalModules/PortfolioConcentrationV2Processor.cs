using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

/// <summary>
/// V2 minimal External module for PortfolioConcentration.
/// Handles ONLY type coercion (long -> int for Parquet INT32 schema) and
/// W4 integer division replication. ALL business logic (join, grouping,
/// aggregation) lives in the upstream SQL Transformation.
///
/// Why this External exists (Tier 2 SCALPEL):
///   W4 — Integer division for sector_pct requires C# (int) cast semantics.
///         SQLite CAST(x AS INTEGER) returns long (Parquet INT64), not int (Parquet INT32).
///   Parquet type fidelity — customer_id and investment_id must be int (INT32),
///         sector_pct must be decimal (DECIMAL). SQLite returns long for all integer types.
///
/// Anti-patterns eliminated:
///   AP1 — investments DataSourcing removed (never referenced by V1 logic)
///   AP3 — Business logic (join, grouping, aggregation) moved to SQL Transformation
///   AP4 — holdings reduced from 6 to 4 columns; securities reduced from 5 to 2 columns
///   AP6 — V1's three foreach loops replaced with single SQL statement
///   AP7 — Magic value "Unknown" replaced with named constant
/// </summary>
public class PortfolioConcentrationV2Processor : IExternalStep
{
    private const string InputKey = "sector_agg";    // Transformation output
    private const string OutputKey = "output";        // ParquetFileWriter input
    private const string DefaultSector = "Unknown";   // Default for securities with null/missing sector

    private static readonly List<string> OutputColumns = new()
    {
        "customer_id", "investment_id", "sector",
        "sector_value", "total_value", "sector_pct", "ifw_effective_date"
    };

    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var sectorAgg = sharedState.TryGetValue(InputKey, out var val)
            ? val as DataFrame
            : null;

        // BR-6: Empty/null input produces empty DataFrame with correct schema.
        // Matches V1 guard clause at PortfolioConcentrationCalculator.cs:20-24.
        if (sectorAgg == null || sectorAgg.Count == 0)
        {
            sharedState[OutputKey] = new DataFrame(new List<Row>(), OutputColumns);
            return sharedState;
        }

        var outputRows = new List<Row>();

        foreach (var row in sectorAgg.Rows)
        {
            // Type coercion: long -> int for Parquet INT32 schema equivalence
            int customerId = Convert.ToInt32(row["customer_id"]);
            int investmentId = Convert.ToInt32(row["investment_id"]);
            string sector = row["sector"]?.ToString() ?? DefaultSector;

            // W6: V1 uses double (not decimal) for monetary accumulation.
            // Epsilon errors in output are intentional V1 replication.
            // SQLite REAL (double) SUM in the upstream Transformation replicates this.
            double sectorValue = Convert.ToDouble(row["sector_value"]);
            double totalValue = Convert.ToDouble(row["total_value"]);

            // W4: V1 bug — integer division truncates to 0. Replicated for output equivalence.
            // V1 casts double -> int (truncating decimal portion), then divides int/int which
            // floors to 0 for any case where sector_value < total_value.
            int sectorInt = (int)sectorValue;
            int totalInt = (int)totalValue;
            decimal sectorPct = (decimal)(sectorInt / totalInt);

            // ifw_effective_date: SQLite returns TEXT from strftime; parse back to DateOnly for Parquet DATE
            DateOnly asOf = DateOnly.Parse(row["ifw_effective_date"].ToString()!);

            outputRows.Add(new Row(new Dictionary<string, object?>
            {
                ["customer_id"] = customerId,
                ["investment_id"] = investmentId,
                ["sector"] = sector,
                ["sector_value"] = sectorValue,
                ["total_value"] = totalValue,
                ["sector_pct"] = sectorPct,
                ["ifw_effective_date"] = asOf
            }));
        }

        sharedState[OutputKey] = new DataFrame(outputRows, OutputColumns);
        return sharedState;
    }
}
