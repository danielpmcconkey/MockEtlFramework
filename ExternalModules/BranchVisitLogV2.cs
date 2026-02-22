using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

/// <summary>
/// V2 of BranchVisitLog: joins branch visits with branches and customers.
/// Uses External module for empty-DataFrame guard (framework Transformation does
/// not register empty DataFrames as SQLite tables, causing SQL to fail on weekends
/// when datalake.customers has no data).
/// Note: branch_visits and branches have data on all days, but customers is weekday-only.
/// The original SQL has WHERE EXISTS (SELECT 1 FROM customers) which would also fail
/// if the customers table isn't registered.
/// </summary>
public class BranchVisitLogV2 : IExternalStep
{
    private static readonly List<string> OutputColumns = new()
    {
        "visit_id", "customer_id", "first_name", "last_name", "branch_id",
        "branch_name", "visit_timestamp", "visit_purpose", "as_of"
    };

    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var customers = sharedState.ContainsKey("customers")
            ? sharedState["customers"] as DataFrame
            : null;

        if (customers == null || customers.Count == 0)
        {
            // On weekends, customers table is empty. The original SQL has
            // WHERE EXISTS (SELECT 1 FROM customers), which would produce no rows
            // when customers is empty. So return empty DataFrame.
            sharedState["visit_log_result"] = new DataFrame(new List<Row>(), OutputColumns);
            return sharedState;
        }

        return new Transformation("visit_log_result", @"
            SELECT bv.visit_id, bv.customer_id, c.first_name, c.last_name,
                   bv.branch_id, COALESCE(b.branch_name, '') AS branch_name,
                   REPLACE(bv.visit_timestamp, 'T', ' ') AS visit_timestamp,
                   bv.visit_purpose, bv.as_of
            FROM branch_visits bv
            LEFT JOIN branches b ON bv.branch_id = b.branch_id AND bv.as_of = b.as_of
            LEFT JOIN customers c ON bv.customer_id = c.id AND bv.as_of = c.as_of
            WHERE EXISTS (SELECT 1 FROM customers)
        ").Execute(sharedState);
    }
}
