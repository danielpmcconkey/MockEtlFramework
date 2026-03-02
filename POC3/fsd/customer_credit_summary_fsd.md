# CustomerCreditSummary -- Functional Specification Document

## 1. Overview & Tier Selection

**Job**: CustomerCreditSummaryV2
**Config**: `customer_credit_summary_v2.json`
**Tier**: 2 (Framework + Minimal External) -- `DataSourcing -> External (aggregation with decimal precision) -> CsvFileWriter`

This job produces a per-customer credit summary combining average credit score, total loan balance, total account balance, and counts of loans and accounts. It iterates all customers and enriches each with aggregated data from credit scores, loan accounts, and regular accounts.

**Tier Justification**: The business logic requires decimal-precision averaging for credit scores (BR-2). V1 computes `avg_credit_score` using C#'s `Enumerable.Average()` on a `List<decimal>`, which yields up to 28-29 significant digits [CustomerCreditSummaryBuilder.cs:84]. SQLite's `AVG()` function returns a `double` (IEEE 754, ~15-16 significant digits), producing different string representations for non-terminating decimals. For example, three scores `[750, 680, 710]` average to `713.33333333333333333333333333` in C# decimal vs `713.333333333333` in SQLite double -- these are different CSV output strings. Since BRD confirms no rounding is applied (OQ-2), Tier 1 SQL cannot produce byte-identical output. The External module handles all aggregation with decimal arithmetic, keeping the logic in one place rather than splitting it awkwardly across SQL and C#.

The External module is still "minimal" in the Tier 2 sense: DataSourcing handles all data fetching (4 tables, effective date injection), and CsvFileWriter handles all output formatting. The External does only the business logic aggregation.

---

## 2. V2 Module Chain

```
DataSourcing (customers)
    -> DataSourcing (accounts)
        -> DataSourcing (credit_scores)
            -> DataSourcing (loan_accounts)
                -> External (CustomerCreditSummaryV2Processor -- LINQ aggregation with decimal precision)
                    -> CsvFileWriter (Output/double_secret_curated/customer_credit_summary.csv)
```

### Module 1: DataSourcing -- customers

| Property | Value |
|----------|-------|
| resultName | `customers` |
| schema | `datalake` |
| table | `customers` |
| columns | `id`, `first_name`, `last_name` |

**Note**: `as_of` is not listed in the columns array. Per framework behavior [DataSourcing.cs:69-72], it is automatically appended to the SELECT and included in the output DataFrame. V1 accesses `custRow["as_of"]` [CustomerCreditSummaryBuilder.cs:121] because DataSourcing injects it.

### Module 2: DataSourcing -- accounts

| Property | Value |
|----------|-------|
| resultName | `accounts` |
| schema | `datalake` |
| table | `accounts` |
| columns | `customer_id`, `current_balance` |

**AP4 fix**: V1 sources `account_id`, `account_type`, `account_status`, `current_balance` [customer_credit_summary.json:17] but the External module only accesses `customer_id` and `current_balance` [CustomerCreditSummaryBuilder.cs:62-63]. The other three columns are unused. V2 sources only the two columns actually needed.

### Module 3: DataSourcing -- credit_scores

| Property | Value |
|----------|-------|
| resultName | `credit_scores` |
| schema | `datalake` |
| table | `credit_scores` |
| columns | `customer_id`, `score` |

**AP4 fix**: V1 sources `credit_score_id`, `customer_id`, `bureau`, `score` [customer_credit_summary.json:24] but the External module only accesses `customer_id` and `score` [CustomerCreditSummaryBuilder.cs:35-36]. V2 sources only the two columns needed.

### Module 4: DataSourcing -- loan_accounts

| Property | Value |
|----------|-------|
| resultName | `loan_accounts` |
| schema | `datalake` |
| table | `loan_accounts` |
| columns | `customer_id`, `current_balance` |

**AP4 fix**: V1 sources `loan_id`, `customer_id`, `loan_type`, `current_balance` [customer_credit_summary.json:31] but the External module only accesses `customer_id` and `current_balance` [CustomerCreditSummaryBuilder.cs:48-49]. V2 sources only the two columns needed.

### Module 5: External -- CustomerCreditSummaryV2Processor

| Property | Value |
|----------|-------|
| assemblyPath | `/workspace/MockEtlFramework/ExternalModules/bin/Debug/net8.0/ExternalModules.dll` |
| typeName | `ExternalModules.CustomerCreditSummaryV2Processor` |

**Responsibilities (and nothing more):**
1. Retrieve `customers`, `accounts`, `credit_scores`, `loan_accounts` from shared state
2. Compound empty guard: if any of the four DataFrames is null or empty, produce an empty output DataFrame (BR-1)
3. Aggregate credit scores per customer using decimal arithmetic (BR-2)
4. Aggregate loan balances and counts per customer using decimal arithmetic (BR-3)
5. Aggregate account balances and counts per customer using decimal arithmetic (BR-4)
6. Build output DataFrame with one row per customer (BR-8)
7. Store result as `output` in shared state

**Implementation approach**: Use LINQ `GroupBy` and `ToLookup` for aggregations instead of manual dictionary iteration. This eliminates AP6 (row-by-row iteration) while preserving decimal precision.

### Module 6: CsvFileWriter

| Property | Value | Evidence |
|----------|-------|----------|
| source | `output` | [customer_credit_summary.json:47] |
| outputFile | `Output/double_secret_curated/customer_credit_summary.csv` | V2 path convention |
| includeHeader | `true` | [customer_credit_summary.json:49] |
| writeMode | `Overwrite` | [customer_credit_summary.json:50] |
| lineEnding | `LF` | [customer_credit_summary.json:51] |
| trailerFormat | (not configured) | [customer_credit_summary.json:45-53] -- no trailer in V1 |

---

## 3. Anti-Pattern Analysis

### Anti-Patterns Identified and Eliminated

| ID | Anti-Pattern | V1 Evidence | V2 Resolution |
|----|-------------|-------------|---------------|
| AP1 | Dead-end sourcing | V1 config sources `datalake.segments` [customer_credit_summary.json:34-38] but the External module never retrieves or references it [CustomerCreditSummaryBuilder.cs:17-20 -- only customers, accounts, credit_scores, loan_accounts are read from shared state] | **Eliminated.** V2 does not source the `segments` table. Only the four tables actually used are sourced. |
| AP3 | Unnecessary External module | V1 uses a full External module for logic that is *mostly* expressible in SQL. However, the decimal-precision averaging for `avg_credit_score` cannot be replicated in SQLite (see Tier Justification). | **Partially eliminated.** V2 retains an External module but ONLY because SQLite's `AVG()` uses `double` precision, which produces different string output from C#'s `decimal.Average()`. The External is justified and minimal -- DataSourcing fetches data, CsvFileWriter handles output. |
| AP4 | Unused columns | V1 sources `account_id`, `account_type`, `account_status` from accounts [customer_credit_summary.json:17]; `credit_score_id`, `bureau` from credit_scores [customer_credit_summary.json:24]; `loan_id`, `loan_type` from loan_accounts [customer_credit_summary.json:31]. None of these are used in the External module [CustomerCreditSummaryBuilder.cs -- only customer_id and current_balance/score accessed]. | **Eliminated.** V2 sources only the columns actually used: `customer_id` and `current_balance` from accounts, `customer_id` and `score` from credit_scores, `customer_id` and `current_balance` from loan_accounts. |
| AP6 | Row-by-row iteration | V1 uses manual `foreach` loops with `Dictionary<int, ...>` to group and aggregate [CustomerCreditSummaryBuilder.cs:33-70, 74-123] | **Eliminated.** V2 uses LINQ `ToLookup`/`GroupBy` for set-based aggregation. |

### Output-Affecting Wrinkles

| ID | Wrinkle | Applicability | V2 Handling |
|----|---------|--------------|-------------|
| W9 | Wrong writeMode | V1 uses `Overwrite` [customer_credit_summary.json:50]. For a multi-day auto-advance run, only the last effective date's output survives on disk. This may or may not be intentional. | **Reproduced.** V2 uses `Overwrite` to match V1 exactly. `// V1 uses Overwrite -- prior days' data is lost on each run.` |

No other W-codes apply to this job. There is no integer division (W4), no banker's rounding (W5), no double-precision accumulation (W6 -- V1 uses decimal), no trailer (W7/W8), no Sunday skip (W1), no weekend fallback (W2), no boundary rows (W3a/b/c), no absurd numParts (W10), no header-every-append (W12).

---

## 4. Output Schema

| Column | Type | Source | Transformation | Evidence |
|--------|------|--------|---------------|----------|
| customer_id | int | customers.id | `Convert.ToInt32(custRow["id"])` | [CustomerCreditSummaryBuilder.cs:76] |
| first_name | string | customers.first_name | `ToString()` with null coalesce to `""` | [CustomerCreditSummaryBuilder.cs:77] |
| last_name | string | customers.last_name | `ToString()` with null coalesce to `""` | [CustomerCreditSummaryBuilder.cs:78] |
| avg_credit_score | decimal or DBNull | credit_scores.score | Average of all scores per customer (`decimal` precision). `DBNull.Value` if customer has no credit scores. Rendered as empty string in CSV. | [CustomerCreditSummaryBuilder.cs:82-89] |
| total_loan_balance | decimal | loan_accounts.current_balance | Sum of all loan balances per customer. Default `0` if no loans. | [CustomerCreditSummaryBuilder.cs:92-98] |
| total_account_balance | decimal | accounts.current_balance | Sum of all account balances per customer. Default `0` if no accounts. | [CustomerCreditSummaryBuilder.cs:102-108] |
| loan_count | int | loan_accounts | Count of loan records per customer. Default `0` if no loans. | [CustomerCreditSummaryBuilder.cs:93,98] |
| account_count | int | accounts | Count of account records per customer. Default `0` if no accounts. | [CustomerCreditSummaryBuilder.cs:103,108] |
| as_of | date | customers.as_of | Pass-through from customer row. Injected by DataSourcing. | [CustomerCreditSummaryBuilder.cs:121] |

**Column order**: The output column order is defined by the `outputColumns` list [CustomerCreditSummaryBuilder.cs:10-15]: `customer_id, first_name, last_name, avg_credit_score, total_loan_balance, total_account_balance, loan_count, account_count, as_of`.

---

## 5. SQL Design

**N/A** -- This job uses Tier 2 with an External module for aggregation. No Transformation (SQL) module is used.

The reason SQL is not used is documented in the Tier Justification (Section 1): SQLite's `AVG()` returns `double` which cannot reproduce the `decimal`-precision averages that V1 produces via `List<decimal>.Average()`.

---

## 6. V2 Job Config JSON

```json
{
  "jobName": "CustomerCreditSummaryV2",
  "firstEffectiveDate": "2024-10-01",
  "modules": [
    {
      "type": "DataSourcing",
      "resultName": "customers",
      "schema": "datalake",
      "table": "customers",
      "columns": ["id", "first_name", "last_name"]
    },
    {
      "type": "DataSourcing",
      "resultName": "accounts",
      "schema": "datalake",
      "table": "accounts",
      "columns": ["customer_id", "current_balance"]
    },
    {
      "type": "DataSourcing",
      "resultName": "credit_scores",
      "schema": "datalake",
      "table": "credit_scores",
      "columns": ["customer_id", "score"]
    },
    {
      "type": "DataSourcing",
      "resultName": "loan_accounts",
      "schema": "datalake",
      "table": "loan_accounts",
      "columns": ["customer_id", "current_balance"]
    },
    {
      "type": "External",
      "assemblyPath": "/workspace/MockEtlFramework/ExternalModules/bin/Debug/net8.0/ExternalModules.dll",
      "typeName": "ExternalModules.CustomerCreditSummaryV2Processor"
    },
    {
      "type": "CsvFileWriter",
      "source": "output",
      "outputFile": "Output/double_secret_curated/customer_credit_summary.csv",
      "includeHeader": true,
      "writeMode": "Overwrite",
      "lineEnding": "LF"
    }
  ]
}
```

**Changes from V1 config:**
- Removed `segments` DataSourcing (AP1 -- dead-end sourcing, never used by External module)
- Removed unused columns from `accounts` (`account_id`, `account_type`, `account_status`) (AP4)
- Removed unused columns from `credit_scores` (`credit_score_id`, `bureau`) (AP4)
- Removed unused columns from `loan_accounts` (`loan_id`, `loan_type`) (AP4)
- External typeName changed to `CustomerCreditSummaryV2Processor`
- Output path changed to `Output/double_secret_curated/customer_credit_summary.csv`
- All writer config params preserved: `includeHeader: true`, `writeMode: Overwrite`, `lineEnding: LF`

---

## 7. Writer Configuration

| Property | V1 Value | V2 Value | Match? |
|----------|----------|----------|--------|
| Writer type | CsvFileWriter | CsvFileWriter | Yes |
| source | `output` | `output` | Yes |
| includeHeader | `true` | `true` | Yes |
| writeMode | `Overwrite` | `Overwrite` | Yes |
| lineEnding | `LF` | `LF` | Yes |
| trailerFormat | (not configured) | (not configured) | Yes |
| outputFile | `Output/curated/customer_credit_summary.csv` | `Output/double_secret_curated/customer_credit_summary.csv` | Path differs (V2 convention) |

---

## 8. Proofmark Config Design

```yaml
comparison_target: "customer_credit_summary"
reader: csv
threshold: 100.0
csv:
  header_rows: 1
  trailer_rows: 0
```

**Rationale:**
- **reader: csv** -- V1 and V2 both produce CSV output via CsvFileWriter
- **header_rows: 1** -- `includeHeader: true` in both V1 and V2
- **trailer_rows: 0** -- No trailer configured in V1 or V2
- **threshold: 100.0** -- All rows must match; no non-deterministic fields identified
- **No EXCLUDED columns** -- All columns are deterministic. `as_of` comes from source data, not execution time.
- **No FUZZY columns** -- V2 uses the same `decimal` arithmetic as V1 for all numeric computations, so results should be byte-identical.

---

## 9. Traceability Matrix

| BRD Requirement | FSD Section | V2 Implementation |
|----------------|-------------|-------------------|
| BR-1: Compound empty guard (all 4 sources must be non-null and non-empty) | Section 2, Module 5 (responsibility #2) | External module checks all four DataFrames; returns empty output DataFrame if any is null/empty |
| BR-2: Average credit score with decimal precision, DBNull.Value for no scores | Section 2, Module 5 (responsibility #3); Section 4 (avg_credit_score column) | LINQ-based decimal Average(); DBNull.Value for customers with no scores |
| BR-3: Total loan balance and loan count aggregated per customer | Section 2, Module 5 (responsibility #4); Section 4 (total_loan_balance, loan_count) | LINQ GroupBy on customer_id, Sum/Count on loan_accounts |
| BR-4: Total account balance and account count aggregated per customer (no filtering) | Section 2, Module 5 (responsibility #5); Section 4 (total_account_balance, account_count) | LINQ GroupBy on customer_id, Sum/Count on accounts |
| BR-5: Customers with no loans get total_loan_balance=0, loan_count=0 | Section 2, Module 5; Section 4 | Default values when customer not found in loan lookup |
| BR-6: Customers with no accounts get total_account_balance=0, account_count=0 | Section 2, Module 5; Section 4 | Default values when customer not found in account lookup |
| BR-7: as_of from customer row | Section 4 (as_of column) | Pass-through from customer DataFrame row |
| BR-8: Customer-driven iteration (every customer produces one output row) | Section 2, Module 5 (responsibility #6) | Iterate over customers DataFrame; one output row per customer |
| BR-9: Segments table sourced but unused | Section 3, AP1 | **Eliminated.** V2 does not source segments. |
| BR-10: Unused columns in accounts, credit_scores, loan_accounts | Section 3, AP4 | **Eliminated.** V2 sources only columns actually used. |
| W9: Overwrite write mode | Section 3 (W9); Section 7 | Reproduced. V2 uses Overwrite to match V1. |

---

## 10. External Module Design

### File: `ExternalModules/CustomerCreditSummaryV2Processor.cs`

**Class**: `ExternalModules.CustomerCreditSummaryV2Processor`
**Interface**: `IExternalStep`

### Pseudocode

```
Execute(sharedState):
    // Retrieve DataFrames from shared state
    customers = sharedState["customers"] as DataFrame (null if missing)
    accounts = sharedState["accounts"] as DataFrame (null if missing)
    creditScores = sharedState["credit_scores"] as DataFrame (null if missing)
    loanAccounts = sharedState["loan_accounts"] as DataFrame (null if missing)

    // BR-1: Compound empty guard -- all four must be non-null and non-empty
    outputColumns = ["customer_id", "first_name", "last_name", "avg_credit_score",
                     "total_loan_balance", "total_account_balance", "loan_count",
                     "account_count", "as_of"]

    if ANY of (customers, accounts, creditScores, loanAccounts) is null or empty:
        sharedState["output"] = empty DataFrame with outputColumns
        return sharedState

    // Build lookups using LINQ (AP6 fix: set-based, not row-by-row)

    // BR-2: Credit score averages (decimal precision)
    scoresByCustomer = creditScores.Rows
        .ToLookup(row => Convert.ToInt32(row["customer_id"]))

    // BR-3: Loan aggregation
    loansByCustomer = loanAccounts.Rows
        .GroupBy(row => Convert.ToInt32(row["customer_id"]))
        .ToDictionary(
            g => g.Key,
            g => (totalBalance: g.Sum(r => Convert.ToDecimal(r["current_balance"])),
                  count: g.Count()))

    // BR-4: Account aggregation
    accountsByCustomer = accounts.Rows
        .GroupBy(row => Convert.ToInt32(row["customer_id"]))
        .ToDictionary(
            g => g.Key,
            g => (totalBalance: g.Sum(r => Convert.ToDecimal(r["current_balance"])),
                  count: g.Count()))

    // BR-8: Customer-driven iteration
    outputRows = []
    for each custRow in customers.Rows:
        customerId = Convert.ToInt32(custRow["id"])
        firstName = custRow["first_name"]?.ToString() ?? ""
        lastName = custRow["last_name"]?.ToString() ?? ""

        // BR-2: Decimal average, DBNull.Value for no scores
        avgCreditScore = if scoresByCustomer contains customerId:
            scoresByCustomer[customerId].Select(r => Convert.ToDecimal(r["score"])).Average()
        else:
            DBNull.Value

        // BR-3, BR-5: Loan totals with defaults
        (totalLoanBalance, loanCount) = loansByCustomer.GetValueOrDefault(customerId, (0m, 0))

        // BR-4, BR-6: Account totals with defaults
        (totalAccountBalance, accountCount) = accountsByCustomer.GetValueOrDefault(customerId, (0m, 0))

        // BR-7: as_of from customer row
        asOf = custRow["as_of"]

        outputRows.Add(new Row({
            "customer_id": customerId,
            "first_name": firstName,
            "last_name": lastName,
            "avg_credit_score": avgCreditScore,
            "total_loan_balance": totalLoanBalance,
            "total_account_balance": totalAccountBalance,
            "loan_count": loanCount,
            "account_count": accountCount,
            "as_of": asOf
        }))

    sharedState["output"] = new DataFrame(outputRows, outputColumns)
    return sharedState
```

### Key Design Decisions

1. **LINQ `ToLookup` for credit scores**: Uses `ToLookup` instead of `GroupBy().ToDictionary()` because we need to iterate the grouped values later to compute the average with `decimal` precision. `ToLookup` provides O(1) lookup and lazy enumeration.

2. **LINQ `GroupBy().ToDictionary()` for loans and accounts**: Pre-computes the sum and count in a single pass. This is cleaner than V1's manual dictionary accumulation [CustomerCreditSummaryBuilder.cs:44-70].

3. **`DBNull.Value` for missing credit scores**: Matches V1 exactly [CustomerCreditSummaryBuilder.cs:88]. When CsvFileWriter's `FormatField` encounters `DBNull.Value`, it calls `ToString()` which returns `""` -- same as a `null` value. This produces an empty field in the CSV.

4. **Decimal arithmetic throughout**: All monetary values and credit scores use `decimal` type. No `double` arithmetic anywhere. This is the same as V1 [CustomerCreditSummaryBuilder.cs:32,45,59] and avoids any W6-style floating-point issues.

5. **No rounding**: Consistent with V1 behavior and BRD OQ-2. No explicit rounding is applied to any computed values.

### Anti-Pattern Elimination Summary

| V1 Pattern | V2 Replacement |
|-----------|---------------|
| Manual `Dictionary<int, List<decimal>>` with `foreach` [CustomerCreditSummaryBuilder.cs:32-42] | LINQ `ToLookup` (AP6) |
| Manual `Dictionary<int, (decimal, int)>` with `foreach` [CustomerCreditSummaryBuilder.cs:45-56, 59-70] | LINQ `GroupBy().ToDictionary()` (AP6) |
| Sources `segments` table never used [customer_credit_summary.json:34-38] | Removed from config (AP1) |
| Sources 7 unused columns across 3 tables | Sources only 4 used columns (AP4) |
