using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

public class BranchVisitLogV2Processor : IExternalStep
{
    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var outputColumns = new List<string>
        {
            "visit_id", "customer_id", "first_name", "last_name",
            "branch_id", "branch_name", "visit_timestamp", "visit_purpose", "as_of"
        };

        var branchVisits = sharedState.ContainsKey("branch_visits") ? sharedState["branch_visits"] as DataFrame : null;
        var branches = sharedState.ContainsKey("branches") ? sharedState["branches"] as DataFrame : null;
        var customers = sharedState.ContainsKey("customers") ? sharedState["customers"] as DataFrame : null;

        // Weekend guard on customers empty
        if (customers == null || customers.Count == 0)
        {
            sharedState["output"] = new DataFrame(new List<Row>(), outputColumns);
            return sharedState;
        }

        if (branchVisits == null || branchVisits.Count == 0)
        {
            sharedState["output"] = new DataFrame(new List<Row>(), outputColumns);
            return sharedState;
        }

        // Build branch_id -> branch_name lookup
        var branchNames = new Dictionary<int, string>();
        if (branches != null)
        {
            foreach (var row in branches.Rows)
            {
                var branchId = Convert.ToInt32(row["branch_id"]);
                var branchName = row["branch_name"]?.ToString() ?? "";
                branchNames[branchId] = branchName;
            }
        }

        // Build customer_id -> (first_name, last_name) lookup
        var customerNames = new Dictionary<int, (string firstName, string lastName)>();
        foreach (var custRow in customers.Rows)
        {
            var custId = Convert.ToInt32(custRow["id"]);
            var firstName = custRow["first_name"]?.ToString() ?? "";
            var lastName = custRow["last_name"]?.ToString() ?? "";
            customerNames[custId] = (firstName, lastName);
        }

        // Row-by-row: iterate branch_visits, enrich with branch_name and customer name
        var outputRows = new List<Row>();
        foreach (var visitRow in branchVisits.Rows)
        {
            var customerId = Convert.ToInt32(visitRow["customer_id"]);
            var branchId = Convert.ToInt32(visitRow["branch_id"]);

            var branchName = branchNames.GetValueOrDefault(branchId, "");
            var (firstName, lastName) = customerNames.GetValueOrDefault(customerId, (null!, null!));

            outputRows.Add(new Row(new Dictionary<string, object?>
            {
                ["visit_id"] = visitRow["visit_id"],
                ["customer_id"] = visitRow["customer_id"],
                ["first_name"] = firstName,
                ["last_name"] = lastName,
                ["branch_id"] = visitRow["branch_id"],
                ["branch_name"] = branchName,
                ["visit_timestamp"] = visitRow["visit_timestamp"],
                ["visit_purpose"] = visitRow["visit_purpose"],
                ["as_of"] = visitRow["as_of"]
            }));
        }

        var df = new DataFrame(outputRows, outputColumns);
        DscWriterUtil.Write("branch_visit_log", false, df);
        sharedState["output"] = df;
        return sharedState;
    }
}
