using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

/// <summary>
/// V2 of CustomerDemographics: computes customer age, age bracket, primary phone/email.
/// Uses External module for empty-DataFrame guard (framework Transformation does
/// not register empty DataFrames as SQLite tables, causing SQL to fail on weekends
/// when datalake.customers has no data).
/// </summary>
public class CustomerDemographicsV2 : IExternalStep
{
    private static readonly List<string> OutputColumns = new()
    {
        "customer_id", "first_name", "last_name", "birthdate", "age", "age_bracket",
        "primary_phone", "primary_email", "as_of"
    };

    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var customers = sharedState.ContainsKey("customers")
            ? sharedState["customers"] as DataFrame
            : null;

        if (customers == null || customers.Count == 0)
        {
            sharedState["demographics_output"] = new DataFrame(new List<Row>(), OutputColumns);
            return sharedState;
        }

        return new Transformation("demographics_output", @"
            SELECT c.id AS customer_id,
                   COALESCE(c.first_name, '') AS first_name,
                   COALESCE(c.last_name, '') AS last_name,
                   strftime('%Y-%m-%d', c.birthdate) AS birthdate,
                   CAST(strftime('%Y', c.as_of) AS INTEGER) - CAST(strftime('%Y', c.birthdate) AS INTEGER)
                     - CASE WHEN strftime('%m-%d', c.as_of) < strftime('%m-%d', c.birthdate) THEN 1 ELSE 0 END AS age,
                   CASE
                     WHEN (CAST(strftime('%Y', c.as_of) AS INTEGER) - CAST(strftime('%Y', c.birthdate) AS INTEGER)
                       - CASE WHEN strftime('%m-%d', c.as_of) < strftime('%m-%d', c.birthdate) THEN 1 ELSE 0 END) < 26 THEN '18-25'
                     WHEN (CAST(strftime('%Y', c.as_of) AS INTEGER) - CAST(strftime('%Y', c.birthdate) AS INTEGER)
                       - CASE WHEN strftime('%m-%d', c.as_of) < strftime('%m-%d', c.birthdate) THEN 1 ELSE 0 END) <= 35 THEN '26-35'
                     WHEN (CAST(strftime('%Y', c.as_of) AS INTEGER) - CAST(strftime('%Y', c.birthdate) AS INTEGER)
                       - CASE WHEN strftime('%m-%d', c.as_of) < strftime('%m-%d', c.birthdate) THEN 1 ELSE 0 END) <= 45 THEN '36-45'
                     WHEN (CAST(strftime('%Y', c.as_of) AS INTEGER) - CAST(strftime('%Y', c.birthdate) AS INTEGER)
                       - CASE WHEN strftime('%m-%d', c.as_of) < strftime('%m-%d', c.birthdate) THEN 1 ELSE 0 END) <= 55 THEN '46-55'
                     WHEN (CAST(strftime('%Y', c.as_of) AS INTEGER) - CAST(strftime('%Y', c.birthdate) AS INTEGER)
                       - CASE WHEN strftime('%m-%d', c.as_of) < strftime('%m-%d', c.birthdate) THEN 1 ELSE 0 END) <= 65 THEN '56-65'
                     ELSE '65+'
                   END AS age_bracket,
                   COALESCE(p.phone_number, '') AS primary_phone,
                   COALESCE(e.email_address, '') AS primary_email,
                   c.as_of
            FROM customers c
            LEFT JOIN (
                SELECT customer_id, phone_number, as_of,
                       ROW_NUMBER() OVER (PARTITION BY customer_id, as_of ORDER BY phone_id) AS rn
                FROM phone_numbers
            ) p ON c.id = p.customer_id AND c.as_of = p.as_of AND p.rn = 1
            LEFT JOIN (
                SELECT customer_id, email_address, as_of,
                       ROW_NUMBER() OVER (PARTITION BY customer_id, as_of ORDER BY email_id) AS rn
                FROM email_addresses
            ) e ON c.id = e.customer_id AND c.as_of = e.as_of AND e.rn = 1
            ORDER BY c.id
        ").Execute(sharedState);
    }
}
