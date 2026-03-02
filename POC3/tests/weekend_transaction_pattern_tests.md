# WeekendTransactionPattern -- V2 Test Plan

## Job Info
- **V2 Config**: `weekend_transaction_pattern_v2.json`
- **Tier**: 2 (Framework + Minimal External)
- **External Modules**: `WeekendTransactionPatternV2DateInjector` (date injection), `WeekendTransactionPatternV2Rounder` (banker's rounding fix)
- **Module Chain**: DataSourcing -> External (date injector) -> Transformation -> External (rounder) -> CsvFileWriter

## Pre-Conditions

1. PostgreSQL is accessible at `172.18.0.1` with `claude/claude` credentials, database `atc`.
2. Table `datalake.transactions` contains data for Q4 2024 (2024-10-01 through 2024-12-31).
3. V1 baseline output exists at `Output/curated/weekend_transaction_pattern.csv` (produced by V1 config `weekend_transaction_pattern.json`).
4. V2 config `weekend_transaction_pattern_v2.json` is deployed to `JobExecutor/Jobs/`.
5. Both External module classes (`WeekendTransactionPatternV2DateInjector`, `WeekendTransactionPatternV2Rounder`) are compiled into `ExternalModules/bin/Debug/net8.0/ExternalModules.dll`.
6. Proofmark config `POC3/proofmark_configs/weekend_transaction_pattern.yaml` is deployed.
7. The `Output/double_secret_curated/` directory exists or will be created by the framework.

## Test Cases

### TC-1: Output Schema Validation

**Objective**: Verify the V2 output CSV has exactly the correct columns in the correct order (BR-3, BR-8, BR-9).

**Expected schema** (5 columns, in order):
| # | Column | Type |
|---|--------|------|
| 1 | day_type | string |
| 2 | txn_count | integer |
| 3 | total_amount | decimal (2 dp) |
| 4 | avg_amount | decimal (2 dp) |
| 5 | as_of | date (yyyy-MM-dd) |

**Steps**:
1. Run V2 job: `dotnet run --project JobExecutor -- WeekendTransactionPatternV2`
2. Read the first line of `Output/double_secret_curated/weekend_transaction_pattern.csv`.
3. Confirm header is: `day_type,txn_count,total_amount,avg_amount,as_of`
4. Compare header to V1 output at `Output/curated/weekend_transaction_pattern.csv` -- must be identical.

**Pass criteria**: Column names and column order match V1 exactly.

### TC-2: Weekday Effective Date -- Two Rows (BR-3, BR-4, BR-5)

**Objective**: Verify that on a weekday effective date, V2 outputs exactly 2 rows: "Weekday" first, "Weekend" second.

**Steps**:
1. Run V2 for a known weekday, e.g. `dotnet run --project JobExecutor -- 2024-10-01 WeekendTransactionPatternV2` (Tuesday).
2. Inspect V2 output. Expect exactly 2 data rows (plus header and trailer).
3. Confirm row 1 has `day_type = "Weekday"` with non-zero `txn_count`.
4. Confirm row 2 has `day_type = "Weekend"` with `txn_count = 0`, `total_amount = 0`, `avg_amount = 0`.
5. Run V1 for the same date and compare.

**Pass criteria**: V2 produces exactly 2 rows in order (Weekday, Weekend). Weekend row has zero counts and amounts. Output matches V1.

### TC-3: Weekend (Saturday) Effective Date -- Two Rows (BR-3, BR-4)

**Objective**: Verify that on a Saturday, V2 outputs exactly 2 rows with no weekly summary.

**Steps**:
1. Run V2 for a known Saturday, e.g. `dotnet run --project JobExecutor -- 2024-10-05 WeekendTransactionPatternV2`.
2. Inspect V2 output. Expect exactly 2 data rows.
3. Confirm row 1 has `day_type = "Weekday"` with `txn_count = 0`, `total_amount = 0`, `avg_amount = 0`.
4. Confirm row 2 has `day_type = "Weekend"` with non-zero `txn_count`.
5. Confirm NO `WEEKLY_TOTAL_*` rows are present.

**Pass criteria**: Saturday produces 2 rows, no weekly summary. Matches V1.

### TC-4: Sunday Effective Date -- Four Rows with Weekly Summary (BR-6, BR-7)

**Objective**: Verify that on a Sunday, V2 outputs 4 rows: 2 daily + 2 weekly summary.

**Steps**:
1. Run V2 for a known Sunday, e.g. `dotnet run --project JobExecutor -- 2024-10-06 WeekendTransactionPatternV2`.
2. Inspect V2 output. Expect exactly 4 data rows.
3. Confirm row order: `Weekday`, `Weekend`, `WEEKLY_TOTAL_Weekday`, `WEEKLY_TOTAL_Weekend`.
4. Confirm `WEEKLY_TOTAL_*` rows aggregate data from Monday through Sunday (2024-09-30 to 2024-10-06, but sourced data starts 2024-10-01 per AP10, so effectively 2024-10-01 to 2024-10-06).
5. Run V1 for the same date and compare.

**Pass criteria**: V2 produces 4 rows in correct order with correct weekly aggregations. Matches V1.

### TC-5: Weekly Summary Aggregation Correctness (BR-7, FSD Section 5)

**Objective**: Verify the weekly summary rows aggregate the full Mon-Sun range correctly.

**Steps**:
1. Pick a mid-range Sunday, e.g. 2024-10-13 (full week of data: Oct 7-13).
2. Run V2 for that date.
3. Query source data to compute expected weekly totals:
   ```sql
   SELECT
     CASE WHEN EXTRACT(DOW FROM as_of) IN (0, 6) THEN 'Weekend' ELSE 'Weekday' END AS day_type,
     COUNT(*) AS txn_count,
     SUM(amount) AS total_amount
   FROM datalake.transactions
   WHERE as_of >= '2024-10-07' AND as_of <= '2024-10-13'
   GROUP BY day_type;
   ```
4. Compare query results against the `WEEKLY_TOTAL_Weekday` and `WEEKLY_TOTAL_Weekend` rows in V2 output (after applying banker's rounding to total_amount and avg_amount).
5. Cross-reference against V1 output for the same date.

**Pass criteria**: Weekly totals match both the manual SQL computation and V1 output.

### TC-6: Row Count Equivalence Across Full Date Range

**Objective**: Verify V2 produces the same number of data rows as V1 for every effective date.

**Steps**:
1. Run both V1 and V2 for the full auto-advance range (2024-10-01 through 2024-12-31).
2. Since writeMode is Overwrite, only the last date's output survives. Compare final outputs.
3. The last date (2024-12-31) is a Tuesday, so expect 2 data rows.
4. To test intermediate dates, run V1 and V2 for individual dates and compare after each.

**Pass criteria**: For every effective date, V2 row count matches V1. Non-Sundays = 2 rows, Sundays = 4 rows.

### TC-7: Writer Configuration (FSD Section 7)

**Objective**: Verify the CsvFileWriter config matches V1 behavior.

| Property | Expected Value | Verification Method |
|----------|---------------|---------------------|
| type | CsvFileWriter | Inspect V2 JSON config |
| source | `output` | Inspect V2 JSON config; confirm the rounder External stores result as `output` |
| outputFile | `Output/double_secret_curated/weekend_transaction_pattern.csv` | Confirm file is written to this path |
| includeHeader | `true` | First line of output is column names |
| trailerFormat | `TRAILER\|{row_count}\|{date}` | Last line matches this pattern |
| writeMode | `Overwrite` | Run job twice; second run replaces first |
| lineEnding | `LF` | `xxd` or `od` on output file to confirm `\n` (0x0A), no `\r` (0x0D) |

**Steps**:
1. Inspect `weekend_transaction_pattern_v2.json` writer section for all properties above.
2. Run the job and verify the output file location.
3. Check line endings: `file Output/double_secret_curated/weekend_transaction_pattern.csv` should report "ASCII text" (not "CRLF").
4. Verify trailer format on last line.

**Pass criteria**: All writer properties match V1 configuration. Output path differs only in directory (`double_secret_curated` vs `curated`).

### TC-8: Trailer Correctness (BR-11)

**Objective**: Verify the trailer row count and date are correct.

**Steps**:
1. Run V2 for a weekday (e.g. 2024-10-01). Inspect trailer: expect `TRAILER|2|2024-10-01`.
2. Run V2 for a Sunday (e.g. 2024-10-06). Inspect trailer: expect `TRAILER|4|2024-10-06`.
3. Compare trailers against V1 for the same dates.

**Pass criteria**: Trailer row count is 2 on non-Sundays and 4 on Sundays. Trailer date matches `__maxEffectiveDate` in `yyyy-MM-dd` format.

### TC-9: Anti-Pattern Elimination Verification

**Objective**: Confirm all identified anti-patterns are addressed in V2.

| AP Code | Anti-Pattern | V2 Status | Verification |
|---------|-------------|-----------|--------------|
| AP3 | Unnecessary External module | Mostly eliminated | V2 has 2 minimal External modules (date injector ~15 lines, rounder ~25 lines) instead of V1's monolithic processor. All business logic (classification, aggregation, weekly summary, zero-count handling) lives in SQL. Verify no aggregation or classification logic exists in the External modules. |
| AP4 | Unused columns | Eliminated | V2 DataSourcing sources only `["amount", "as_of"]`. V1 sourced `transaction_id`, `account_id`, `txn_timestamp`, `txn_type`, `amount`. Verify V2 config has only 2 columns listed. |
| AP6 | Row-by-row iteration | Eliminated | V1's `foreach` loops replaced by SQL `GROUP BY`/`COUNT`/`SUM`. The rounder External iterates only 2-4 output rows, not input rows. Verify SQL uses set-based operations. |
| AP10 | Over-sourced date range | Retained (documented) | V2 retains hardcoded `minEffectiveDate: "2024-10-01"` / `maxEffectiveDate: "2024-12-31"`. Verify config has these values and the FSD documents why (framework cannot compute dynamic date offsets for weekly summaries). |

**Steps**:
1. Inspect `weekend_transaction_pattern_v2.json` for column list and date range.
2. Inspect External module source code for absence of business logic.
3. Inspect Transformation SQL for set-based operations.

**Pass criteria**: AP3 mostly eliminated (External modules are adapters only), AP4 and AP6 fully eliminated, AP10 retained with documentation.

### TC-10: Edge Cases

#### TC-10a: Empty Transactions (BR-10)

**Objective**: Verify behavior when no transactions exist for the effective date.

**Steps**:
1. If testable with a date where `datalake.transactions` has zero rows for `as_of = maxDate`, run V2 for that date.
2. Expected: The rounder External receives an empty/null `pre_output` DataFrame and produces an empty `output` DataFrame. CsvFileWriter writes header + `TRAILER|0|{date}` only.
3. Compare against V1 for the same date.

**Pass criteria**: Empty input produces header-only output with `TRAILER|0|{date}`. Matches V1 behavior.

#### TC-10b: First Week Boundary -- Oct 6 Sunday (FSD OQ-3)

**Objective**: Verify the first Sunday's weekly summary handles the partial week correctly.

**Steps**:
1. Run V2 for 2024-10-06 (first Sunday). `monday_of_week` = 2024-09-30, but sourced data starts at 2024-10-01.
2. Verify `WEEKLY_TOTAL_*` rows aggregate only Oct 1-Oct 6 data (not Sep 30).
3. Compare against V1 for 2024-10-06.

**Pass criteria**: Weekly totals cover Oct 1-6 only (missing Sep 30 data is expected). Matches V1.

#### TC-10c: Last Day of Range -- Dec 31 Tuesday

**Objective**: Verify the last effective date produces correct output without weekly summary.

**Steps**:
1. Run V2 for 2024-12-31 (Tuesday).
2. Confirm exactly 2 rows (Weekday + Weekend), no weekly summary.
3. Compare against V1.

**Pass criteria**: Dec 31 output is 2 rows. Matches V1.

#### TC-10d: All Transactions in One Category

**Objective**: Verify that when all transactions for a date fall into one day_type, the other category still appears with zeros (BR-4).

**Steps**:
1. Run V2 for a weekday where all transactions have the same `as_of` (guaranteed since DataSourcing filters to `as_of = maxDate`).
2. On a weekday, all transactions are classified as "Weekday". Confirm "Weekend" row exists with `txn_count = 0`, `total_amount = 0`, `avg_amount = 0`.
3. On a Saturday, all transactions are classified as "Weekend". Confirm "Weekday" row exists with zeros.

**Pass criteria**: Both day_type rows always appear, even when one has zero transactions.

### TC-11: Proofmark Configuration

**Objective**: Validate the Proofmark YAML config is correct and complete.

**Expected config** (`POC3/proofmark_configs/weekend_transaction_pattern.yaml`):
```yaml
comparison_target: "weekend_transaction_pattern"
reader: csv
threshold: 100.0
csv:
  header_rows: 1
  trailer_rows: 1
```

**Verification checklist**:
| Field | Expected | Rationale |
|-------|----------|-----------|
| comparison_target | `"weekend_transaction_pattern"` | Matches job/output name |
| reader | `csv` | Output is CsvFileWriter |
| threshold | `100.0` | Strict match -- rounding divergence resolved by External module |
| csv.header_rows | `1` | `includeHeader: true` in writer config |
| csv.trailer_rows | `1` | `trailerFormat` present with `writeMode: Overwrite` -- single trailer at EOF |
| columns.excluded | (none) | No non-deterministic fields per BRD |
| columns.fuzzy | (none) | Rounding handled by `Math.Round(decimal, 2, MidpointRounding.ToEven)` in External module -- no tolerance needed |

**Pass criteria**: YAML file matches expected config exactly. No excluded or fuzzy columns.

### TC-12: Proofmark Comparison Execution

**Objective**: Run Proofmark and confirm V1-V2 output equivalence.

**Steps**:
1. Run V1 for full date range to produce baseline at `Output/curated/weekend_transaction_pattern.csv`.
2. Run V2 for full date range to produce output at `Output/double_secret_curated/weekend_transaction_pattern.csv`.
3. Execute Proofmark comparison using the config at `POC3/proofmark_configs/weekend_transaction_pattern.yaml`.
4. Confirm 100% match rate.

**Pass criteria**: Proofmark reports 100.0% match. Zero row-level differences.

## W-Code Test Cases

### TC-W3a: End-of-Week Boundary (Sunday Weekly Summaries)

**Objective**: Verify weekly summary rows are emitted only on Sundays and contain correct aggregations.

**Steps**:
1. Run V2 for every Sunday in Q4 2024 (Oct 6, 13, 20, 27; Nov 3, 10, 17, 24; Dec 1, 8, 15, 22, 29).
2. For each Sunday, confirm 4 output rows with `WEEKLY_TOTAL_*` rows present.
3. For each non-Sunday, confirm 2 output rows with no `WEEKLY_TOTAL_*` rows.
4. Compare all outputs against V1.

**Pass criteria**: Weekly summary rows appear exclusively on Sundays. All weekly aggregations match V1.

### TC-W5: Banker's Rounding -- RESOLVED

**Objective**: Confirm the rounding fixer External module produces V1-equivalent banker's rounding.

**Background**: V1 uses `Math.Round(decimal, 2)` which defaults to `MidpointRounding.ToEven`. The V2 SQL produces unrounded intermediate values, and the `WeekendTransactionPatternV2Rounder` applies `Math.Round(decimal, 2, MidpointRounding.ToEven)` post-SQL.

**Steps**:
1. Run V2 and V1 for dates where `avg_amount` might hit exact midpoints (i.e., `total_amount / txn_count` produces a value ending in `.XX5`).
2. Verify V2 `total_amount` and `avg_amount` match V1 exactly for every date.
3. If any differences are found, inspect whether they are in the pre-rounding or post-rounding stage.

**Pass criteria**: All `total_amount` and `avg_amount` values match V1 exactly. The External module's `MidpointRounding.ToEven` produces identical output to V1's `Math.Round(decimal, 2)`.

### TC-W5-OQ2: Decimal-to-Double Precision in SQLite (FSD OQ-2)

**Objective**: Verify that SQLite's REAL (double) storage of amounts does not introduce precision loss that survives the post-SQL rounding.

**Steps**:
1. For dates with large transaction counts, compute expected `SUM(amount)` using `decimal` arithmetic (query PostgreSQL directly).
2. Compare against V2's unrounded `pre_output` DataFrame values (if inspectable via debug logging).
3. Verify that after the rounder applies `Math.Round(decimal, 2)`, any double->decimal conversion artifacts are eliminated.

**Pass criteria**: No precision differences survive the rounding step. If any do appear, the FSD contingency is to add fuzzy tolerance of 0.01 absolute on `total_amount`.

## Notes

1. **Output path difference**: V1 writes to `Output/curated/`, V2 writes to `Output/double_secret_curated/`. This is by design per POC3 spec and is NOT a defect.
2. **AP10 retention**: The hardcoded date range is intentionally retained. This is documented in the FSD and is not a test failure.
3. **Two External modules**: V2 uses two separate External module classes (`DateInjector` and `Rounder`). This is architecturally clean -- each handles a single adapter concern. Neither contains business logic.
4. **Overwrite mode**: Since writeMode is Overwrite, multi-day auto-advance only preserves the last date's output. Proofmark comparison after full-range runs compares only the final date. Per-date comparison requires individual single-date runs.
5. **as_of column**: The `as_of` in all output rows is `__maxEffectiveDate` formatted as `yyyy-MM-dd` (BR-9), not derived from individual transaction dates.
