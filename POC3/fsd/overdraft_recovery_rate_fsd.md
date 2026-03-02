# OverdraftRecoveryRate -- Functional Specification Document

## 1. Job Summary

This job calculates the overdraft fee recovery rate -- the proportion of overdraft events where fees were actually charged rather than waived. It aggregates all overdraft events for a given effective date into a single summary row containing total events, charged count, waived count, recovery rate, and the as-of date. Due to a known integer division bug (W4), the recovery rate is always 0. Output is a CSV file with a header and a trailer line, written in Overwrite mode.

## 2. V2 Module Chain

**Tier: 1 (Framework Only)**

```
DataSourcing -> Transformation (SQL) -> CsvFileWriter
```

**Justification:** The V1 External module (`OverdraftRecoveryRateProcessor.cs`) performs simple counting and integer division that maps directly to SQL aggregation functions. SQLite's integer division naturally replicates W4 (integer truncation to 0). There is no procedural logic, no cross-date-range querying, no snapshot fallback, and no operation that requires C# -- everything is expressible in a single SQL query. The V1 External module is an unnecessary AP3 anti-pattern.

**Empty-DataFrame guard:** The Transformation module does not register empty DataFrames as SQLite tables (`Transformation.cs:46`: `if (!df.Rows.Any()) return;`). On effective dates with no overdraft events, the SQL would fail because the `overdraft_events` table does not exist in the SQLite context. To handle this, the SQL uses `CREATE TABLE IF NOT EXISTS` as a preamble to ensure the table exists (as an empty table) even when DataSourcing returns zero rows. See Section 4 for the full SQL. See Open Questions (OQ-1) for behavioral implications.

## 3. DataSourcing Config

**Source table:** `datalake.overdraft_events`

| Column | Type | Used By | Justification |
|--------|------|---------|---------------|
| fee_waived | boolean | Transformation SQL | Determines charged vs. waived classification (BR-1) |

**Columns removed vs. V1 (AP4 elimination):**
- `overdraft_id` -- never referenced in V1 processing logic [OverdraftRecoveryRateProcessor.cs:33-41]
- `account_id` -- never referenced in V1 processing logic
- `customer_id` -- never referenced in V1 processing logic
- `overdraft_amount` -- never referenced in V1 processing logic
- `fee_amount` -- never referenced in V1 processing logic
- `event_timestamp` -- never referenced in V1 processing logic

**Effective date handling:** No hardcoded dates. The framework injects `__minEffectiveDate` and `__maxEffectiveDate` into shared state at runtime (BR-7). DataSourcing uses these to filter `WHERE as_of >= @minDate AND as_of <= @maxDate`. The `as_of` column is automatically appended by DataSourcing since it is not in the explicit column list.

**V2 DataSourcing JSON:**
```json
{
  "type": "DataSourcing",
  "resultName": "overdraft_events",
  "schema": "datalake",
  "table": "overdraft_events",
  "columns": ["fee_waived"]
}
```

## 4. Transformation SQL

```sql
CREATE TABLE IF NOT EXISTS "overdraft_events" ("fee_waived" INTEGER, "as_of" TEXT);
SELECT
  COUNT(*) AS total_events,
  SUM(CASE WHEN fee_waived = 0 THEN 1 ELSE 0 END) AS charged_count,
  SUM(CASE WHEN fee_waived = 1 THEN 1 ELSE 0 END) AS waived_count,
  CASE
    WHEN COUNT(*) = 0 THEN 0
    ELSE SUM(CASE WHEN fee_waived = 0 THEN 1 ELSE 0 END) / COUNT(*)
  END AS recovery_rate,
  MAX(as_of) AS as_of
FROM overdraft_events
```

**SQL design notes:**

- **W4 replication (integer division):** `SUM(...) / COUNT(*)` in SQLite performs integer division when both operands are integers. Since `charged_count < total_events` always (because `charged_count + waived_count = total_events` and `waived_count >= 0`), the division truncates to 0. This naturally replicates V1's `(decimal)(chargedCount / totalEvents)` where both operands are `int`. Evidence: [OverdraftRecoveryRateProcessor.cs:44].
- **W5 replication (banker's rounding):** V1 applies `Math.Round(recoveryRate, 4, MidpointRounding.ToEven)` to the result. Since the integer division always produces 0, rounding 0 to 4 decimal places is a no-op. The SQL omits an explicit ROUND call because `ROUND(0, 4)` in SQLite returns `0.0` which formats identically to `0` via `ToString()`. The rounding behavior is documented here for traceability but has no output effect. Evidence: [OverdraftRecoveryRateProcessor.cs:47].
- **BRD correction:** The BRD (BR-3) states rounding is to "2 decimal places." The actual V1 source code uses 4 decimal places: `Math.Round(recoveryRate, 4, MidpointRounding.ToEven)`. The FSD follows the source code, not the BRD's incorrect description. Evidence: [OverdraftRecoveryRateProcessor.cs:47].
- **Boolean handling:** PostgreSQL `boolean` values are converted to SQLite `INTEGER` (0/1) by the framework's `ToSqliteValue` method (`Transformation.cs:109`: `bool b => b ? 1 : 0`). The SQL uses `fee_waived = 0` for charged (not waived) and `fee_waived = 1` for waived.
- **Division by zero guard:** `CASE WHEN COUNT(*) = 0 THEN 0 ELSE ... END` prevents division by zero on empty datasets, matching V1's early-return guard at [OverdraftRecoveryRateProcessor.cs:23-27].
- **Empty table preamble:** `CREATE TABLE IF NOT EXISTS` ensures the SQL does not fail when DataSourcing returns an empty DataFrame. When the DataFrame has rows, the Transformation module creates the table first and the `IF NOT EXISTS` is a no-op.
- **as_of column:** `MAX(as_of)` returns the effective date as a `yyyy-MM-dd` text string (DataSourcing converts `DateOnly` to text via `Transformation.cs:110`: `DateOnly d => d.ToString("yyyy-MM-dd")`). Since the executor sets `__minEffectiveDate = __maxEffectiveDate`, all rows share the same `as_of` value, so `MAX(as_of)` = that date. This matches V1's `maxDate.ToString("yyyy-MM-dd")`. Evidence: [OverdraftRecoveryRateProcessor.cs:56].

**Transformation JSON:**
```json
{
  "type": "Transformation",
  "resultName": "output",
  "sql": "CREATE TABLE IF NOT EXISTS \"overdraft_events\" (\"fee_waived\" INTEGER, \"as_of\" TEXT); SELECT COUNT(*) AS total_events, SUM(CASE WHEN fee_waived = 0 THEN 1 ELSE 0 END) AS charged_count, SUM(CASE WHEN fee_waived = 1 THEN 1 ELSE 0 END) AS waived_count, CASE WHEN COUNT(*) = 0 THEN 0 ELSE SUM(CASE WHEN fee_waived = 0 THEN 1 ELSE 0 END) / COUNT(*) END AS recovery_rate, MAX(as_of) AS as_of FROM overdraft_events"
}
```

## 5. Writer Config

| Property | Value | Evidence |
|----------|-------|----------|
| type | CsvFileWriter | [overdraft_recovery_rate.json:18] |
| source | `output` | [overdraft_recovery_rate.json:19] |
| outputFile | `Output/double_secret_curated/overdraft_recovery_rate.csv` | V2 output path convention |
| includeHeader | true | [overdraft_recovery_rate.json:21] |
| trailerFormat | `TRAILER\|{row_count}\|{date}` | [overdraft_recovery_rate.json:22], BR-6 |
| writeMode | Overwrite | [overdraft_recovery_rate.json:23] |
| lineEnding | LF | [overdraft_recovery_rate.json:24] |

**Writer JSON:**
```json
{
  "type": "CsvFileWriter",
  "source": "output",
  "outputFile": "Output/double_secret_curated/overdraft_recovery_rate.csv",
  "includeHeader": true,
  "trailerFormat": "TRAILER|{row_count}|{date}",
  "writeMode": "Overwrite",
  "lineEnding": "LF"
}
```

**Trailer behavior:** The `{row_count}` token is replaced with `df.Count` by the framework (`CsvFileWriter.cs:64`). For a single summary row, this is always 1. The `{date}` token is replaced with `__maxEffectiveDate` formatted as `yyyy-MM-dd` (`CsvFileWriter.cs:60-62`). Evidence: BR-6.

## 6. Wrinkle Replication

### W4: Integer Division

- **V1 behavior:** `decimal recoveryRate = (decimal)(chargedCount / totalEvents);` where both operands are `int`. Since `chargedCount < totalEvents`, integer division truncates to 0. Evidence: [OverdraftRecoveryRateProcessor.cs:44].
- **V2 replication:** SQLite's `SUM(...) / COUNT(*)` performs integer division when both operands are integers (which they are, since `SUM` of integer `CASE` expressions and `COUNT(*)` both return integers). The result is 0, matching V1. No explicit `CAST` or `Math.Truncate` is needed because the natural SQLite behavior matches V1's natural C# behavior. The SQL comment in the job config is not possible (JSON doesn't support comments), so documentation lives here and in the Transformation SQL design notes above.

### W5: Banker's Rounding

- **V1 behavior:** `Math.Round(recoveryRate, 4, MidpointRounding.ToEven)` applied to a value that is always 0. Evidence: [OverdraftRecoveryRateProcessor.cs:47].
- **V2 replication:** Since the input is always 0 (due to W4), rounding has no effect on the output. The V2 SQL omits an explicit `ROUND()` call because `ROUND(0, 4)` in SQLite produces `0.0`, which formats identically to the integer `0` through `FormatField` (`CsvFileWriter.cs:80`: `val.ToString()`). If W4 were ever fixed, W5 would need revisiting because SQLite's `ROUND()` uses round-half-away-from-zero, not banker's rounding. This is a no-op for now but documented for future awareness.

## 7. Anti-Pattern Elimination

### AP3: Unnecessary External Module -- ELIMINATED

- **V1 problem:** V1 uses a full External module (`OverdraftRecoveryRateProcessor.cs`) for logic that is a simple count + integer division, which is directly expressible in SQL. Evidence: [overdraft_recovery_rate.json:13-16], [OverdraftRecoveryRateProcessor.cs:29-41].
- **V2 solution:** Replaced with a Transformation module using SQL aggregation. The module chain is `DataSourcing -> Transformation -> CsvFileWriter` (Tier 1).

### AP4: Unused Columns -- ELIMINATED

- **V1 problem:** DataSourcing pulls 7 columns (`overdraft_id`, `account_id`, `customer_id`, `overdraft_amount`, `fee_amount`, `fee_waived`, `event_timestamp`) but only `fee_waived` is used in the processing logic. Evidence: [overdraft_recovery_rate.json:10] vs. [OverdraftRecoveryRateProcessor.cs:36] (only `row["fee_waived"]` is accessed). Also documented in BRD EC-5.
- **V2 solution:** DataSourcing pulls only `fee_waived`. The `as_of` column is automatically appended by the DataSourcing module and is used in the SQL for the output `as_of` column.

### AP6: Row-by-Row Iteration -- ELIMINATED

- **V1 problem:** The External module uses a `foreach` loop over all rows to count events, charged, and waived: `foreach (var row in overdraftEvents.Rows) { totalEvents++; ... }`. Evidence: [OverdraftRecoveryRateProcessor.cs:33-41].
- **V2 solution:** Replaced with SQL aggregation: `COUNT(*)`, `SUM(CASE WHEN ...)`. Set-based operation, no row-by-row iteration.

### AP7: Magic Values -- NOT APPLICABLE

- No hardcoded thresholds or magic values exist in this job's V1 logic. The counts and division are straightforward aggregation without business-rule thresholds.

## 8. Proofmark Config

```yaml
comparison_target: "overdraft_recovery_rate"
reader: csv
threshold: 100.0
csv:
  header_rows: 1
  trailer_rows: 1
```

**Config rationale:**
- `reader: csv` -- output is a CSV file.
- `header_rows: 1` -- V1 config has `includeHeader: true`.
- `trailer_rows: 1` -- V1 config has `trailerFormat` present and `writeMode: Overwrite`. Overwrite mode produces a file with exactly one trailer at the end. Per CONFIG_GUIDE.md Example 3.
- `threshold: 100.0` -- no non-deterministic fields, no floating-point accumulation. All output values are deterministic integers and a date string. Strict 100% match required.
- No EXCLUDED columns -- all output columns (`total_events`, `charged_count`, `waived_count`, `recovery_rate`, `as_of`) are deterministic. BRD confirms: "None identified. Output is deterministic."
- No FUZZY columns -- `recovery_rate` is always exactly 0 in both V1 and V2 (due to W4 integer division). Integer columns (`total_events`, `charged_count`, `waived_count`) are exact counts. `as_of` is a date string.

## 9. Open Questions

**OQ-1: Empty-DataFrame behavior on intermediate effective dates.**

On effective dates with no overdraft events (23 out of 92 days in the 2024-10-01 to 2024-12-31 range), V1 and V2 behave differently on intermediate runs:

- **V1:** External module returns an empty DataFrame. CsvFileWriter writes header + trailer (row_count=0). Run succeeds. File is overwritten on the next day.
- **V2:** `CREATE TABLE IF NOT EXISTS` preamble creates an empty table. SQL returns a single row with `total_events=0, charged_count=0, waived_count=0, recovery_rate=0, as_of=NULL`. CsvFileWriter writes header + 1 data row + trailer (row_count=1). Run succeeds. File is overwritten on the next day.

Since the job uses Overwrite mode and the final effective date (2024-12-31) has data, the final output is identical between V1 and V2. The intermediate behavioral difference does not affect Proofmark comparison. However, it means the V2 job produces a slightly different file on zero-event days (1 row with zeros instead of 0 rows). This is acceptable for output equivalence of the final result.

If this intermediate-day behavioral difference is deemed unacceptable (e.g., for audit trail or per-day comparison purposes), the mitigation is to wrap the query in a `CASE` that returns zero rows when the table is empty, or to escalate to Tier 2 with a minimal External module that replicates V1's empty-data guard. For the scope of this project (Proofmark comparison of final output), Tier 1 is sufficient.

**OQ-2: BRD rounding precision discrepancy.**

The BRD (BR-3) states the recovery rate is "rounded to 2 decimal places." The actual V1 source code rounds to 4 decimal places: `Math.Round(recoveryRate, 4, MidpointRounding.ToEven)` at [OverdraftRecoveryRateProcessor.cs:47]. Since the value is always 0, this discrepancy has no output impact. The V2 implementation follows the source code (4 decimal places), not the BRD. The BRD should be corrected.

**OQ-3: Microsoft.Data.Sqlite multi-statement support.**

The V2 SQL relies on `CREATE TABLE IF NOT EXISTS ...; SELECT ...` being executed as a multi-statement query via `ExecuteReader()`. Microsoft.Data.Sqlite supports this -- `ExecuteReader()` returns the result set of the last SELECT statement. This should be verified during Phase D testing. If multi-statement support causes issues, the fallback is to use two separate Transformation steps or escalate to Tier 2.
