using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

public class CardExpirationWatchProcessor : IExternalStep
{
    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var outputColumns = new List<string>
        {
            "card_id", "customer_id", "first_name", "last_name", "card_type",
            "expiration_date", "days_until_expiry", "as_of"
        };

        var maxDate = (DateOnly)sharedState["__maxEffectiveDate"];

        // W2: Weekend fallback — use Friday's date on Sat/Sun
        DateOnly targetDate = maxDate;
        if (maxDate.DayOfWeek == DayOfWeek.Saturday) targetDate = maxDate.AddDays(-1);
        else if (maxDate.DayOfWeek == DayOfWeek.Sunday) targetDate = maxDate.AddDays(-2);

        var cards = sharedState.ContainsKey("cards")
            ? sharedState["cards"] as DataFrame
            : null;
        var customers = sharedState.ContainsKey("customers")
            ? sharedState["customers"] as DataFrame
            : null;

        if (cards == null || cards.Count == 0)
        {
            sharedState["output"] = new DataFrame(new List<Row>(), outputColumns);
            return sharedState;
        }

        // Build customer lookup
        var customerLookup = new Dictionary<int, (string first, string last)>();
        if (customers != null)
        {
            foreach (var c in customers.Rows)
            {
                var custId = Convert.ToInt32(c["id"]);
                customerLookup[custId] = (
                    c["first_name"]?.ToString() ?? "",
                    c["last_name"]?.ToString() ?? ""
                );
            }
        }

        // AP6: Row-by-row iteration to find cards expiring within 90 days
        var outputRows = new List<Row>();
        foreach (var card in cards.Rows)
        {
            var rawExp = card["expiration_date"];
            var expirationDate = rawExp is DateOnly d ? d : DateOnly.FromDateTime((DateTime)rawExp!);
            var daysUntilExpiry = expirationDate.DayNumber - targetDate.DayNumber;

            if (daysUntilExpiry >= 0 && daysUntilExpiry <= 90)
            {
                var custId = Convert.ToInt32(card["customer_id"]);
                var name = customerLookup.GetValueOrDefault(custId, ("", ""));

                outputRows.Add(new Row(new Dictionary<string, object?>
                {
                    ["card_id"] = card["card_id"],
                    ["customer_id"] = custId,
                    ["first_name"] = name.Item1,
                    ["last_name"] = name.Item2,
                    ["card_type"] = card["card_type"],
                    ["expiration_date"] = expirationDate,
                    ["days_until_expiry"] = daysUntilExpiry,
                    ["as_of"] = targetDate
                }));
            }
        }

        sharedState["output"] = new DataFrame(outputRows, outputColumns);
        return sharedState;
    }
}
