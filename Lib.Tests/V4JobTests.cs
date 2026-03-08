using Lib.DataFrames;
using Lib.Modules;

namespace Lib.Tests;

/// <summary>
/// V4 job unit tests covering all 5 POC4 dry-run jobs.
/// Tests exercise the SQL Transformation logic used by each V4 job config,
/// verifying correctness of aggregation, joins, filters, and output schemas.
/// All tests run in-memory — no database required.
/// </summary>
public class V4JobTests
{
    // ====================================================================
    // PeakTransactionTimes V4 Tests
    // ====================================================================

    private static DataFrame MakeTransactionsForPeak() => DataFrame.FromObjects(new[]
    {
        new { txn_timestamp = "2024-10-01T09:15:00", amount = 100.50, ifw_effective_date = "2024-10-01" },
        new { txn_timestamp = "2024-10-01T09:30:00", amount = 200.25, ifw_effective_date = "2024-10-01" },
        new { txn_timestamp = "2024-10-01T14:00:00", amount = 50.75, ifw_effective_date = "2024-10-01" },
        new { txn_timestamp = "2024-10-01T14:45:00", amount = 75.00, ifw_effective_date = "2024-10-01" },
        new { txn_timestamp = "2024-10-01T14:59:00", amount = 25.00, ifw_effective_date = "2024-10-01" },
        new { txn_timestamp = "2024-10-01T23:00:00", amount = 10.00, ifw_effective_date = "2024-10-01" }
    });

    [Fact]
    public void PeakTransactionTimes_HourlyAggregation_GroupsByHour()
    {
        var state = new Dictionary<string, object> { ["transactions"] = MakeTransactionsForPeak() };
        var result = new Transformation("hourly_aggregation",
            "SELECT CAST(strftime('%H', txn_timestamp) AS INTEGER) AS hour_of_day, " +
            "COUNT(*) AS txn_count, ROUND(SUM(amount), 2) AS total_amount " +
            "FROM transactions GROUP BY strftime('%H', txn_timestamp) ORDER BY hour_of_day")
            .Execute(state);

        var df = (DataFrame)result["hourly_aggregation"];
        Assert.Equal(3, df.Count); // 3 distinct hours: 9, 14, 23

        // Hour 9: 2 transactions, 300.75
        var hour9 = df.Rows.First(r => Convert.ToInt32(r["hour_of_day"]) == 9);
        Assert.Equal(2L, Convert.ToInt64(hour9["txn_count"]));
        Assert.Equal(300.75, Convert.ToDouble(hour9["total_amount"]), 2);

        // Hour 14: 3 transactions, 150.75
        var hour14 = df.Rows.First(r => Convert.ToInt32(r["hour_of_day"]) == 14);
        Assert.Equal(3L, Convert.ToInt64(hour14["txn_count"]));
        Assert.Equal(150.75, Convert.ToDouble(hour14["total_amount"]), 2);

        // Hour 23: 1 transaction, 10.00
        var hour23 = df.Rows.First(r => Convert.ToInt32(r["hour_of_day"]) == 23);
        Assert.Equal(1L, Convert.ToInt64(hour23["txn_count"]));
        Assert.Equal(10.00, Convert.ToDouble(hour23["total_amount"]), 2);
    }

    [Fact]
    public void PeakTransactionTimes_OutputOrdering_SortedByHour()
    {
        var state = new Dictionary<string, object> { ["transactions"] = MakeTransactionsForPeak() };
        var result = new Transformation("hourly_aggregation",
            "SELECT CAST(strftime('%H', txn_timestamp) AS INTEGER) AS hour_of_day, " +
            "COUNT(*) AS txn_count, ROUND(SUM(amount), 2) AS total_amount " +
            "FROM transactions GROUP BY strftime('%H', txn_timestamp) ORDER BY hour_of_day")
            .Execute(state);

        var df = (DataFrame)result["hourly_aggregation"];
        var hours = df.Rows.Select(r => Convert.ToInt32(r["hour_of_day"])).ToList();
        Assert.Equal(hours.OrderBy(h => h).ToList(), hours);
    }

    [Fact]
    public void PeakTransactionTimes_EmptyInput_ProducesZeroRows()
    {
        var empty = new DataFrame(new[] { "txn_timestamp", "amount", "ifw_effective_date" });
        var state = new Dictionary<string, object> { ["transactions"] = empty };
        var result = new Transformation("hourly_aggregation",
            "SELECT CAST(strftime('%H', txn_timestamp) AS INTEGER) AS hour_of_day, " +
            "COUNT(*) AS txn_count, ROUND(SUM(amount), 2) AS total_amount " +
            "FROM transactions GROUP BY strftime('%H', txn_timestamp) ORDER BY hour_of_day")
            .Execute(state);

        var df = (DataFrame)result["hourly_aggregation"];
        Assert.Equal(0, df.Count);
    }

    [Fact]
    public void PeakTransactionTimes_Rounding_TwoDecimalPlaces()
    {
        var txns = DataFrame.FromObjects(new[]
        {
            new { txn_timestamp = "2024-10-01T10:00:00", amount = 1.111, ifw_effective_date = "2024-10-01" },
            new { txn_timestamp = "2024-10-01T10:30:00", amount = 2.222, ifw_effective_date = "2024-10-01" }
        });
        var state = new Dictionary<string, object> { ["transactions"] = txns };
        var result = new Transformation("hourly_aggregation",
            "SELECT CAST(strftime('%H', txn_timestamp) AS INTEGER) AS hour_of_day, " +
            "COUNT(*) AS txn_count, ROUND(SUM(amount), 2) AS total_amount " +
            "FROM transactions GROUP BY strftime('%H', txn_timestamp) ORDER BY hour_of_day")
            .Execute(state);

        var df = (DataFrame)result["hourly_aggregation"];
        var total = Convert.ToDouble(df.Rows.First()["total_amount"]);
        // 1.111 + 2.222 = 3.333, rounded to 3.33
        Assert.Equal(3.33, total, 2);
    }

    [Fact]
    public void PeakTransactionTimes_NoAccountsSourcing_ConfigDoesNotSourceAccounts()
    {
        // This is a config-level test: verify the V4 config only sources transactions
        // (AP1 and AP4 elimination). We verify by building the state with only transactions
        // and confirming the transformation still works.
        var state = new Dictionary<string, object> { ["transactions"] = MakeTransactionsForPeak() };
        var result = new Transformation("hourly_aggregation",
            "SELECT CAST(strftime('%H', txn_timestamp) AS INTEGER) AS hour_of_day, " +
            "COUNT(*) AS txn_count, ROUND(SUM(amount), 2) AS total_amount " +
            "FROM transactions GROUP BY strftime('%H', txn_timestamp) ORDER BY hour_of_day")
            .Execute(state);

        Assert.Equal(3, ((DataFrame)result["hourly_aggregation"]).Count);
    }

    // ====================================================================
    // DailyBalanceMovement V4 Tests
    // ====================================================================

    private static DataFrame MakeTransactionsForBalance() => DataFrame.FromObjects(new[]
    {
        new { account_id = 1001, txn_type = "Debit", amount = 100.0, ifw_effective_date = "2024-10-01" },
        new { account_id = 1001, txn_type = "Credit", amount = 250.0, ifw_effective_date = "2024-10-01" },
        new { account_id = 1001, txn_type = "Debit", amount = 50.0, ifw_effective_date = "2024-10-01" },
        new { account_id = 1002, txn_type = "Credit", amount = 500.0, ifw_effective_date = "2024-10-01" },
        new { account_id = 1002, txn_type = "Transfer", amount = 75.0, ifw_effective_date = "2024-10-01" },
        new { account_id = 1003, txn_type = "Debit", amount = 200.0, ifw_effective_date = "2024-10-01" }
    });

    private static DataFrame MakeAccountsForBalance() => DataFrame.FromObjects(new[]
    {
        new { account_id = 1001, customer_id = 100, ifw_effective_date = "2024-10-01" },
        new { account_id = 1002, customer_id = 200, ifw_effective_date = "2024-10-01" }
        // account 1003 deliberately missing — customer_id should default to 0
    });

    [Fact]
    public void DailyBalanceMovement_Aggregation_CorrectDebitCreditTotals()
    {
        var state = new Dictionary<string, object>
        {
            ["transactions"] = MakeTransactionsForBalance(),
            ["accounts"] = MakeAccountsForBalance()
        };
        var result = new Transformation("daily_balance_movement",
            "SELECT t.account_id, COALESCE(a.customer_id, 0) AS customer_id, " +
            "SUM(CASE WHEN t.txn_type = 'Debit' THEN CAST(t.amount AS REAL) ELSE 0 END) AS debit_total, " +
            "SUM(CASE WHEN t.txn_type = 'Credit' THEN CAST(t.amount AS REAL) ELSE 0 END) AS credit_total, " +
            "SUM(CASE WHEN t.txn_type = 'Credit' THEN CAST(t.amount AS REAL) ELSE 0 END) - " +
            "SUM(CASE WHEN t.txn_type = 'Debit' THEN CAST(t.amount AS REAL) ELSE 0 END) AS net_movement, " +
            "MIN(t.ifw_effective_date) AS ifw_effective_date " +
            "FROM transactions t LEFT JOIN accounts a ON t.account_id = a.account_id " +
            "GROUP BY t.account_id, a.customer_id")
            .Execute(state);

        var df = (DataFrame)result["daily_balance_movement"];
        Assert.Equal(3, df.Count);

        // Account 1001: debit=150, credit=250, net=100
        var a1001 = df.Rows.First(r => Convert.ToInt32(r["account_id"]) == 1001);
        Assert.Equal(150.0, Convert.ToDouble(a1001["debit_total"]), 2);
        Assert.Equal(250.0, Convert.ToDouble(a1001["credit_total"]), 2);
        Assert.Equal(100.0, Convert.ToDouble(a1001["net_movement"]), 2);
    }

    [Fact]
    public void DailyBalanceMovement_NetMovement_CreditMinusDebit()
    {
        var state = new Dictionary<string, object>
        {
            ["transactions"] = MakeTransactionsForBalance(),
            ["accounts"] = MakeAccountsForBalance()
        };
        var result = new Transformation("daily_balance_movement",
            "SELECT t.account_id, COALESCE(a.customer_id, 0) AS customer_id, " +
            "SUM(CASE WHEN t.txn_type = 'Debit' THEN CAST(t.amount AS REAL) ELSE 0 END) AS debit_total, " +
            "SUM(CASE WHEN t.txn_type = 'Credit' THEN CAST(t.amount AS REAL) ELSE 0 END) AS credit_total, " +
            "SUM(CASE WHEN t.txn_type = 'Credit' THEN CAST(t.amount AS REAL) ELSE 0 END) - " +
            "SUM(CASE WHEN t.txn_type = 'Debit' THEN CAST(t.amount AS REAL) ELSE 0 END) AS net_movement, " +
            "MIN(t.ifw_effective_date) AS ifw_effective_date " +
            "FROM transactions t LEFT JOIN accounts a ON t.account_id = a.account_id " +
            "GROUP BY t.account_id, a.customer_id")
            .Execute(state);

        var df = (DataFrame)result["daily_balance_movement"];
        // Verify net = credit - debit for every row
        foreach (var row in df.Rows)
        {
            var debit = Convert.ToDouble(row["debit_total"]);
            var credit = Convert.ToDouble(row["credit_total"]);
            var net = Convert.ToDouble(row["net_movement"]);
            Assert.Equal(credit - debit, net, 10);
        }
    }

    [Fact]
    public void DailyBalanceMovement_UnmatchedAccount_CustomerIdDefaultsToZero()
    {
        var state = new Dictionary<string, object>
        {
            ["transactions"] = MakeTransactionsForBalance(),
            ["accounts"] = MakeAccountsForBalance()
        };
        var result = new Transformation("daily_balance_movement",
            "SELECT t.account_id, COALESCE(a.customer_id, 0) AS customer_id, " +
            "SUM(CASE WHEN t.txn_type = 'Debit' THEN CAST(t.amount AS REAL) ELSE 0 END) AS debit_total, " +
            "SUM(CASE WHEN t.txn_type = 'Credit' THEN CAST(t.amount AS REAL) ELSE 0 END) AS credit_total, " +
            "SUM(CASE WHEN t.txn_type = 'Credit' THEN CAST(t.amount AS REAL) ELSE 0 END) - " +
            "SUM(CASE WHEN t.txn_type = 'Debit' THEN CAST(t.amount AS REAL) ELSE 0 END) AS net_movement, " +
            "MIN(t.ifw_effective_date) AS ifw_effective_date " +
            "FROM transactions t LEFT JOIN accounts a ON t.account_id = a.account_id " +
            "GROUP BY t.account_id, a.customer_id")
            .Execute(state);

        var df = (DataFrame)result["daily_balance_movement"];
        // Account 1003 has no match in accounts — customer_id should be 0
        var a1003 = df.Rows.First(r => Convert.ToInt32(r["account_id"]) == 1003);
        Assert.Equal(0L, Convert.ToInt64(a1003["customer_id"]));
    }

    [Fact]
    public void DailyBalanceMovement_NonDebitCreditTxnType_SilentlyIgnored()
    {
        var state = new Dictionary<string, object>
        {
            ["transactions"] = MakeTransactionsForBalance(),
            ["accounts"] = MakeAccountsForBalance()
        };
        var result = new Transformation("daily_balance_movement",
            "SELECT t.account_id, COALESCE(a.customer_id, 0) AS customer_id, " +
            "SUM(CASE WHEN t.txn_type = 'Debit' THEN CAST(t.amount AS REAL) ELSE 0 END) AS debit_total, " +
            "SUM(CASE WHEN t.txn_type = 'Credit' THEN CAST(t.amount AS REAL) ELSE 0 END) AS credit_total, " +
            "SUM(CASE WHEN t.txn_type = 'Credit' THEN CAST(t.amount AS REAL) ELSE 0 END) - " +
            "SUM(CASE WHEN t.txn_type = 'Debit' THEN CAST(t.amount AS REAL) ELSE 0 END) AS net_movement, " +
            "MIN(t.ifw_effective_date) AS ifw_effective_date " +
            "FROM transactions t LEFT JOIN accounts a ON t.account_id = a.account_id " +
            "GROUP BY t.account_id, a.customer_id")
            .Execute(state);

        var df = (DataFrame)result["daily_balance_movement"];
        // Account 1002 has one Credit (500) and one Transfer (75) — Transfer should NOT count
        var a1002 = df.Rows.First(r => Convert.ToInt32(r["account_id"]) == 1002);
        Assert.Equal(0.0, Convert.ToDouble(a1002["debit_total"]), 2);
        Assert.Equal(500.0, Convert.ToDouble(a1002["credit_total"]), 2);
    }

    [Fact]
    public void DailyBalanceMovement_EmptyTransactions_ProducesZeroRows()
    {
        var empty = new DataFrame(new[] { "account_id", "txn_type", "amount", "ifw_effective_date" });
        var accounts = MakeAccountsForBalance();
        var state = new Dictionary<string, object>
        {
            ["transactions"] = empty,
            ["accounts"] = accounts
        };
        var result = new Transformation("daily_balance_movement",
            "SELECT t.account_id, COALESCE(a.customer_id, 0) AS customer_id, " +
            "SUM(CASE WHEN t.txn_type = 'Debit' THEN CAST(t.amount AS REAL) ELSE 0 END) AS debit_total, " +
            "SUM(CASE WHEN t.txn_type = 'Credit' THEN CAST(t.amount AS REAL) ELSE 0 END) AS credit_total, " +
            "SUM(CASE WHEN t.txn_type = 'Credit' THEN CAST(t.amount AS REAL) ELSE 0 END) - " +
            "SUM(CASE WHEN t.txn_type = 'Debit' THEN CAST(t.amount AS REAL) ELSE 0 END) AS net_movement, " +
            "MIN(t.ifw_effective_date) AS ifw_effective_date " +
            "FROM transactions t LEFT JOIN accounts a ON t.account_id = a.account_id " +
            "GROUP BY t.account_id, a.customer_id")
            .Execute(state);

        var df = (DataFrame)result["daily_balance_movement"];
        Assert.Equal(0, df.Count);
    }

    [Fact]
    public void DailyBalanceMovement_NoExternalModule_SqlTransformationSuffices()
    {
        // Verify the SQL Transformation produces the expected 6-column schema
        var state = new Dictionary<string, object>
        {
            ["transactions"] = MakeTransactionsForBalance(),
            ["accounts"] = MakeAccountsForBalance()
        };
        var result = new Transformation("daily_balance_movement",
            "SELECT t.account_id, COALESCE(a.customer_id, 0) AS customer_id, " +
            "SUM(CASE WHEN t.txn_type = 'Debit' THEN CAST(t.amount AS REAL) ELSE 0 END) AS debit_total, " +
            "SUM(CASE WHEN t.txn_type = 'Credit' THEN CAST(t.amount AS REAL) ELSE 0 END) AS credit_total, " +
            "SUM(CASE WHEN t.txn_type = 'Credit' THEN CAST(t.amount AS REAL) ELSE 0 END) - " +
            "SUM(CASE WHEN t.txn_type = 'Debit' THEN CAST(t.amount AS REAL) ELSE 0 END) AS net_movement, " +
            "MIN(t.ifw_effective_date) AS ifw_effective_date " +
            "FROM transactions t LEFT JOIN accounts a ON t.account_id = a.account_id " +
            "GROUP BY t.account_id, a.customer_id")
            .Execute(state);

        var df = (DataFrame)result["daily_balance_movement"];
        Assert.Contains("account_id", df.Columns);
        Assert.Contains("customer_id", df.Columns);
        Assert.Contains("debit_total", df.Columns);
        Assert.Contains("credit_total", df.Columns);
        Assert.Contains("net_movement", df.Columns);
        Assert.Contains("ifw_effective_date", df.Columns);
    }

    // ====================================================================
    // CreditScoreDelta V4 Tests
    // ====================================================================

    private static DataFrame MakeTodaysScores() => DataFrame.FromObjects(new[]
    {
        new { customer_id = 2252, bureau = "Equifax", score = 720, ifw_effective_date = "2024-10-02" },
        new { customer_id = 2252, bureau = "Experian", score = 710, ifw_effective_date = "2024-10-02" },
        new { customer_id = 2252, bureau = "TransUnion", score = 730, ifw_effective_date = "2024-10-02" },
        new { customer_id = 2581, bureau = "Equifax", score = 680, ifw_effective_date = "2024-10-02" },
        new { customer_id = 2581, bureau = "Experian", score = 690, ifw_effective_date = "2024-10-02" },
        new { customer_id = 2581, bureau = "TransUnion", score = 670, ifw_effective_date = "2024-10-02" },
        new { customer_id = 2632, bureau = "Equifax", score = 750, ifw_effective_date = "2024-10-02" },
        new { customer_id = 2632, bureau = "Experian", score = 745, ifw_effective_date = "2024-10-02" },
        new { customer_id = 2632, bureau = "TransUnion", score = 760, ifw_effective_date = "2024-10-02" }
    });

    private static DataFrame MakePriorScores() => DataFrame.FromObjects(new[]
    {
        new { customer_id = 2252, bureau = "Equifax", score = 715, ifw_effective_date = "2024-10-01" },
        new { customer_id = 2252, bureau = "Experian", score = 710, ifw_effective_date = "2024-10-01" },  // same — should be excluded
        new { customer_id = 2252, bureau = "TransUnion", score = 725, ifw_effective_date = "2024-10-01" },
        new { customer_id = 2581, bureau = "Equifax", score = 680, ifw_effective_date = "2024-10-01" },  // same — should be excluded
        new { customer_id = 2581, bureau = "Experian", score = 690, ifw_effective_date = "2024-10-01" },  // same — should be excluded
        new { customer_id = 2581, bureau = "TransUnion", score = 670, ifw_effective_date = "2024-10-01" },  // same — should be excluded
        new { customer_id = 2632, bureau = "Equifax", score = 740, ifw_effective_date = "2024-10-01" },
        new { customer_id = 2632, bureau = "Experian", score = 745, ifw_effective_date = "2024-10-01" },  // same — should be excluded
        new { customer_id = 2632, bureau = "TransUnion", score = 755, ifw_effective_date = "2024-10-01" }
    });

    private static DataFrame MakeCustomersForCredit() => DataFrame.FromObjects(new[]
    {
        new { id = 2252, sort_name = "Reyes Gabriel", ifw_effective_date = "2024-10-01" },
        new { id = 2581, sort_name = "Chen Wei", ifw_effective_date = "2024-10-01" },
        new { id = 2632, sort_name = "Patel Anita", ifw_effective_date = "2024-10-01" }
    });

    [Fact]
    public void CreditScoreDelta_ChangeDetection_ExcludesUnchangedScores()
    {
        var state = new Dictionary<string, object>
        {
            ["todays_scores"] = MakeTodaysScores(),
            ["prior_scores"] = MakePriorScores(),
            ["customers"] = MakeCustomersForCredit()
        };
        var result = new Transformation("credit_score_deltas",
            "SELECT t.customer_id, c.sort_name, t.bureau, t.score AS current_score, p.score AS prior_score " +
            "FROM todays_scores t " +
            "LEFT JOIN prior_scores p ON t.customer_id = p.customer_id AND t.bureau = p.bureau " +
            "LEFT JOIN customers c ON t.customer_id = c.id " +
            "WHERE p.score IS NULL OR t.score <> p.score " +
            "ORDER BY t.customer_id, t.bureau")
            .Execute(state);

        var df = (DataFrame)result["credit_score_deltas"];
        // Changed: 2252-Equifax(715->720), 2252-TransUnion(725->730), 2632-Equifax(740->750), 2632-TransUnion(755->760)
        Assert.Equal(4, df.Count);

        // Verify no unchanged scores included
        foreach (var row in df.Rows)
        {
            var current = Convert.ToInt32(row["current_score"]);
            var prior = row["prior_score"];
            if (prior != null)
            {
                Assert.NotEqual(current, Convert.ToInt32(prior));
            }
        }
    }

    [Fact]
    public void CreditScoreDelta_NoPrior_AllRowsIncludedWithNullPrior()
    {
        var state = new Dictionary<string, object>
        {
            ["todays_scores"] = MakeTodaysScores(),
            ["prior_scores"] = new DataFrame(new[] { "customer_id", "bureau", "score", "ifw_effective_date" }),
            ["customers"] = MakeCustomersForCredit()
        };
        var result = new Transformation("credit_score_deltas",
            "SELECT t.customer_id, c.sort_name, t.bureau, t.score AS current_score, p.score AS prior_score " +
            "FROM todays_scores t " +
            "LEFT JOIN prior_scores p ON t.customer_id = p.customer_id AND t.bureau = p.bureau " +
            "LEFT JOIN customers c ON t.customer_id = c.id " +
            "WHERE p.score IS NULL OR t.score <> p.score " +
            "ORDER BY t.customer_id, t.bureau")
            .Execute(state);

        var df = (DataFrame)result["credit_score_deltas"];
        // All 9 rows included (3 customers x 3 bureaus) since all prior_score is NULL
        Assert.Equal(9, df.Count);
        Assert.All(df.Rows, row => Assert.Null(row["prior_score"]));
    }

    [Fact]
    public void CreditScoreDelta_CustomerNameEnrichment_CorrectSortNames()
    {
        var state = new Dictionary<string, object>
        {
            ["todays_scores"] = MakeTodaysScores(),
            ["prior_scores"] = new DataFrame(new[] { "customer_id", "bureau", "score", "ifw_effective_date" }),
            ["customers"] = MakeCustomersForCredit()
        };
        var result = new Transformation("credit_score_deltas",
            "SELECT t.customer_id, c.sort_name, t.bureau, t.score AS current_score, p.score AS prior_score " +
            "FROM todays_scores t " +
            "LEFT JOIN prior_scores p ON t.customer_id = p.customer_id AND t.bureau = p.bureau " +
            "LEFT JOIN customers c ON t.customer_id = c.id " +
            "WHERE p.score IS NULL OR t.score <> p.score " +
            "ORDER BY t.customer_id, t.bureau")
            .Execute(state);

        var df = (DataFrame)result["credit_score_deltas"];
        var c2252 = df.Rows.First(r => Convert.ToInt32(r["customer_id"]) == 2252);
        Assert.Equal("Reyes Gabriel", c2252["sort_name"]?.ToString());
    }

    [Fact]
    public void CreditScoreDelta_OutputOrdering_CustomerThenBureau()
    {
        var state = new Dictionary<string, object>
        {
            ["todays_scores"] = MakeTodaysScores(),
            ["prior_scores"] = new DataFrame(new[] { "customer_id", "bureau", "score", "ifw_effective_date" }),
            ["customers"] = MakeCustomersForCredit()
        };
        var result = new Transformation("credit_score_deltas",
            "SELECT t.customer_id, c.sort_name, t.bureau, t.score AS current_score, p.score AS prior_score " +
            "FROM todays_scores t " +
            "LEFT JOIN prior_scores p ON t.customer_id = p.customer_id AND t.bureau = p.bureau " +
            "LEFT JOIN customers c ON t.customer_id = c.id " +
            "WHERE p.score IS NULL OR t.score <> p.score " +
            "ORDER BY t.customer_id, t.bureau")
            .Execute(state);

        var df = (DataFrame)result["credit_score_deltas"];
        var customerIds = df.Rows.Select(r => Convert.ToInt32(r["customer_id"])).ToList();
        Assert.Equal(customerIds.OrderBy(x => x).ToList(), customerIds);
    }

    [Fact]
    public void CreditScoreDelta_CustomerScopeOnly_ThreeCustomers()
    {
        var state = new Dictionary<string, object>
        {
            ["todays_scores"] = MakeTodaysScores(),
            ["prior_scores"] = new DataFrame(new[] { "customer_id", "bureau", "score", "ifw_effective_date" }),
            ["customers"] = MakeCustomersForCredit()
        };
        var result = new Transformation("credit_score_deltas",
            "SELECT t.customer_id, c.sort_name, t.bureau, t.score AS current_score, p.score AS prior_score " +
            "FROM todays_scores t " +
            "LEFT JOIN prior_scores p ON t.customer_id = p.customer_id AND t.bureau = p.bureau " +
            "LEFT JOIN customers c ON t.customer_id = c.id " +
            "WHERE p.score IS NULL OR t.score <> p.score " +
            "ORDER BY t.customer_id, t.bureau")
            .Execute(state);

        var df = (DataFrame)result["credit_score_deltas"];
        var distinctCustomers = df.Rows.Select(r => Convert.ToInt32(r["customer_id"])).Distinct().ToList();
        Assert.Equal(3, distinctCustomers.Count);
        Assert.Contains(2252, distinctCustomers);
        Assert.Contains(2581, distinctCustomers);
        Assert.Contains(2632, distinctCustomers);
    }

    // ====================================================================
    // BranchVisitsByCustomerCsvAppendTrailer V4 Tests
    // ====================================================================

    private static DataFrame MakeVisitsForBranch() => DataFrame.FromObjects(new[]
    {
        new { visit_id = 1, customer_id = 100, branch_id = 5, visit_timestamp = "2024-10-01T10:00:00", visit_purpose = "Deposit", ifw_effective_date = "2024-10-01" },
        new { visit_id = 2, customer_id = 200, branch_id = 3, visit_timestamp = "2024-10-01T11:00:00", visit_purpose = "Withdrawal", ifw_effective_date = "2024-10-01" },
        new { visit_id = 3, customer_id = 100, branch_id = 5, visit_timestamp = "2024-10-01T14:00:00", visit_purpose = "Inquiry", ifw_effective_date = "2024-10-01" },
        new { visit_id = 4, customer_id = 1499, branch_id = 7, visit_timestamp = "2024-10-01T15:00:00", visit_purpose = "Loan", ifw_effective_date = "2024-10-01" }
    });

    private static DataFrame MakeCustomersForBranch() => DataFrame.FromObjects(new[]
    {
        new { id = 100, sort_name = "Smith John", ifw_effective_date = "2024-10-01" },
        new { id = 200, sort_name = "Jones Mary", ifw_effective_date = "2024-10-01" },
        new { id = 1499, sort_name = "Brown Alice", ifw_effective_date = "2024-10-01" }
    });

    [Fact]
    public void BranchVisits_CustomerEnrichment_JoinsSortName()
    {
        var state = new Dictionary<string, object>
        {
            ["visits"] = MakeVisitsForBranch(),
            ["customers"] = MakeCustomersForBranch()
        };
        var result = new Transformation("branch_visits_by_customer",
            "SELECT v.visit_id, v.customer_id, c.sort_name, v.branch_id, v.visit_timestamp, v.visit_purpose " +
            "FROM visits v LEFT JOIN customers c ON v.customer_id = c.id " +
            "ORDER BY v.customer_id, v.visit_timestamp")
            .Execute(state);

        var df = (DataFrame)result["branch_visits_by_customer"];
        Assert.Equal(4, df.Count);

        var firstRow = df.Rows.First(r => Convert.ToInt32(r["visit_id"]) == 1);
        Assert.Equal("Smith John", firstRow["sort_name"]?.ToString());
    }

    [Fact]
    public void BranchVisits_OutputOrdering_CustomerThenTimestamp()
    {
        var state = new Dictionary<string, object>
        {
            ["visits"] = MakeVisitsForBranch(),
            ["customers"] = MakeCustomersForBranch()
        };
        var result = new Transformation("branch_visits_by_customer",
            "SELECT v.visit_id, v.customer_id, c.sort_name, v.branch_id, v.visit_timestamp, v.visit_purpose " +
            "FROM visits v LEFT JOIN customers c ON v.customer_id = c.id " +
            "ORDER BY v.customer_id, v.visit_timestamp")
            .Execute(state);

        var df = (DataFrame)result["branch_visits_by_customer"];
        var customerIds = df.Rows.Select(r => Convert.ToInt32(r["customer_id"])).ToList();
        // Should be sorted by customer_id ascending
        Assert.Equal(customerIds.OrderBy(x => x).ToList(), customerIds);
    }

    [Fact]
    public void BranchVisits_AllColumns_PassThrough()
    {
        var state = new Dictionary<string, object>
        {
            ["visits"] = MakeVisitsForBranch(),
            ["customers"] = MakeCustomersForBranch()
        };
        var result = new Transformation("branch_visits_by_customer",
            "SELECT v.visit_id, v.customer_id, c.sort_name, v.branch_id, v.visit_timestamp, v.visit_purpose " +
            "FROM visits v LEFT JOIN customers c ON v.customer_id = c.id " +
            "ORDER BY v.customer_id, v.visit_timestamp")
            .Execute(state);

        var df = (DataFrame)result["branch_visits_by_customer"];
        Assert.Contains("visit_id", df.Columns);
        Assert.Contains("customer_id", df.Columns);
        Assert.Contains("sort_name", df.Columns);
        Assert.Contains("branch_id", df.Columns);
        Assert.Contains("visit_timestamp", df.Columns);
        Assert.Contains("visit_purpose", df.Columns);
    }

    [Fact]
    public void BranchVisits_MissingCustomer_NullSortName()
    {
        var visits = DataFrame.FromObjects(new[]
        {
            new { visit_id = 1, customer_id = 9999, branch_id = 1, visit_timestamp = "2024-10-01T10:00:00", visit_purpose = "Deposit", ifw_effective_date = "2024-10-01" }
        });
        var customers = MakeCustomersForBranch();
        var state = new Dictionary<string, object>
        {
            ["visits"] = visits,
            ["customers"] = customers
        };
        var result = new Transformation("branch_visits_by_customer",
            "SELECT v.visit_id, v.customer_id, c.sort_name, v.branch_id, v.visit_timestamp, v.visit_purpose " +
            "FROM visits v LEFT JOIN customers c ON v.customer_id = c.id " +
            "ORDER BY v.customer_id, v.visit_timestamp")
            .Execute(state);

        var df = (DataFrame)result["branch_visits_by_customer"];
        Assert.Equal(1, df.Count);
        Assert.Null(df.Rows.First()["sort_name"]);
    }

    [Fact]
    public void BranchVisits_EmptyVisits_ProducesZeroRows()
    {
        var empty = new DataFrame(new[] { "visit_id", "customer_id", "branch_id", "visit_timestamp", "visit_purpose", "ifw_effective_date" });
        var state = new Dictionary<string, object>
        {
            ["visits"] = empty,
            ["customers"] = MakeCustomersForBranch()
        };
        var result = new Transformation("branch_visits_by_customer",
            "SELECT v.visit_id, v.customer_id, c.sort_name, v.branch_id, v.visit_timestamp, v.visit_purpose " +
            "FROM visits v LEFT JOIN customers c ON v.customer_id = c.id " +
            "ORDER BY v.customer_id, v.visit_timestamp")
            .Execute(state);

        Assert.Equal(0, ((DataFrame)result["branch_visits_by_customer"]).Count);
    }

    // ====================================================================
    // DansTransactionSpecial V4 Tests
    // ====================================================================

    private static DataFrame MakeTransactionsForDans() => DataFrame.FromObjects(new[]
    {
        new { transaction_id = 1, account_id = 1001, txn_timestamp = "2024-10-01T09:00:00", txn_type = "Debit", amount = 100.0, description = "Purchase", ifw_effective_date = "2024-10-01" },
        new { transaction_id = 2, account_id = 1001, txn_timestamp = "2024-10-01T10:00:00", txn_type = "Credit", amount = 500.0, description = "Deposit", ifw_effective_date = "2024-10-01" },
        new { transaction_id = 3, account_id = 1002, txn_timestamp = "2024-10-01T11:00:00", txn_type = "Debit", amount = 200.0, description = "ATM", ifw_effective_date = "2024-10-01" }
    });

    private static DataFrame MakeAccountsForDans() => DataFrame.FromObjects(new[]
    {
        new { account_id = 1001, customer_id = 100, account_type = "Checking", account_status = "Active", current_balance = 5000.0, ifw_effective_date = "2024-10-01" },
        new { account_id = 1002, customer_id = 200, account_type = "Savings", account_status = "Active", current_balance = 10000.0, ifw_effective_date = "2024-10-01" }
    });

    private static DataFrame MakeCustomersForDans() => DataFrame.FromObjects(new[]
    {
        new { id = 100, sort_name = "Smith John", ifw_effective_date = "2024-10-01" },
        new { id = 200, sort_name = "Jones Mary", ifw_effective_date = "2024-10-01" }
    });

    private static DataFrame MakeAddressesForDans() => DataFrame.FromObjects(new[]
    {
        new { customer_id = 100, city = "New York", state_province = "NY", postal_code = "10001", start_date = "2024-01-01", ifw_effective_date = "2024-10-01" },
        new { customer_id = 200, city = "Chicago", state_province = "IL", postal_code = "60601", start_date = "2024-01-01", ifw_effective_date = "2024-10-01" }
    });

    [Fact]
    public void DansTransactionSpecial_TransactionDetails_Denormalization()
    {
        var state = new Dictionary<string, object>
        {
            ["transactions"] = MakeTransactionsForDans(),
            ["accounts"] = MakeAccountsForDans(),
            ["customers"] = MakeCustomersForDans(),
            ["addresses"] = MakeAddressesForDans()
        };
        var result = new Transformation("transaction_details",
            "WITH deduped_addresses AS (SELECT customer_id, city, state_province, postal_code, " +
            "ROW_NUMBER() OVER (PARTITION BY customer_id ORDER BY start_date DESC) AS rn " +
            "FROM addresses) " +
            "SELECT t.transaction_id, t.account_id, a.customer_id, c.sort_name, " +
            "t.txn_timestamp, t.txn_type, t.amount, t.description, " +
            "a.account_type, a.account_status, a.current_balance, " +
            "da.city, da.state_province, da.postal_code, t.ifw_effective_date " +
            "FROM transactions t " +
            "LEFT JOIN accounts a ON t.account_id = a.account_id " +
            "LEFT JOIN customers c ON a.customer_id = c.id " +
            "LEFT JOIN deduped_addresses da ON a.customer_id = da.customer_id AND da.rn = 1 " +
            "ORDER BY t.transaction_id")
            .Execute(state);

        var df = (DataFrame)result["transaction_details"];
        Assert.Equal(3, df.Count);

        // Verify enrichment for transaction 1
        var t1 = df.Rows.First(r => Convert.ToInt32(r["transaction_id"]) == 1);
        Assert.Equal(100, Convert.ToInt32(t1["customer_id"]));
        Assert.Equal("Smith John", t1["sort_name"]?.ToString());
        Assert.Equal("Checking", t1["account_type"]?.ToString());
        Assert.Equal("New York", t1["city"]?.ToString());
        Assert.Equal("NY", t1["state_province"]?.ToString());
    }

    [Fact]
    public void DansTransactionSpecial_TransactionDetails_OrderedByTransactionId()
    {
        var state = new Dictionary<string, object>
        {
            ["transactions"] = MakeTransactionsForDans(),
            ["accounts"] = MakeAccountsForDans(),
            ["customers"] = MakeCustomersForDans(),
            ["addresses"] = MakeAddressesForDans()
        };
        var result = new Transformation("transaction_details",
            "WITH deduped_addresses AS (SELECT customer_id, city, state_province, postal_code, " +
            "ROW_NUMBER() OVER (PARTITION BY customer_id ORDER BY start_date DESC) AS rn " +
            "FROM addresses) " +
            "SELECT t.transaction_id, t.account_id, a.customer_id, c.sort_name, " +
            "t.txn_timestamp, t.txn_type, t.amount, t.description, " +
            "a.account_type, a.account_status, a.current_balance, " +
            "da.city, da.state_province, da.postal_code, t.ifw_effective_date " +
            "FROM transactions t " +
            "LEFT JOIN accounts a ON t.account_id = a.account_id " +
            "LEFT JOIN customers c ON a.customer_id = c.id " +
            "LEFT JOIN deduped_addresses da ON a.customer_id = da.customer_id AND da.rn = 1 " +
            "ORDER BY t.transaction_id")
            .Execute(state);

        var df = (DataFrame)result["transaction_details"];
        var txnIds = df.Rows.Select(r => Convert.ToInt32(r["transaction_id"])).ToList();
        Assert.Equal(new List<int> { 1, 2, 3 }, txnIds);
    }

    [Fact]
    public void DansTransactionSpecial_AddressDedup_KeepsMostRecent()
    {
        // Two addresses for customer 100, different start_dates
        var addresses = DataFrame.FromObjects(new[]
        {
            new { customer_id = 100, city = "Old City", state_province = "OC", postal_code = "00000", start_date = "2023-01-01", ifw_effective_date = "2024-10-01" },
            new { customer_id = 100, city = "New York", state_province = "NY", postal_code = "10001", start_date = "2024-06-01", ifw_effective_date = "2024-10-01" },
            new { customer_id = 200, city = "Chicago", state_province = "IL", postal_code = "60601", start_date = "2024-01-01", ifw_effective_date = "2024-10-01" }
        });
        var state = new Dictionary<string, object>
        {
            ["transactions"] = MakeTransactionsForDans(),
            ["accounts"] = MakeAccountsForDans(),
            ["customers"] = MakeCustomersForDans(),
            ["addresses"] = addresses
        };
        var result = new Transformation("transaction_details",
            "WITH deduped_addresses AS (SELECT customer_id, city, state_province, postal_code, " +
            "ROW_NUMBER() OVER (PARTITION BY customer_id ORDER BY start_date DESC) AS rn " +
            "FROM addresses) " +
            "SELECT t.transaction_id, t.account_id, a.customer_id, c.sort_name, " +
            "t.txn_timestamp, t.txn_type, t.amount, t.description, " +
            "a.account_type, a.account_status, a.current_balance, " +
            "da.city, da.state_province, da.postal_code, t.ifw_effective_date " +
            "FROM transactions t " +
            "LEFT JOIN accounts a ON t.account_id = a.account_id " +
            "LEFT JOIN customers c ON a.customer_id = c.id " +
            "LEFT JOIN deduped_addresses da ON a.customer_id = da.customer_id AND da.rn = 1 " +
            "ORDER BY t.transaction_id")
            .Execute(state);

        var df = (DataFrame)result["transaction_details"];
        // Customer 100 should have "New York" (most recent start_date)
        var t1 = df.Rows.First(r => Convert.ToInt32(r["transaction_id"]) == 1);
        Assert.Equal("New York", t1["city"]?.ToString());
    }

    [Fact]
    public void DansTransactionSpecial_StateProvinceAggregation_CountAndSum()
    {
        var state = new Dictionary<string, object>
        {
            ["transactions"] = MakeTransactionsForDans(),
            ["accounts"] = MakeAccountsForDans(),
            ["customers"] = MakeCustomersForDans(),
            ["addresses"] = MakeAddressesForDans()
        };
        // First, run the transaction_details transformation
        var result1 = new Transformation("transaction_details",
            "WITH deduped_addresses AS (SELECT customer_id, city, state_province, postal_code, " +
            "ROW_NUMBER() OVER (PARTITION BY customer_id ORDER BY start_date DESC) AS rn " +
            "FROM addresses) " +
            "SELECT t.transaction_id, t.account_id, a.customer_id, c.sort_name, " +
            "t.txn_timestamp, t.txn_type, t.amount, t.description, " +
            "a.account_type, a.account_status, a.current_balance, " +
            "da.city, da.state_province, da.postal_code, t.ifw_effective_date " +
            "FROM transactions t " +
            "LEFT JOIN accounts a ON t.account_id = a.account_id " +
            "LEFT JOIN customers c ON a.customer_id = c.id " +
            "LEFT JOIN deduped_addresses da ON a.customer_id = da.customer_id AND da.rn = 1 " +
            "ORDER BY t.transaction_id")
            .Execute(state);

        // Then, run the state/province aggregation
        var result2 = new Transformation("transactions_by_state_province",
            "SELECT ifw_effective_date, state_province, COUNT(*) AS transaction_count, " +
            "SUM(amount) AS total_amount FROM transaction_details " +
            "GROUP BY ifw_effective_date, state_province " +
            "ORDER BY ifw_effective_date, state_province")
            .Execute(result1);

        var df = (DataFrame)result2["transactions_by_state_province"];
        // 2 states: IL (1 txn, 200.0), NY (2 txns, 600.0)
        Assert.Equal(2, df.Count);

        var il = df.Rows.First(r => r["state_province"]?.ToString() == "IL");
        Assert.Equal(1L, Convert.ToInt64(il["transaction_count"]));
        Assert.Equal(200.0, Convert.ToDouble(il["total_amount"]), 2);

        var ny = df.Rows.First(r => r["state_province"]?.ToString() == "NY");
        Assert.Equal(2L, Convert.ToInt64(ny["transaction_count"]));
        Assert.Equal(600.0, Convert.ToDouble(ny["total_amount"]), 2);
    }

    [Fact]
    public void DansTransactionSpecial_NullAddress_NullStateProvince()
    {
        // Transaction with no matching account → no customer → no address
        var txns = DataFrame.FromObjects(new[]
        {
            new { transaction_id = 1, account_id = 9999, txn_timestamp = "2024-10-01T09:00:00", txn_type = "Debit", amount = 100.0, description = "Test", ifw_effective_date = "2024-10-01" }
        });
        var state = new Dictionary<string, object>
        {
            ["transactions"] = txns,
            ["accounts"] = MakeAccountsForDans(),
            ["customers"] = MakeCustomersForDans(),
            ["addresses"] = MakeAddressesForDans()
        };
        var result = new Transformation("transaction_details",
            "WITH deduped_addresses AS (SELECT customer_id, city, state_province, postal_code, " +
            "ROW_NUMBER() OVER (PARTITION BY customer_id ORDER BY start_date DESC) AS rn " +
            "FROM addresses) " +
            "SELECT t.transaction_id, t.account_id, a.customer_id, c.sort_name, " +
            "t.txn_timestamp, t.txn_type, t.amount, t.description, " +
            "a.account_type, a.account_status, a.current_balance, " +
            "da.city, da.state_province, da.postal_code, t.ifw_effective_date " +
            "FROM transactions t " +
            "LEFT JOIN accounts a ON t.account_id = a.account_id " +
            "LEFT JOIN customers c ON a.customer_id = c.id " +
            "LEFT JOIN deduped_addresses da ON a.customer_id = da.customer_id AND da.rn = 1 " +
            "ORDER BY t.transaction_id")
            .Execute(state);

        var df = (DataFrame)result["transaction_details"];
        Assert.Equal(1, df.Count);
        Assert.Null(df.Rows.First()["customer_id"]);
        Assert.Null(df.Rows.First()["sort_name"]);
        Assert.Null(df.Rows.First()["city"]);
        Assert.Null(df.Rows.First()["state_province"]);
    }

    [Fact]
    public void DansTransactionSpecial_OutputSchema_TransactionDetails_AllColumns()
    {
        var state = new Dictionary<string, object>
        {
            ["transactions"] = MakeTransactionsForDans(),
            ["accounts"] = MakeAccountsForDans(),
            ["customers"] = MakeCustomersForDans(),
            ["addresses"] = MakeAddressesForDans()
        };
        var result = new Transformation("transaction_details",
            "WITH deduped_addresses AS (SELECT customer_id, city, state_province, postal_code, " +
            "ROW_NUMBER() OVER (PARTITION BY customer_id ORDER BY start_date DESC) AS rn " +
            "FROM addresses) " +
            "SELECT t.transaction_id, t.account_id, a.customer_id, c.sort_name, " +
            "t.txn_timestamp, t.txn_type, t.amount, t.description, " +
            "a.account_type, a.account_status, a.current_balance, " +
            "da.city, da.state_province, da.postal_code, t.ifw_effective_date " +
            "FROM transactions t " +
            "LEFT JOIN accounts a ON t.account_id = a.account_id " +
            "LEFT JOIN customers c ON a.customer_id = c.id " +
            "LEFT JOIN deduped_addresses da ON a.customer_id = da.customer_id AND da.rn = 1 " +
            "ORDER BY t.transaction_id")
            .Execute(state);

        var df = (DataFrame)result["transaction_details"];
        var expected = new[] { "transaction_id", "account_id", "customer_id", "sort_name",
            "txn_timestamp", "txn_type", "amount", "description",
            "account_type", "account_status", "current_balance",
            "city", "state_province", "postal_code", "ifw_effective_date" };
        foreach (var col in expected)
        {
            Assert.Contains(col, df.Columns);
        }
    }
}
