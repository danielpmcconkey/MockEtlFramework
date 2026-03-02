# WireDirectionSummary -- V2 Test Plan

## Job Info
- **V2 Config**: `wire_direction_summary_v2.json`
- **Tier**: 2 (Framework + Minimal External)
- **External Module**: `WireDirectionSummaryV2Processor` (W7-aware CSV writer only)
- **Module Chain**: DataSourcing -> Transformation (SQL) -> External (CSV writing with inflated trailer)

## Pre-Conditions

1. PostgreSQL is accessible at `172.18.0.1` with `claude/claude` credentials, database `atc`.
2. Table `datalake.wire_transfers` contains data for the effective date range starting `2024-10-01`.
3. V1 baseline output exists at `Output/curated/wire_direction_summary.csv` (produced by V1 config `wire_direction_summary.json`).
4. V2 config `wire_direction_summary_v2.json` is deployed to `JobExecutor/Jobs/`.
5. External module class `WireDirectionSummaryV2Processor` is compiled into `ExternalModules/bin/Debug/net8.0/ExternalModules.dll`.
6. Proofmark config `POC3/proofmark_configs/wire_direction_summary.yaml` is deployed.
7. The `Output/double_secret_curated/` directory exists or will be created by the External module.

## Test Cases

### TC-1: Output Schema Validation

**Objective**: Verify the V2 output CSV has exactly the correct columns in the correct order (BR-2, BR-4).

**Expected schema** (5 columns, in order):
| # | Column | Type |
|---|--------|------|
| 1 | direction | string (Inbound/Outbound) |
| 2 | wire_count | integer |
| 3 | total_amount | numeric (2 dp) |
| 4 | avg_amount | numeric (2 dp) |
| 5 | as_of | date |

**Steps**:
1. Run V2 job: `dotnet run --project JobExecutor -- WireDirectionSummaryV2`
2. Read the first line of `Output/double_secret_curated/wire_direction_summary.csv`.
3. Confirm header is: `direction,wire_count,total_amount,avg_amount,as_of`
4. Compare header to V1 output at `Output/curated/wire_direction_summary.csv` -- must be identical.

**Pass criteria**: Column names and column order match V1 exactly.

### TC-2: Row Count Equivalence

**Objective**: Verify V2 produces the same number of data rows as V1 (BR-1, BR-2).

**Steps**:
1. Count data rows (excluding header and trailer) in V1 output.
2. Count data rows (excluding header and trailer) in V2 output.
3. Compare counts. Expected: typically 2 rows (one per direction: Inbound, Outbound).

**Pass criteria**: Row counts are identical. Typically 2, but could be 1 if all wires are one direction.

### TC-3: Data Content Equivalence

**Objective**: Verify V2 output data matches V1 for all columns.

**Steps**:
1. Run V1 and V2 for the same effective date (e.g. 2024-10-01).
2. Compare data rows (excluding header and trailer) between the two outputs.
3. For each row, verify: `direction`, `wire_count`, `total_amount`, `avg_amount`, `as_of` all match.
4. If differences exist in `total_amount` or `avg_amount`, check whether they are at exact .XX5 midpoints (W5 risk).

**Pass criteria**: All data values match V1 exactly. OR: differences are exclusively in numeric columns at W5 midpoint boundaries (triggers contingency path).

### TC-4: Aggregation Correctness (BR-1, BR-2)

**Objective**: Verify the SQL aggregation produces correct counts, totals, and averages.

**Steps**:
1. Run V2 for a specific date (e.g. 2024-10-01).
2. Query source data directly to compute expected values:
   ```sql
   SELECT
     direction,
     COUNT(*) AS wire_count,
     ROUND(SUM(amount)::numeric, 2) AS total_amount,
     ROUND((SUM(amount) / COUNT(*))::numeric, 2) AS avg_amount
   FROM datalake.wire_transfers
   WHERE as_of = '2024-10-01'
   GROUP BY direction
   ORDER BY direction;
   ```
3. Compare V2 output against the manual query results.
4. Note: Minor rounding differences are possible due to SQLite ROUND vs PostgreSQL ROUND vs V1's `Math.Round`. Use V1 output as the authoritative baseline.

**Pass criteria**: V2 output matches V1 output. Manual SQL query serves as a sanity check.

### TC-5: No Status Filter (BR-1)

**Objective**: Confirm all wire transfer statuses (Completed, Pending, Rejected) are included in the aggregation.

**Steps**:
1. Query for wire transfers with non-Completed statuses:
   ```sql
   SELECT direction, status, COUNT(*)
   FROM datalake.wire_transfers
   WHERE as_of = '2024-10-01' AND status != 'Completed'
   GROUP BY direction, status;
   ```
2. If any non-Completed wires exist, verify they are counted in V2's `wire_count` and included in `total_amount`/`avg_amount`.
3. Confirm V2 SQL has no `WHERE status = 'Completed'` or similar filter.

**Pass criteria**: All statuses are included. No status filter in V2 SQL or External module.

### TC-6: Trailer Correctness -- Inflated Input Count (BR-3, W7)

**Objective**: Verify the trailer uses the INPUT row count (before grouping), not the OUTPUT row count.

**Steps**:
1. Run V2 for a specific date (e.g. 2024-10-01).
2. Read the trailer line (last line of output file).
3. Expected format: `TRAILER|{inputCount}|{date}` where `{inputCount}` is the count of all `wire_transfers` rows for that date (typically 35-62), NOT the output row count (typically 2).
4. Verify by querying: `SELECT COUNT(*) FROM datalake.wire_transfers WHERE as_of = '2024-10-01'`.
5. Compare trailer against V1's trailer for the same date.

**Pass criteria**: Trailer row count matches the input row count, not the output row count. Format is `TRAILER|{N}|{yyyy-MM-dd}`. Matches V1 exactly.

### TC-7: Trailer Date (BR-9)

**Objective**: Verify the trailer date uses `__maxEffectiveDate` formatted as `yyyy-MM-dd`.

**Steps**:
1. Run V2 for 2024-10-15.
2. Read the trailer line. Confirm date portion is `2024-10-15`.
3. Run for 2024-12-31. Confirm date portion is `2024-12-31`.
4. Compare against V1 trailers for the same dates.

**Pass criteria**: Trailer date matches `__maxEffectiveDate` in `yyyy-MM-dd` format.

### TC-8: Writer Configuration (FSD Section 5)

**Objective**: Verify the External module's CSV writing matches V1's output format exactly.

| Property | Expected Value | Verification Method |
|----------|---------------|---------------------|
| Output path | `Output/double_secret_curated/wire_direction_summary.csv` | Confirm file is written to this path |
| Header | Yes (column names joined by comma) | First line of output is column names |
| Line ending | LF (`\n`) | `xxd` or `od` on output file to confirm `\n` (0x0A), no `\r` (0x0D) |
| Write mode | Overwrite (`append: false`) | Run job twice; second run replaces first |
| Trailer format | `TRAILER\|{inputCount}\|{date}` | Last line matches this pattern with inflated count (W7) |
| RFC 4180 quoting | No -- values joined by `.ToString()` and comma | No quoted fields in output (BR-5) |

**Steps**:
1. Run the job and verify the output file location.
2. Check line endings: `file Output/double_secret_curated/wire_direction_summary.csv` should report "ASCII text" (not "CRLF").
3. Verify no RFC 4180 quoting in any field values.
4. Verify trailer on last line.
5. Run the job a second time -- confirm file is overwritten, not appended.

**Pass criteria**: All writer properties match V1 behavior. Output path differs only in directory.

### TC-9: Anti-Pattern Elimination Verification

**Objective**: Confirm all identified anti-patterns are addressed in V2.

| AP Code | Anti-Pattern | V2 Status | Verification |
|---------|-------------|-----------|--------------|
| AP3 | Unnecessary External module | Partially eliminated | V1 External does data iteration, grouping, aggregation, AND file writing (107 lines). V2 External does ONLY file writing (~40 lines). All business logic is in SQL. Verify External module source contains zero aggregation/grouping logic. |
| AP4 | Unused columns | Eliminated | V2 DataSourcing sources only `["direction", "amount"]`. V1 sourced 5 columns: `wire_id`, `customer_id`, `direction`, `amount`, `status`. Verify V2 config has only 2 columns listed. |
| AP6 | Row-by-row iteration | Eliminated | V1's `foreach` loop with `Dictionary<string, (int, decimal)>` replaced by SQL `GROUP BY direction` with `COUNT`/`SUM`. Verify V2 SQL uses set-based operations. |

**Steps**:
1. Inspect `wire_direction_summary_v2.json` for column list (should be `["direction", "amount"]`).
2. Inspect `WireDirectionSummaryV2Processor` source: confirm it reads pre-computed DataFrames and writes to disk. No grouping, counting, summing, or averaging in the code.
3. Inspect Transformation SQL for `GROUP BY direction` with `COUNT(*)`, `SUM(amount)`.

**Pass criteria**: AP3 partially eliminated (External is a writer-only adapter), AP4 and AP6 fully eliminated.

### TC-10: Edge Cases

#### TC-10a: Empty Wire Transfers (BR-7)

**Objective**: Verify behavior when no wire transfers exist for the effective date.

**Steps**:
1. If testable with a date where `datalake.wire_transfers` has zero rows for `as_of = maxDate`, run V2 for that date.
2. Expected: The External module writes header + `TRAILER|0|{date}` with no data rows.
3. Compare against V1 for the same date.

**Pass criteria**: Empty input produces header + `TRAILER|0|{date}` only. Matches V1.

#### TC-10b: Single Direction Only (BRD Edge Case 4)

**Objective**: Verify behavior when all wires for a date are in one direction.

**Steps**:
1. Query for dates with only one direction:
   ```sql
   SELECT as_of, COUNT(DISTINCT direction) AS dir_count
   FROM datalake.wire_transfers
   GROUP BY as_of
   HAVING COUNT(DISTINCT direction) = 1;
   ```
2. If such a date exists, run V2 for it.
3. Expected: 1 data row (the direction present), trailer with the full input count.
4. Compare against V1.

**Pass criteria**: Output has 1 data row. Trailer count reflects all input rows regardless of direction count.

#### TC-10c: Row Ordering -- Inbound Before Outbound (FSD OQ-3)

**Objective**: Verify V2 row order matches V1 row order.

**Steps**:
1. Run V2 and V1 for the same date.
2. V2 uses `ORDER BY direction` in SQL, which produces alphabetical order (Inbound before Outbound).
3. V1 uses dictionary iteration order, which is insertion-order (data-dependent).
4. Compare row order between V1 and V2.
5. If V1 produces a different order (e.g. Outbound first), the V2 SQL `ORDER BY` must be adjusted.

**Pass criteria**: Row order matches V1 exactly. If V1 consistently produces Inbound-first, the alphabetical `ORDER BY direction` is correct. If not, this is a defect requiring SQL adjustment.

#### TC-10d: as_of Column Value (BR-4, FSD OQ-2)

**Objective**: Verify the `as_of` column in V2 output matches V1's format and value.

**Steps**:
1. Run V1 and V2 for 2024-10-01.
2. Compare the `as_of` column value in both outputs.
3. V1 uses `wireTransfers.Rows[0]["as_of"]?.ToString()` on a `DateOnly` value. V2's SQL produces `as_of` from the GROUP BY query.
4. Check format: V1 uses `DateOnly.ToString()` which in invariant culture produces `MM/dd/yyyy`. V2 must match.

**Pass criteria**: `as_of` format and value match V1 exactly. If format differs, the External module needs explicit formatting.

#### TC-10e: Directory Auto-Creation (BR-8)

**Objective**: Verify the External module creates the output directory if it does not exist.

**Steps**:
1. Delete the `Output/double_secret_curated/` directory (if it exists).
2. Run V2.
3. Verify the directory was created and the output file was written.

**Pass criteria**: Directory is created automatically. No error thrown.

### TC-11: Proofmark Configuration

**Objective**: Validate the Proofmark YAML config is correct and complete.

**Expected config** (`POC3/proofmark_configs/wire_direction_summary.yaml`):
```yaml
comparison_target: "wire_direction_summary"
reader: csv
threshold: 100.0
csv:
  header_rows: 1
  trailer_rows: 1
```

**Verification checklist**:
| Field | Expected | Rationale |
|-------|----------|-----------|
| comparison_target | `"wire_direction_summary"` | Matches job/output name |
| reader | `csv` | Output is a CSV file |
| threshold | `100.0` | Strict match -- start strict per best practices |
| csv.header_rows | `1` | External module writes a header row |
| csv.trailer_rows | `1` | Trailer present with Overwrite mode -- single trailer at EOF |
| columns.excluded | (none) | No non-deterministic fields per BRD |
| columns.fuzzy | (none) | Start strict; add fuzzy only if W5 rounding causes comparison failure |

**Pass criteria**: YAML file matches expected config exactly. No excluded or fuzzy columns.

### TC-12: Proofmark Comparison Execution

**Objective**: Run Proofmark and confirm V1-V2 output equivalence.

**Steps**:
1. Run V1 for full date range to produce baseline at `Output/curated/wire_direction_summary.csv`.
2. Run V2 for full date range to produce output at `Output/double_secret_curated/wire_direction_summary.csv`.
3. Execute Proofmark comparison using the config at `POC3/proofmark_configs/wire_direction_summary.yaml`.
4. Confirm 100% match rate.

**Contingency**: If comparison fails on `avg_amount` or `total_amount` due to W5 rounding divergence:
- Option A: Move rounding into the External module using `Math.Round(value, 2, MidpointRounding.ToEven)` and re-run.
- Option B: Add fuzzy tolerance per FSD Section 8 contingency config:
  ```yaml
  columns:
    fuzzy:
      - name: "avg_amount"
        tolerance: 0.01
        tolerance_type: absolute
        reason: "V1 uses decimal Math.Round (banker's rounding) vs V2 SQLite ROUND (half-up). [WireDirectionSummaryWriter.cs:49]"
      - name: "total_amount"
        tolerance: 0.01
        tolerance_type: absolute
        reason: "V1 uses decimal SUM vs V2 SQLite REAL SUM. [WireDirectionSummaryWriter.cs:48]"
  ```

**Pass criteria**: Proofmark reports 100.0% match. If contingency is triggered, document the affected rows and resolution path.

## W-Code Test Cases

### TC-W5: Banker's Rounding Risk

**Objective**: Assess whether W5 rounding divergence affects this job's output.

**Background**: V1 uses `Math.Round(decimal, 2)` (banker's rounding, `MidpointRounding.ToEven`). V2 uses SQLite `ROUND(value, 2)` (half-away-from-zero). These produce different results only at exact .XX5 midpoints.

**Steps**:
1. For `total_amount`: Source `amount` values have exactly 2 decimal places. `SUM` of 2-dp values is exact in both decimal and double. `ROUND(SUM, 2)` is a no-op (already 2 dp). W5 risk is LOW for `total_amount`.
2. For `avg_amount`: `SUM(amount) / COUNT(*)` can produce arbitrary decimal expansions. Check for midpoints:
   ```sql
   SELECT direction,
     SUM(amount) AS total,
     COUNT(*) AS cnt,
     SUM(amount) * 1.0 / COUNT(*) AS raw_avg
   FROM datalake.wire_transfers
   WHERE as_of = '2024-10-01'
   GROUP BY direction;
   ```
3. If `raw_avg` ends in exactly `.XX5` for any date, the two rounding modes will diverge.
4. Run Proofmark to empirically detect any differences.

**Pass criteria**: No .XX5 midpoint averages exist in the data (W5 is moot), OR midpoints exist and trigger the contingency path described in TC-12.

### TC-W7: Trailer Inflated Count

**Objective**: Verify the trailer's row count is the INPUT row count, not the OUTPUT row count.

**Steps**:
1. Run V2 for 2024-10-01.
2. Count data rows in output (should be 2: Inbound, Outbound).
3. Read trailer. It should NOT say `TRAILER|2|2024-10-01`.
4. Query input count: `SELECT COUNT(*) FROM datalake.wire_transfers WHERE as_of = '2024-10-01'`.
5. Trailer should say `TRAILER|{that count}|2024-10-01` (e.g., `TRAILER|47|2024-10-01`).
6. Compare against V1 trailer for the same date.

**Pass criteria**: Trailer count matches the input row count (before grouping), NOT the output row count (2). V1 and V2 trailers are identical.

### TC-W9: Overwrite Write Mode

**Objective**: Verify Overwrite behavior matches V1 -- each run replaces the entire file.

**Steps**:
1. Run V2 for 2024-10-01. Note output file content.
2. Run V2 for 2024-10-02. Note output file content.
3. Confirm the file contains ONLY 2024-10-02 data (2024-10-01 data is gone).
4. Confirm only one header and one trailer in the file (no duplicates from prior runs).

**Pass criteria**: File is fully overwritten on each run. Only the last execution date's data survives.

## Notes

1. **Output path difference**: V1 writes to `Output/curated/`, V2 writes to `Output/double_secret_curated/`. This is by design per POC3 spec and is NOT a defect.
2. **No CsvFileWriter in V2 config**: Unlike most V2 jobs, this job does NOT use the framework's CsvFileWriter. The External module handles all file I/O because CsvFileWriter's `{row_count}` token cannot produce the inflated input-row count required by W7. The V2 config has no writer module -- the External module IS the writer.
3. **Overwrite mode caution**: Since the External module uses `append: false`, multi-day auto-advance only preserves the last date's output. Run per-date comparisons for thorough validation, or run V1 and V2 for the same final date.
4. **Row ordering risk (FSD OQ-3)**: V2 uses `ORDER BY direction` (alphabetical). V1 uses dictionary iteration order. If V1 outputs Outbound before Inbound, this will cause a Proofmark failure. Check V1 output early and adjust SQL if needed.
5. **as_of format risk (FSD OQ-2)**: V1's `DateOnly.ToString()` format depends on the runtime culture. V2's as_of comes through SQLite. If formats differ, the External module must apply explicit formatting.
6. **Trailer date fallback (BR-9)**: V1 falls back to `DateTime.Today` if `__maxEffectiveDate` is not in shared state. V2 External module must replicate this fallback, though in practice the executor always injects the date.
