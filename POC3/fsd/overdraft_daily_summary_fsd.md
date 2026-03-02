# OverdraftDailySummary -- Functional Specification Document

## 1. Job Summary

The `OverdraftDailySummaryV2` job produces a daily summary of overdraft activity by grouping `datalake.overdraft_events` by `as_of` date and computing event count, total overdraft amount, and total fees per group. When `__maxEffectiveDate` falls on a Sunday, an additional `WEEKLY_TOTAL` summary row is appended that sums all groups across the entire effective date range (not just the current calendar week). Output is a CSV file with a header row and a `TRAILER|{row_count}|{date}` trailer line. The job uses `Overwrite` mode, so on multi-day auto-advance runs only the final effective date's output survives. This is a Tier 1 rewrite: the V1 External module is eliminated entirely in favor of framework-native `DataSourcing -> Transformation (SQL) -> CsvFileWriter`.

---

## 2. V2 Module Chain

**Tier: 1 (Framework Only)** -- `DataSourcing -> Transformation (SQL) -> CsvFileWriter`

| Step | Module | resultName / Config | Purpose |
|------|--------|---------------------|---------|
| 1 | DataSourcing | `overdraft_events` | Source overdraft event records from `datalake.overdraft_events` for the effective date range |
| 2 | Transformation | `output` | Compute daily summaries via GROUP BY; conditionally append WEEKLY_TOTAL row on Sundays via UNION ALL |
| 3 | CsvFileWriter | -- | Write `output` DataFrame to CSV with header and trailer |

**Tier Justification:**

All V1 business logic is expressible in SQL:
- Daily grouping with `COUNT(*)` and `SUM()` aggregates is a standard `GROUP BY`. [BRD:BR-1]
- The conditional `WEEKLY_TOTAL` row is implementable as a `UNION ALL` with a `WHERE` clause gated on `strftime('%w', MAX(as_of)) = '0'` (SQLite's Sunday check). [BRD:BR-4, W3a]
- No procedural logic, no external data access patterns, no operations requiring C#.
- The V1 External module (`OverdraftDailySummaryProcessor`) does nothing that SQL cannot do. Its `foreach` loop manually builds a dictionary of groups -- this is literally what `GROUP BY` exists for. [OverdraftDailySummaryProcessor.cs:32-45]

Eliminating the External module addresses AP3 (unnecessary External) and AP6 (row-by-row iteration).

---

## 3. DataSourcing Config

### Tables Sourced

| Table | resultName | Columns | Effective Date Handling |
|-------|-----------|---------|------------------------|
| `datalake.overdraft_events` | `overdraft_events` | `overdraft_amount`, `fee_amount` | Runtime injection via `__minEffectiveDate` / `__maxEffectiveDate` shared state keys. No hardcoded dates. `as_of` column appended automatically by DataSourcing module. |

### Columns Sourced vs V1

V1 sources 7 columns: `overdraft_id`, `account_id`, `customer_id`, `overdraft_amount`, `fee_amount`, `fee_waived`, `event_timestamp`. Of these, only `overdraft_amount` and `fee_amount` are ever read by the External processor [OverdraftDailySummaryProcessor.cs:36-38]. The remaining 5 columns are dead weight (AP4). The `as_of` column is appended automatically by the DataSourcing module and does not need to be listed.

V2 sources only **2 columns**: `overdraft_amount`, `fee_amount`. This eliminates AP4.

### Dead-End Table Elimination

V1 also sources `datalake.transactions` (columns: `transaction_id`, `account_id`, `txn_timestamp`, `txn_type`, `amount`, `description`) but the External processor never accesses `sharedState["transactions"]` [OverdraftDailySummaryProcessor.cs:23, BRD:BR-6]. This entire DataSourcing entry is removed in V2, eliminating AP1.

---

## 4. Transformation SQL

```sql
-- Daily summaries: group overdraft events by as_of date
-- BR-1: Group by as_of; BR-2: All fees included (no fee_waived filter)
-- BR-3: event_date = as_of for daily rows
SELECT
    as_of AS event_date,
    COUNT(*) AS overdraft_count,
    SUM(overdraft_amount) AS total_overdraft_amount,
    SUM(fee_amount) AS total_fees,
    as_of
FROM overdraft_events
GROUP BY as_of

UNION ALL

-- W3a: End-of-week boundary — append WEEKLY_TOTAL row on Sundays
-- EC-1: WEEKLY_TOTAL sums ALL groups in the effective range, not just the current week
-- SQLite strftime('%w', ...) returns '0' for Sunday
SELECT
    'WEEKLY_TOTAL' AS event_date,
    COUNT(*) AS overdraft_count,
    SUM(overdraft_amount) AS total_overdraft_amount,
    SUM(fee_amount) AS total_fees,
    MAX(as_of) AS as_of
FROM overdraft_events
WHERE (SELECT strftime('%w', MAX(as_of)) FROM overdraft_events) = '0'
```

### SQL Design Notes

1. **Daily rows (first SELECT):** Groups `overdraft_events` by `as_of` and computes `COUNT(*)`, `SUM(overdraft_amount)`, and `SUM(fee_amount)`. The `as_of` column appears twice in the output: once aliased as `event_date` and once as itself, replicating V1's behavior where both carry the same value for daily rows [BRD:BR-3, OverdraftDailySummaryProcessor.cs:51-55].

2. **WEEKLY_TOTAL row (second SELECT via UNION ALL):** Aggregates ALL rows in the table (no GROUP BY), replicating V1's `groups.Values.Sum(...)` behavior that sums across the entire effective range [BRD:BR-4, EC-1]. The `WHERE` clause uses a correlated subquery `(SELECT strftime('%w', MAX(as_of)) FROM overdraft_events) = '0'` to gate this row's inclusion on whether the maximum `as_of` date is a Sunday. When the max date is not a Sunday, the `WHERE` evaluates to false and the UNION ALL contributes zero rows.

3. **WEEKLY_TOTAL as_of value:** `MAX(as_of)` produces the max date as a string (since `as_of` is stored as TEXT in SQLite after DataSourcing's `DateOnly.ToString("yyyy-MM-dd")` conversion), matching V1's `maxDate.ToString("yyyy-MM-dd")` [BRD:BR-5, OverdraftDailySummaryProcessor.cs:73].

4. **Fee inclusion:** `SUM(fee_amount)` includes all fees regardless of `fee_waived` status. The `fee_waived` column is not sourced or referenced [BRD:BR-2, EC-6].

5. **Row ordering:** No explicit `ORDER BY` is applied. V1's output order is determined by dictionary insertion order (which follows DataSourcing's `as_of` ordering). SQLite's `GROUP BY as_of` with data pre-sorted by `as_of` produces the same ascending date order. The WEEKLY_TOTAL row from the UNION ALL naturally appears last.

6. **Decimal vs REAL:** V1 uses C# `decimal` for accumulation [BRD:BR-7]. V2's SQL runs through SQLite REAL (double). For simple addition of financial amounts with no division or multiplication chains, REAL representation should produce identical string output. If precision differences arise during Phase D comparison, a fuzzy tolerance will be added with evidence.

---

## 5. Writer Config

| Property | V1 Value | V2 Value | Match? |
|----------|----------|----------|--------|
| Writer type | CsvFileWriter | CsvFileWriter | YES |
| source | `output` | `output` | YES |
| outputFile | `Output/curated/overdraft_daily_summary.csv` | `Output/double_secret_curated/overdraft_daily_summary.csv` | Path change only (V2 convention) |
| includeHeader | `true` | `true` | YES |
| trailerFormat | `TRAILER\|{row_count}\|{date}` | `TRAILER\|{row_count}\|{date}` | YES |
| writeMode | `Overwrite` | `Overwrite` | YES |
| lineEnding | `LF` | `LF` | YES |
| numParts | N/A (CSV) | N/A (CSV) | N/A |

All writer configuration parameters match V1 exactly. Only the output path changes from `Output/curated/` to `Output/double_secret_curated/` as required by the V2 output convention.

**Trailer semantics:** `{row_count}` is substituted with the count of all DataFrame rows (including the WEEKLY_TOTAL row if present). `{date}` is substituted with `__maxEffectiveDate` from shared state [Architecture.md:241, BRD:BR-8, EC-3].

---

## 6. Wrinkle Replication

### W3a -- End-of-week boundary

**Applies: YES**

V1 appends a `WEEKLY_TOTAL` summary row when `__maxEffectiveDate` is a Sunday [OverdraftDailySummaryProcessor.cs:61, BRD:BR-4]. The row sums ALL daily groups in the effective date range, not just the current calendar week [OverdraftDailySummaryProcessor.cs:62-64, BRD:EC-1].

**V2 replication:** The `UNION ALL` in the Transformation SQL produces the WEEKLY_TOTAL row. The `WHERE (SELECT strftime('%w', MAX(as_of)) FROM overdraft_events) = '0'` clause gates inclusion on Sunday. The second SELECT has no `GROUP BY`, so it aggregates all rows in the table, replicating V1's "sum all groups" behavior. The code is clean and self-documenting -- the SQL comment explains the V1 behavior and the `strftime` condition is explicit.

### W9 -- Wrong writeMode

**Applies: YES**

V1 uses `Overwrite` mode [overdraft_daily_summary.json:30], meaning each execution replaces the entire CSV file. On multi-day auto-advance runs, only the final effective date's output survives [BRD:EC-5].

**V2 replication:** V2 uses `"writeMode": "Overwrite"` identically. This is documented as intentional V1 behavior replication.

### Non-applicable wrinkles

| W-Code | Reason Not Applicable |
|--------|----------------------|
| W1 (Sunday skip) | Job does not skip Sundays; it adds a WEEKLY_TOTAL row on Sundays |
| W2 (Weekend fallback) | No weekend date fallback logic |
| W3b (End-of-month boundary) | No MONTHLY_TOTAL row |
| W3c (End-of-quarter boundary) | No QUARTERLY_TOTAL row |
| W4 (Integer division) | No percentage calculations |
| W5 (Banker's rounding) | No rounding operations |
| W6 (Double epsilon) | V1 uses decimal, not double [BRD:BR-7] |
| W7 (Trailer inflated count) | Trailer count matches DataFrame row count (standard framework behavior) |
| W8 (Trailer stale date) | Trailer date uses `{date}` token (framework substitutes `__maxEffectiveDate`) |
| W10 (Absurd numParts) | CSV output, not Parquet |
| W12 (Header every append) | Uses Overwrite mode, not Append |

---

## 7. Anti-Pattern Elimination

### AP1 -- Dead-end sourcing

**Applies: YES**

V1 sources `datalake.transactions` but the External processor never reads `sharedState["transactions"]` [OverdraftDailySummaryProcessor.cs:23, BRD:BR-6].

**V2 elimination:** The `transactions` DataSourcing entry is completely removed from the V2 job config. Only `overdraft_events` is sourced.

### AP3 -- Unnecessary External module

**Applies: YES**

V1 uses `OverdraftDailySummaryProcessor` (a C# External module) where the entire business logic is a `GROUP BY` aggregation with a conditional union. The processor iterates rows with `foreach`, builds a dictionary of groups, then iterates the dictionary to build output rows [OverdraftDailySummaryProcessor.cs:32-57]. This is textbook SQL.

**V2 elimination:** The External module is replaced entirely by a Transformation (SQL) module. The SQL `GROUP BY` + `UNION ALL` produces identical output using framework-native capabilities.

### AP4 -- Unused columns

**Applies: YES**

V1 sources 7 columns from `overdraft_events`: `overdraft_id`, `account_id`, `customer_id`, `overdraft_amount`, `fee_amount`, `fee_waived`, `event_timestamp`. Only `overdraft_amount`, `fee_amount`, and the auto-appended `as_of` are read [OverdraftDailySummaryProcessor.cs:36-38]. The other 5 columns (`overdraft_id`, `account_id`, `customer_id`, `fee_waived`, `event_timestamp`) are unused [BRD:EC-6].

**V2 elimination:** V2 sources only `overdraft_amount` and `fee_amount`. The `as_of` column is auto-appended by the DataSourcing module and does not need to be listed.

### AP6 -- Row-by-row iteration

**Applies: YES**

V1 uses `foreach (var row in overdraftEvents.Rows)` to manually group and accumulate values into `Dictionary<string, (int count, decimal totalAmount, decimal totalFees)>` [OverdraftDailySummaryProcessor.cs:34-45].

**V2 elimination:** Replaced by SQL `GROUP BY as_of` with `COUNT(*)` and `SUM()` aggregates. Set-based operation replaces procedural iteration.

### Non-applicable anti-patterns

| AP-Code | Reason Not Applicable |
|---------|----------------------|
| AP2 (Duplicated logic) | No cross-job duplication identified |
| AP5 (Asymmetric NULLs) | No NULL-dependent branching in the aggregation logic |
| AP7 (Magic values) | The only literal string is `"WEEKLY_TOTAL"`, a self-documenting label |
| AP8 (Complex SQL / unused CTEs) | V1 has no SQL (External module). V2 SQL is minimal with no unused CTEs. |
| AP9 (Misleading names) | Job name accurately describes what the job produces |
| AP10 (Over-sourcing dates) | V1 relies on framework-injected effective dates; V2 does the same |

---

## 8. Proofmark Config

```yaml
comparison_target: "overdraft_daily_summary"
reader: csv
threshold: 100.0
csv:
  header_rows: 1
  trailer_rows: 1
```

### Config Rationale

- **reader: csv** -- V1 and V2 both use CsvFileWriter.
- **header_rows: 1** -- `includeHeader: true` in both V1 and V2 configs.
- **trailer_rows: 1** -- `trailerFormat` is present and `writeMode` is `Overwrite`, so there is exactly one trailer at the end of the file.
- **threshold: 100.0** -- All output is deterministic. No tolerance for mismatches.
- **No excluded columns** -- BRD identifies no non-deterministic fields. All output values are derived deterministically from source data filtered by the effective date range [BRD: Non-Deterministic Fields].
- **No fuzzy columns** -- All monetary columns (`total_overdraft_amount`, `total_fees`) are simple `SUM` aggregates. V1 uses `decimal` [BRD:BR-7]; V2 uses SQLite REAL. For simple addition with no division/multiplication chains, the string representation should be identical. If precision differences surface during Phase D, a fuzzy tolerance will be added with evidence at that time.

---

## 9. Open Questions

1. **SQLite REAL vs C# decimal for monetary sums** -- V1 accumulates `overdraft_amount` and `fee_amount` using C# `decimal` [BRD:BR-7]. V2's Transformation module runs through SQLite, which uses REAL (IEEE 754 double) for numeric operations. For simple addition of source values, this should produce identical output strings. However, if the source data contains values that expose double-precision rounding differences (e.g., many small values that accumulate epsilon drift), the outputs could diverge. This will be validated by Proofmark in Phase D. If a mismatch occurs, the resolution is to add a fuzzy tolerance to the Proofmark config for the affected columns, or to escalate to Tier 2 with a minimal External module that performs the accumulation in `decimal`.

2. **Row ordering guarantee** -- V1's output order depends on dictionary insertion order, which mirrors DataSourcing's `as_of` sort order. V2 relies on SQLite's `GROUP BY as_of` producing rows in ascending `as_of` order without an explicit `ORDER BY`. SQLite's documentation does not guarantee `GROUP BY` output order. If row ordering differs, an `ORDER BY as_of` clause should be added to the first SELECT (or wrapped in an outer query). This will be validated by Proofmark in Phase D.

3. **Weekly total scope** (carried from BRD) -- The WEEKLY_TOTAL row sums ALL groups across the entire effective date range, not just the current week's data [BRD:EC-1]. If the effective range spans multiple weeks, the "weekly" total is actually a multi-week total. V2 replicates this behavior exactly as V1 does. Whether this is intentional business logic or a bug is outside scope -- output equivalence is the goal.

---

## V2 Job Config (Reference)

```json
{
  "jobName": "OverdraftDailySummaryV2",
  "firstEffectiveDate": "2024-10-01",
  "modules": [
    {
      "type": "DataSourcing",
      "resultName": "overdraft_events",
      "schema": "datalake",
      "table": "overdraft_events",
      "columns": ["overdraft_amount", "fee_amount"]
    },
    {
      "type": "Transformation",
      "resultName": "output",
      "sql": "SELECT as_of AS event_date, COUNT(*) AS overdraft_count, SUM(overdraft_amount) AS total_overdraft_amount, SUM(fee_amount) AS total_fees, as_of FROM overdraft_events GROUP BY as_of UNION ALL SELECT 'WEEKLY_TOTAL' AS event_date, COUNT(*) AS overdraft_count, SUM(overdraft_amount) AS total_overdraft_amount, SUM(fee_amount) AS total_fees, MAX(as_of) AS as_of FROM overdraft_events WHERE (SELECT strftime('%w', MAX(as_of)) FROM overdraft_events) = '0'"
    },
    {
      "type": "CsvFileWriter",
      "source": "output",
      "outputFile": "Output/double_secret_curated/overdraft_daily_summary.csv",
      "includeHeader": true,
      "trailerFormat": "TRAILER|{row_count}|{date}",
      "writeMode": "Overwrite",
      "lineEnding": "LF"
    }
  ]
}
```

---

## Traceability Matrix

| FSD Decision | BRD Requirement | W/AP Code | Evidence |
|-------------|-----------------|-----------|----------|
| Tier 1 module chain (no External) | BR-6, AP3 | AP3 | V1 logic is GROUP BY + conditional UNION; [OverdraftDailySummaryProcessor.cs:32-75] |
| Remove `transactions` DataSourcing | BR-6 | AP1 | [OverdraftDailySummaryProcessor.cs:23] `// AP1: transactions sourced but never used` |
| Source only `overdraft_amount`, `fee_amount` | -- | AP4 | [OverdraftDailySummaryProcessor.cs:36-38] Only `as_of`, `overdraft_amount`, `fee_amount` accessed |
| SQL GROUP BY replaces foreach loop | BR-1 | AP6 | [OverdraftDailySummaryProcessor.cs:34-45] foreach over rows building dictionary |
| GROUP BY as_of for daily summaries | BR-1 | -- | [OverdraftDailySummaryProcessor.cs:32-45] |
| SUM(fee_amount) with no fee_waived filter | BR-2 | -- | [OverdraftDailySummaryProcessor.cs:38] No `fee_waived` check |
| event_date = as_of for daily rows | BR-3 | -- | [OverdraftDailySummaryProcessor.cs:51-55] |
| WEEKLY_TOTAL on Sundays via UNION ALL | BR-4, BR-5, EC-1 | W3a | [OverdraftDailySummaryProcessor.cs:61-75] |
| WEEKLY_TOTAL sums ALL groups | EC-1 | W3a | [OverdraftDailySummaryProcessor.cs:62-64] `groups.Values.Sum(...)` |
| writeMode: Overwrite | EC-5 | W9 | [overdraft_daily_summary.json:30] |
| Trailer: `TRAILER\|{row_count}\|{date}` | BR-8, EC-3 | -- | [overdraft_daily_summary.json:29] |
| Runtime date injection (no hardcoded dates) | BR-9 | -- | [overdraft_daily_summary.json:4-19] |
| lineEnding: LF | -- | -- | [overdraft_daily_summary.json:31] |
| No Proofmark exclusions or fuzzy | Non-Deterministic Fields: None | -- | BRD: "All output is deterministic" |
