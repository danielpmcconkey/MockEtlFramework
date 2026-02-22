using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

/// <summary>
/// V2 of CustomerFullProfile: joins customer demographics with segment data.
/// Uses External module for empty-DataFrame guard (framework Transformation does
/// not register empty DataFrames as SQLite tables, causing SQL to fail on weekends
/// when curated.customer_demographics is empty -- it's an Overwrite-mode table
/// sourced from weekday-only datalake.customers).
/// </summary>
public class CustomerFullProfileV2 : IExternalStep
{
    private static readonly List<string> OutputColumns = new()
    {
        "customer_id", "first_name", "last_name", "age", "age_bracket",
        "primary_phone", "primary_email", "segments", "as_of"
    };

    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var demographics = sharedState.ContainsKey("customer_demographics")
            ? sharedState["customer_demographics"] as DataFrame
            : null;

        if (demographics == null || demographics.Count == 0)
        {
            sharedState["profile_output"] = new DataFrame(new List<Row>(), OutputColumns);
            return sharedState;
        }

        return new Transformation("profile_output", @"
            SELECT cd.customer_id, cd.first_name, cd.last_name, cd.age, cd.age_bracket,
                   cd.primary_phone, cd.primary_email,
                   COALESCE(seg_agg.segment_list, '') AS segments,
                   cd.as_of
            FROM customer_demographics cd
            LEFT JOIN (
                SELECT cs.customer_id, cs.as_of, GROUP_CONCAT(s.segment_name) AS segment_list
                FROM customers_segments cs
                JOIN segments s ON cs.segment_id = s.segment_id AND cs.as_of = s.as_of
                GROUP BY cs.customer_id, cs.as_of
            ) seg_agg ON cd.customer_id = seg_agg.customer_id AND cd.as_of = seg_agg.as_of
            ORDER BY cd.customer_id
        ").Execute(sharedState);
    }
}
