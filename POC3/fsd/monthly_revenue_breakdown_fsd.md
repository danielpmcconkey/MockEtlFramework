# MonthlyRevenueBreakdown -- Functional Specification Document

## 1. Job Summary

The V2 job (`MonthlyRevenueBreakdownV2`) produces a daily revenue breakdown from two sources: overdraft fees (non-waived only) and credit transaction amounts (as an interest proxy). On October 31 (fiscal Q3 end), two additional quarterly summary rows are appended. The quarterly summary rows duplicate the daily values -- they are NOT accumulated quarter totals. Revenue values are rounded to 2 decimal places using banker's rounding (`MidpointRounding.ToEven`). Output is a CSV file with header, trailer, and Overwrite mode, written to `Output/double_secret_curated/monthly_revenue_breakdown.csv`.

---

## 2. V2 Module Chain

**Tier: 2 (Framework + Minimal External)** -- `DataSourcing -> Transformation (SQL) -> External (minimal) -> CsvFileWriter`

**Tier Justification:** The core aggregation logic (sum fees where not waived, sum credit amounts) is straightforward SQL. However, two specific operations cannot be cleanly expressed in SQLite's Transformation module:

1. **Banker's rounding (W5):** SQLite's `ROUND()` function uses half-away-from-zero rounding, not half-to-even (banker's rounding). The V1 code explicitly uses `Math.Round(value, 2, MidpointRounding.ToEven)` [MonthlyRevenueBreakdownBuilder.cs:58,64]. While it is unlikely that the actual data produces values landing exactly on a .5 midpoint after summation, the BRD documents this as a HIGH confidence output-affecting wrinkle. Reproducing it requires C#'s `Math.Round` with `MidpointRounding.ToEven`, which is not available in SQLite.

2. **`as_of` from shared state (BR-9):** The V1 code sets `as_of` to `__maxEffectiveDate` from shared state, not from data rows [MonthlyRevenueBreakdownBuilder.cs:18]. The Transformation module does not expose non-DataFrame shared state entries to SQL. While `as_of` on data rows equals `__maxEffectiveDate` for single-day runs, the defensive edge case (zero data rows for a given day) means the External module is the correct place to source this value.

The External module is minimal: it reads the pre-aggregated DataFrame from the Transformation step, applies banker's rounding, sets `as_of` from `__maxEffectiveDate`, and conditionally appends October 31 quarterly rows. All heavy-lifting (filtering, aggregation) is done in SQL.

### Module Chain

| Step | Module Type | Config Key | Purpose |
|------|-------------|------------|---------|
| 1 | DataSourcing | `overdraft_events` | Source overdraft event records from `datalake.overdraft_events` for the effective date range |
| 2 | DataSourcing | `transactions` | Source transaction records from `datalake.transactions` for the effective date range |
| 3 | Transformation | `revenue_aggregates` | Aggregate overdraft fees (non-waived) and credit transaction amounts into raw sums and counts |
| 4 | External | `MonthlyRevenueBreakdownV2Processor` | Apply banker's rounding, inject `as_of` from shared state, conditionally append Oct 31 quarterly rows |
| 5 | CsvFileWriter | -- | Write the `output` DataFrame to CSV with header and trailer |

---

## 3. DataSourcing Config

**DataSourcing -- overdraft_events:**
- Schema: `datalake`
- Table: `overdraft_events`
- Columns: `fee_amount`, `fee_waived`
- No `minEffectiveDate` / `maxEffectiveDate` -- injected at runtime via shared state
- Note: `overdraft_id`, `account_id`, `customer_id` are deliberately excluded (AP4 elimination -- none appear in the output or processing logic)
- The framework automatically appends `as_of` since it is not in the column list

**DataSourcing -- transactions:**
- Schema: `datalake`
- Table: `transactions`
- Columns: `txn_type`, `amount`
- No `minEffectiveDate` / `maxEffectiveDate` -- injected at runtime via shared state
- Note: `transaction_id`, `account_id` are deliberately excluded (AP4 elimination -- none appear in the output or processing logic)
- The framework automatically appends `as_of` since it is not in the column list

**Effective date handling:** Both DataSourcing modules rely on the framework's automatic effective date injection via shared state keys `__minEffectiveDate` and `__maxEffectiveDate`. The executor gap-fills one day at a time, so each run processes a single effective date. No date fields are hardcoded in the job config. Evidence: [monthly_revenue_breakdown.json:5-25] -- no `minEffectiveDate`/`maxEffectiveDate` fields in DataSourcing entries.

**Eliminated sources:**
- `datalake.customers` (id, first_name, last_name) -- sourced in V1 [monthly_revenue_breakdown.json:19-21] but never referenced by the External module [MonthlyRevenueBreakdownBuilder.cs:8-99]. Removed per AP1.

---

## 4. Transformation SQL

**Combined as a single Transformation SQL (using cross join of two subqueries):**

```sql
SELECT
    o.overdraft_revenue,
    o.overdraft_count,
    c.credit_revenue,
    c.credit_count
FROM (
    SELECT
        COALESCE(SUM(CASE WHEN fee_waived = 0 THEN fee_amount ELSE 0 END), 0) AS overdraft_revenue,
        COALESCE(SUM(CASE WHEN fee_waived = 0 THEN 1 ELSE 0 END), 0) AS overdraft_count
    FROM overdraft_events
) o
CROSS JOIN (
    SELECT
        COALESCE(SUM(CASE WHEN txn_type = 'Credit' THEN amount ELSE 0 END), 0) AS credit_revenue,
        COALESCE(SUM(CASE WHEN txn_type = 'Credit' THEN 1 ELSE 0 END), 0) AS credit_count
    FROM transactions
) c
```

**SQL Design Notes:**
- The `fee_waived` column is stored as boolean in PostgreSQL but arrives in SQLite as INTEGER (0/1) because the Transformation module's `ToSqliteValue` converts booleans: `bool b => b ? 1 : 0` [Transformation.cs:109]. The `CASE WHEN fee_waived = 0` check filters for non-waived events (`fee_waived = false = 0`). Evidence: [MonthlyRevenueBreakdownBuilder.cs:28] `if (!feeWaived)`.
- `COALESCE(SUM(...), 0)` ensures a zero result when no qualifying rows exist, matching V1 behavior where revenues initialize to `0m` and counts to `0` (BR-10). Evidence: [MonthlyRevenueBreakdownBuilder.cs:21-22,37-38].
- The SQL produces a single row with 4 scalar values (overdraft_revenue, overdraft_count, credit_revenue, credit_count). The External module reshapes this into the output format (2 or 4 rows depending on date).
- The CROSS JOIN between the two subqueries is safe because each subquery produces exactly one row (they are unconditional aggregations).
- No rounding is applied in SQL -- rounding is deferred to the External module to ensure banker's rounding (W5) via `Math.Round(value, 2, MidpointRounding.ToEven)`.
- The `customers` table is NOT referenced in SQL, and its DataSourcing entry is removed (AP1 elimination).

**Edge case -- empty DataFrames:** The framework's Transformation module skips table registration for DataFrames with zero rows [Transformation.cs:46: `if (!df.Rows.Any()) return;`]. If either `overdraft_events` or `transactions` is empty for a given effective date, the corresponding SQLite table will not exist and the SQL will fail with "no such table." The External module must handle this: if `revenue_aggregates` is not present in shared state (or the Transformation threw), the External module should default to 0 revenue and 0 count for both sources, matching V1's behavior. See Open Questions, OQ-1.

---

## 5. Writer Config

| Property | V1 Value | V2 Value | Match? |
|----------|----------|----------|--------|
| Writer type | CsvFileWriter | CsvFileWriter | YES |
| source | `output` | `output` | YES |
| outputFile | `Output/curated/monthly_revenue_breakdown.csv` | `Output/double_secret_curated/monthly_revenue_breakdown.csv` | Path change only (required) |
| includeHeader | true | true | YES |
| trailerFormat | `TRAILER\|{row_count}\|{date}` | `TRAILER\|{row_count}\|{date}` | YES |
| writeMode | Overwrite | Overwrite | YES |
| lineEnding | LF | LF | YES |

The writer configuration matches V1 exactly. Only the output path changes from `Output/curated/` to `Output/double_secret_curated/` as required by the V2 convention. The `{row_count}` token will reflect the correct number of data rows (2 on normal days, 4 on Oct 31), and `{date}` will be substituted with `__maxEffectiveDate` by the framework's CsvFileWriter [Architecture.md:241].

---

## 6. Wrinkle Replication

For each output-affecting wrinkle (W-code), how V2 replicates V1's behavior:

### W3c -- End-of-quarter boundary

**V1 behavior:** On October 31, V1 appends 2 additional QUARTERLY_TOTAL summary rows. The quarterly values are copies of the daily values -- not accumulated quarter totals. Evidence: [MonthlyRevenueBreakdownBuilder.cs:73] `if (maxDate.Month == 10 && maxDate.Day == 31)`, and [MonthlyRevenueBreakdownBuilder.cs:75-78] `decimal qOverdraftRevenue = overdraftRevenue;` (copies same-day values).

**V2 replication:** The External module checks `if (maxDate.Month == FiscalQ3EndMonth && maxDate.Day == FiscalQ3EndDay)` using named constants. On match, it appends rows with `revenue_source` values `"QUARTERLY_TOTAL_overdraft_fees"` and `"QUARTERLY_TOTAL_credit_interest_proxy"`, using the same rounded daily revenue and count values. Comment in code: `// W3c: Fiscal quarter boundary -- Q4 starts Nov 1, Oct 31 is last day of Q3. Quarterly values duplicate the daily values (not accumulated quarter totals) per V1 behavior [MonthlyRevenueBreakdownBuilder.cs:75-78].`

### W5 -- Banker's rounding

**V1 behavior:** Revenue totals are rounded with `Math.Round(value, 2, MidpointRounding.ToEven)`. Evidence: [MonthlyRevenueBreakdownBuilder.cs:58,64].

**V2 replication:** The External module applies the same `Math.Round(value, 2, MidpointRounding.ToEven)` to the raw sums produced by the SQL Transformation step. This is done in C# because SQLite's `ROUND()` uses half-away-from-zero rounding, not banker's rounding. Comment in code: `// W5: Banker's rounding (MidpointRounding.ToEven) -- correct for financial contexts, explicitly replicating V1 behavior [MonthlyRevenueBreakdownBuilder.cs:58,64].`

### W9 -- Wrong writeMode

**V1 behavior:** Uses `Overwrite` mode. On multi-day auto-advance runs, each day's output overwrites the previous day's file. Only the final effective date's data survives. Evidence: [monthly_revenue_breakdown.json:37] `"writeMode": "Overwrite"`.

**V2 replication:** V2 uses `"writeMode": "Overwrite"` in the CsvFileWriter config, matching V1 exactly. Comment in FSD: V1 uses Overwrite -- prior days' data is lost on each run.

### Not applicable

W1 (Sunday skip), W2 (weekend fallback), W3a (weekly boundary), W3b (monthly boundary), W4 (integer division), W6 (double epsilon), W7 (trailer inflated count), W8 (trailer stale date), W10 (absurd numParts), W12 (header every append) -- none of these apply to this job. The trailer uses framework `{row_count}` and `{date}` tokens which are computed correctly by the CsvFileWriter module.

---

## 7. Anti-Pattern Elimination

For each code-quality anti-pattern (AP-code), how V2 eliminates it:

### AP1 -- Dead-end sourcing (ELIMINATED)

**V1 problem:** V1 sources `datalake.customers` (id, first_name, last_name) via DataSourcing [monthly_revenue_breakdown.json:19-21], but the External module (`MonthlyRevenueBreakdownBuilder.cs`) never references the `customers` DataFrame [MonthlyRevenueBreakdownBuilder.cs:8-99 -- no mention of "customers"]. This is pure dead-end sourcing that wastes a database query.

**V2 elimination:** The `customers` DataSourcing entry is removed from the V2 config entirely. Only `overdraft_events` and `transactions` are sourced.

### AP3 -- Unnecessary External module (PARTIALLY ELIMINATED)

**V1 problem:** V1 uses a full External module (`MonthlyRevenueBreakdownBuilder`) for the entire business logic: filtering, aggregation, rounding, and output construction. The filtering and aggregation (SUM/COUNT with conditional logic) are textbook SQL operations.

**V2 elimination:** V2 moves the filtering and aggregation into a Transformation SQL step, reducing the V1 Tier 3 (full External) to a Tier 2 (framework + minimal External). The External module is retained only for banker's rounding (W5, not available in SQLite), `as_of` injection from shared state (BR-9, not accessible in SQL), and the Oct 31 conditional row construction (W3c). These are the genuine non-SQL operations.

### AP4 -- Unused columns (ELIMINATED)

**V1 problem:** V1 sources `overdraft_id`, `account_id`, `customer_id` from `overdraft_events` [monthly_revenue_breakdown.json:10] and `transaction_id`, `account_id` from `transactions` [monthly_revenue_breakdown.json:15-16], none of which are used in the output. V1 also sources `id`, `first_name`, `last_name` from `customers` (entirely unused table, see AP1).

**V2 elimination:** V2 sources only `fee_amount`, `fee_waived` from `overdraft_events` and `txn_type`, `amount` from `transactions`. Every sourced column is used in the SQL aggregation logic.

### AP6 -- Row-by-row iteration (ELIMINATED)

**V1 problem:** V1 iterates `overdraft_events` row-by-row with a `foreach` loop to sum fees and count non-waived events [MonthlyRevenueBreakdownBuilder.cs:25-33], and similarly iterates `transactions` row-by-row [MonthlyRevenueBreakdownBuilder.cs:41-49]. These are textbook SQL `SUM`/`COUNT` aggregations.

**V2 elimination:** V2 performs the aggregation in SQL using `SUM(CASE WHEN ...)` and `SUM(CASE WHEN ... THEN 1 ELSE 0 END)`. The External module only post-processes the already-aggregated single-row result -- it does not iterate over input data rows.

### AP9 -- Misleading names (DOCUMENTED)

**V1 problem:** The job name "MonthlyRevenueBreakdown" implies monthly aggregation, but the output is a daily breakdown (2 rows per day) with a quarterly boundary event on Oct 31 (4 rows that day).

**V2 handling:** Cannot rename -- output filenames must match V1 (`monthly_revenue_breakdown.csv`). Documented here: the job name is misleading.

### Not applicable

- AP2 (duplicated logic): No cross-job duplication identified within this job's scope.
- AP5 (asymmetric NULLs): NULL handling is consistent -- empty DataFrames default to 0 revenue and 0 count for both sources. No asymmetric treatment.
- AP7 (magic values): The October 31 boundary is documented with a fiscal quarter comment in V1 [MonthlyRevenueBreakdownBuilder.cs:72]. V2 uses named constants (`FiscalQ3EndMonth`, `FiscalQ3EndDay`) for additional clarity.
- AP8 (complex SQL / unused CTEs): V1 used no SQL. V2's SQL is straightforward with no unused CTEs.
- AP10 (over-sourcing dates): V1 relies on framework-injected effective dates, not manual date filtering. V2 does the same.

---

## 8. Proofmark Config

### Excluded Columns
**None.**

No columns are non-deterministic. All output values are derived deterministically from source data and the effective date. There are no timestamps, UUIDs, random values, or execution-time-dependent fields in the output schema. Evidence: BRD Non-Deterministic Fields section states "None identified."

### Fuzzy Columns
**None.**

The `total_revenue` column uses banker's rounding (`MidpointRounding.ToEven`) in both V1 and V2, applied via `Math.Round` in C# (not SQLite). Since both V1 and V2 use the same C# rounding function on the same aggregated decimal values, no epsilon divergence is expected. The aggregation in V2 is done via SQLite's `SUM()` on decimal-sourced values (which pass through REAL/double in SQLite), but the raw sum is converted back to `decimal` via `Convert.ToDecimal` in the External module before rounding, matching V1's accumulation path.

**Potential concern:** SQLite `SUM()` operates on REAL (double) values, while V1 accumulates with C# `decimal`. For simple sums of financial amounts (no division, no multiplication chains), the double-to-decimal conversion should produce identical values. If a precision difference manifests (due to double-precision floating-point accumulation differing from decimal accumulation), Proofmark will catch it and a fuzzy tolerance on `total_revenue` can be added with evidence at that time.

### Rationale
Starting from the default of zero exclusions and zero fuzzy overrides, per best practices. No evidence justifies any overrides at design time.

### YAML Config

```yaml
comparison_target: "monthly_revenue_breakdown"
reader: csv
threshold: 100.0
csv:
  header_rows: 1
  trailer_rows: 1
```

**Note:** `trailer_rows: 1` because V1 uses `writeMode: Overwrite` with a `trailerFormat`. In Overwrite mode, each run produces a file with exactly one trailer at the end. This follows the CONFIG_GUIDE.md Example 3 pattern.

---

## 9. Open Questions

**OQ-1: Empty DataFrame table registration in SQLite.**
The framework's Transformation module skips SQLite table creation for DataFrames with zero rows [Transformation.cs:46]. If either `overdraft_events` or `transactions` has no data for a given effective date, the SQL will fail because the referenced table does not exist. V1 handles this gracefully via null checks [MonthlyRevenueBreakdownBuilder.cs:23,39]. The V2 External module should handle the case where `revenue_aggregates` is missing from shared state (Transformation failure) by defaulting to 0 revenue and 0 count. Alternatively, the Transformation step could be wrapped in a try-catch, or the External module could bypass the Transformation entirely for this edge case. **Resolution:** The Developer should implement a fallback in the External module: if `revenue_aggregates` is not in shared state or has no rows, default all values to 0. This preserves V1's zero-row output behavior (BR-10). Confidence: HIGH that this edge case is unlikely during the 2024-10-01 to 2024-12-31 date range (both tables have data for all dates), but the code should be robust.

**OQ-2: Quarterly summary values duplicate daily values -- bug or intentional?**
The BRD notes (Open Question 1) that the QUARTERLY_TOTAL rows use the same day's values rather than accumulated quarter totals. This appears to be a bug or stub implementation in V1 [MonthlyRevenueBreakdownBuilder.cs:75-78]. However, since output equivalence requires reproducing V1's behavior, V2 replicates this exactly. If it is later determined to be a bug that should be fixed, the fix is isolated to the External module's Oct 31 logic.

**OQ-3: SQLite REAL vs C# decimal precision for SUM aggregation.**
V1 accumulates revenue using C# `decimal` arithmetic [MonthlyRevenueBreakdownBuilder.cs:30,46]. V2's SQL Transformation runs `SUM()` through SQLite REAL (IEEE 754 double). For simple addition of `numeric` values from PostgreSQL, the results should be identical, but double-precision accumulation could theoretically differ from decimal accumulation for certain value distributions. This is monitored via Proofmark. If a mismatch is detected in Phase D, the resolution is either: (a) add a fuzzy tolerance on `total_revenue` in the Proofmark config, or (b) move the aggregation into the External module (escalating to Tier 3). Confidence: HIGH that no mismatch will occur for this dataset.

---

## 10. Output Schema

| Column | Source Table | Source Column | Transformation | Evidence |
|--------|-------------|---------------|----------------|----------|
| revenue_source | Fixed strings | N/A | `"overdraft_fees"`, `"credit_interest_proxy"`, or `"QUARTERLY_TOTAL_"` prefixed variants on Oct 31 | [BRD:BR-4, BR-7, MonthlyRevenueBreakdownBuilder.cs:57,63,82,89] |
| total_revenue | overdraft_events / transactions | fee_amount / amount | SUM of qualifying values, banker's-rounded to 2 decimal places (MidpointRounding.ToEven) | [BRD:BR-1, BR-2, BR-3, MonthlyRevenueBreakdownBuilder.cs:58,64] |
| transaction_count | overdraft_events / transactions | (derived) | COUNT of non-waived overdraft events or Credit transactions | [BRD:BR-1, BR-2, MonthlyRevenueBreakdownBuilder.cs:59,65] |
| as_of | shared state | `__maxEffectiveDate` | DateOnly from shared state, NOT from data rows | [BRD:BR-9, MonthlyRevenueBreakdownBuilder.cs:18,60,66] |

**Column count:** 4
**Column order:** revenue_source, total_revenue, transaction_count, as_of (matches V1 output column definition at MonthlyRevenueBreakdownBuilder.cs:10-13)

**Row count:**
- Normal days: 2 rows (overdraft_fees, credit_interest_proxy)
- October 31: 4 rows (2 daily + 2 quarterly)

**Trailer row:** `TRAILER|{row_count}|{date}` where `{row_count}` is the count of all DataFrame rows (2 or 4) and `{date}` is `__maxEffectiveDate`. Handled by the framework's CsvFileWriter trailer token substitution.

---

## 11. V2 Job Config

```json
{
  "jobName": "MonthlyRevenueBreakdownV2",
  "firstEffectiveDate": "2024-10-01",
  "modules": [
    {
      "type": "DataSourcing",
      "resultName": "overdraft_events",
      "schema": "datalake",
      "table": "overdraft_events",
      "columns": ["fee_amount", "fee_waived"]
    },
    {
      "type": "DataSourcing",
      "resultName": "transactions",
      "schema": "datalake",
      "table": "transactions",
      "columns": ["txn_type", "amount"]
    },
    {
      "type": "Transformation",
      "resultName": "revenue_aggregates",
      "sql": "SELECT o.overdraft_revenue, o.overdraft_count, c.credit_revenue, c.credit_count FROM (SELECT COALESCE(SUM(CASE WHEN fee_waived = 0 THEN fee_amount ELSE 0 END), 0) AS overdraft_revenue, COALESCE(SUM(CASE WHEN fee_waived = 0 THEN 1 ELSE 0 END), 0) AS overdraft_count FROM overdraft_events) o CROSS JOIN (SELECT COALESCE(SUM(CASE WHEN txn_type = 'Credit' THEN amount ELSE 0 END), 0) AS credit_revenue, COALESCE(SUM(CASE WHEN txn_type = 'Credit' THEN 1 ELSE 0 END), 0) AS credit_count FROM transactions) c"
    },
    {
      "type": "External",
      "assemblyPath": "/workspace/MockEtlFramework/ExternalModules/bin/Debug/net8.0/ExternalModules.dll",
      "typeName": "ExternalModules.MonthlyRevenueBreakdownV2Processor"
    },
    {
      "type": "CsvFileWriter",
      "source": "output",
      "outputFile": "Output/double_secret_curated/monthly_revenue_breakdown.csv",
      "includeHeader": true,
      "trailerFormat": "TRAILER|{row_count}|{date}",
      "writeMode": "Overwrite",
      "lineEnding": "LF"
    }
  ]
}
```

---

## 12. External Module Design

### Module: `MonthlyRevenueBreakdownV2Processor`
**File:** `ExternalModules/MonthlyRevenueBreakdownV2Processor.cs`

### Responsibility
The External module is a **Tier 2 scalpel** -- it handles ONLY the operations that cannot be expressed in SQLite:
1. Read the pre-aggregated `revenue_aggregates` DataFrame (1 row, 4 columns) from the Transformation step
2. Apply banker's rounding (`MidpointRounding.ToEven`) to the revenue totals (W5)
3. Retrieve `__maxEffectiveDate` from shared state and use it as the `as_of` value (BR-9)
4. Construct the 2 daily output rows (overdraft_fees, credit_interest_proxy) (BR-4)
5. On October 31, append 2 quarterly summary rows with `QUARTERLY_TOTAL_` prefixed names, using the same daily values (BR-5, BR-6, BR-7, W3c)
6. Store the result as `output` DataFrame in shared state for the CsvFileWriter

### Input
- `revenue_aggregates` DataFrame from shared state (produced by Transformation step)
  - Columns: `overdraft_revenue` (double from SQLite REAL), `overdraft_count` (long from SQLite INTEGER), `credit_revenue` (double from SQLite REAL), `credit_count` (long from SQLite INTEGER)
  - Rows: exactly 1
- `__maxEffectiveDate` (DateOnly) from shared state

### Output
- `output` DataFrame in shared state
  - Columns: `revenue_source`, `total_revenue`, `transaction_count`, `as_of`
  - Rows: 2 (normal days) or 4 (October 31)

### Design Notes

**Named constants for clarity (AP7 compliance):**
```csharp
// Fiscal quarter boundary: Q4 starts Nov 1, so Oct 31 is the last day of fiscal Q3
private const int FiscalQ3EndMonth = 10;
private const int FiscalQ3EndDay = 31;
```

**Banker's rounding (W5):**
```csharp
// W5: Banker's rounding (MidpointRounding.ToEven) -- correct for financial contexts,
// explicitly replicating V1 behavior [MonthlyRevenueBreakdownBuilder.cs:58,64]
var roundedOverdraftRevenue = Math.Round(overdraftRevenue, 2, MidpointRounding.ToEven);
var roundedCreditRevenue = Math.Round(creditRevenue, 2, MidpointRounding.ToEven);
```

**October 31 quarterly summary (W3c):**
```csharp
// W3c: Fiscal quarter boundary -- Oct 31 is last day of Q3.
// Quarterly values duplicate the daily values (not accumulated quarter totals).
// V1 evidence: [MonthlyRevenueBreakdownBuilder.cs:75-78] copies same-day values.
if (maxDate.Month == FiscalQ3EndMonth && maxDate.Day == FiscalQ3EndDay)
{
    // Append QUARTERLY_TOTAL_overdraft_fees and QUARTERLY_TOTAL_credit_interest_proxy rows
    // with the same revenue/count values as the daily rows
}
```

**Set-based operations (AP6 compliance):**
The External module does NOT iterate over input data rows. It reads the single pre-aggregated row from the Transformation step and constructs output rows directly. All row-by-row iteration was moved to SQL in the Transformation step.

**No dead-end data (AP1/AP4 compliance):**
The External module reads every column from `revenue_aggregates` (all 4 are used). It does not access any other DataFrames from shared state (the unused `customers` source has been removed from the pipeline).

**Empty DataFrame fallback (OQ-1):**
```csharp
// If Transformation failed due to empty source tables (no SQLite table created),
// revenue_aggregates may not be in shared state. Default to 0/0 per V1 behavior (BR-10).
var aggregates = sharedState.ContainsKey("revenue_aggregates")
    ? sharedState["revenue_aggregates"] as DataFrame
    : null;
```

### Pseudocode

```
Execute(sharedState):
    aggregates = sharedState.get("revenue_aggregates") as DataFrame (may be null)
    maxDate = (DateOnly)sharedState["__maxEffectiveDate"]

    if aggregates != null and aggregates.Rows.Count > 0:
        row = aggregates.Rows[0]
        overdraftRevenue = Convert.ToDecimal(row["overdraft_revenue"])
        overdraftCount = Convert.ToInt32(row["overdraft_count"])
        creditRevenue = Convert.ToDecimal(row["credit_revenue"])
        creditCount = Convert.ToInt32(row["credit_count"])
    else:
        // BR-10: Default to 0 when no data
        overdraftRevenue = 0m
        overdraftCount = 0
        creditRevenue = 0m
        creditCount = 0

    // W5: Apply banker's rounding
    roundedOverdraft = Math.Round(overdraftRevenue, 2, MidpointRounding.ToEven)
    roundedCredit = Math.Round(creditRevenue, 2, MidpointRounding.ToEven)

    // Build daily rows (always present, BR-4)
    outputRows = [
        { revenue_source: "overdraft_fees", total_revenue: roundedOverdraft, transaction_count: overdraftCount, as_of: maxDate },
        { revenue_source: "credit_interest_proxy", total_revenue: roundedCredit, transaction_count: creditCount, as_of: maxDate }
    ]

    // W3c: Fiscal Q3 end boundary -- append quarterly summary rows
    if (maxDate.Month == FiscalQ3EndMonth && maxDate.Day == FiscalQ3EndDay):
        outputRows.Add({ revenue_source: "QUARTERLY_TOTAL_overdraft_fees", total_revenue: roundedOverdraft, transaction_count: overdraftCount, as_of: maxDate })
        outputRows.Add({ revenue_source: "QUARTERLY_TOTAL_credit_interest_proxy", total_revenue: roundedCredit, transaction_count: creditCount, as_of: maxDate })

    sharedState["output"] = new DataFrame(outputRows, ["revenue_source", "total_revenue", "transaction_count", "as_of"])
    return sharedState
```

---

## 13. Traceability Matrix

| FSD Decision | BRD Requirement | Evidence |
|-------------|-----------------|----------|
| Tier 2 module chain (External for rounding + date logic) | BR-3 (W5 banker's rounding), BR-9 (as_of from shared state), BR-4/BR-5 (Oct 31 conditional rows) | SQLite `ROUND()` uses half-away-from-zero, not half-to-even; `__maxEffectiveDate` is not a DataFrame and thus not accessible in SQL |
| Remove `customers` DataSourcing (dead-end) | BR-8, AP1 | [MonthlyRevenueBreakdownBuilder.cs:8-99] -- no reference to `customers` DataFrame; [monthly_revenue_breakdown.json:19-21] -- `customers` sourced but unused |
| Source only `fee_amount`, `fee_waived` from overdraft_events | AP4 | [MonthlyRevenueBreakdownBuilder.cs:27,30] -- only `fee_waived` and `fee_amount` accessed from overdraft rows |
| Source only `txn_type`, `amount` from transactions | AP4 | [MonthlyRevenueBreakdownBuilder.cs:43,46] -- only `txn_type` and `amount` accessed from transaction rows |
| SQL aggregation instead of foreach loops | AP6 | [MonthlyRevenueBreakdownBuilder.cs:25-33,41-49] -- row-by-row iteration replaced with SQL SUM/COUNT |
| Filter overdraft events where fee_waived = false | BR-1 | [MonthlyRevenueBreakdownBuilder.cs:28] `if (!feeWaived)` |
| Filter transactions where txn_type = 'Credit' | BR-2 | [MonthlyRevenueBreakdownBuilder.cs:44] `if (txnType == "Credit")` |
| Banker's rounding via `MidpointRounding.ToEven` | BR-3, W5 | [MonthlyRevenueBreakdownBuilder.cs:58,64] `Math.Round(value, 2, MidpointRounding.ToEven)` |
| 2 daily rows: overdraft_fees, credit_interest_proxy | BR-4 | [MonthlyRevenueBreakdownBuilder.cs:55-68] -- always 2 output rows |
| Oct 31 quarterly rows with QUARTERLY_TOTAL_ prefix | BR-4, BR-5, BR-7, W3c | [MonthlyRevenueBreakdownBuilder.cs:73] `if (maxDate.Month == 10 && maxDate.Day == 31)` |
| Quarterly values = daily values (not accumulated) | BR-6 | [MonthlyRevenueBreakdownBuilder.cs:75-78] `decimal qOverdraftRevenue = overdraftRevenue;` |
| as_of from `__maxEffectiveDate` (not from data rows) | BR-9 | [MonthlyRevenueBreakdownBuilder.cs:18] `var maxDate = (DateOnly)sharedState["__maxEffectiveDate"];` |
| Empty data defaults to 0 revenue, 0 count | BR-10 | [MonthlyRevenueBreakdownBuilder.cs:21-22,37-38] -- `decimal overdraftRevenue = 0m; int overdraftCount = 0;` with null check |
| writeMode: Overwrite | W9 | [monthly_revenue_breakdown.json:37] `"writeMode": "Overwrite"` -- prior days' data lost on each run |
| trailerFormat: TRAILER\|{row_count}\|{date} | BRD: Writer Configuration | [monthly_revenue_breakdown.json:36] |
| includeHeader: true | BRD: Writer Configuration | [monthly_revenue_breakdown.json:35] |
| lineEnding: LF | BRD: Writer Configuration | [monthly_revenue_breakdown.json:38] |
| firstEffectiveDate: 2024-10-01 | BRD: Traceability Matrix | [monthly_revenue_breakdown.json:3] |
| No Proofmark exclusions/fuzzy | BRD: Non-Deterministic Fields = None | All fields are deterministic |
| Misleading job name documented | AP9 | Name says "monthly" but output is daily with quarterly boundary -- cannot rename per V2 rules |
| Named constants for fiscal quarter boundary | AP7 (preventive) | V1 uses inline `10` and `31` [MonthlyRevenueBreakdownBuilder.cs:73]; V2 uses `FiscalQ3EndMonth` and `FiscalQ3EndDay` |
