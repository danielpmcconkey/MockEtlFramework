# MonthlyRevenueBreakdown -- V2 Test Plan

## Job Info
- **V2 Config**: `monthly_revenue_breakdown_v2.json`
- **Tier**: 2 (Framework + Minimal External)
- **External Module**: `MonthlyRevenueBreakdownV2Processor` (`ExternalModules/MonthlyRevenueBreakdownV2Processor.cs`)

## Pre-Conditions

1. PostgreSQL is accessible at `172.18.0.1` with user `claude`, database `atc`.
2. The `datalake.overdraft_events` and `datalake.transactions` tables exist and contain data for the date range 2024-10-01 through 2024-12-31.
3. The V1 baseline output exists at `Output/curated/monthly_revenue_breakdown.csv`.
4. The V2 output directory `Output/double_secret_curated/` exists or will be created by the framework.
5. The V1 External module source at `ExternalModules/MonthlyRevenueBreakdownBuilder.cs` is available for reference (read-only).
6. The V1 job config at `JobExecutor/Jobs/monthly_revenue_breakdown.json` is unmodified (reference only).
7. The V2 External module `ExternalModules/MonthlyRevenueBreakdownV2Processor.cs` compiles successfully via `dotnet build`.

## Test Cases

### TC-1: Output Schema Validation

**Objective:** Verify V2 output CSV has the correct columns in the correct order.

| Step | Action | Expected Result |
|------|--------|-----------------|
| 1.1 | Run V2 job for a single normal date (e.g., 2024-10-01). | Job completes successfully. |
| 1.2 | Read the header row of `Output/double_secret_curated/monthly_revenue_breakdown.csv`. | Header is: `revenue_source,total_revenue,transaction_count,as_of` |
| 1.3 | Verify column count is exactly 4. | 4 columns per data row. |
| 1.4 | Verify column order matches V1: `revenue_source`, `total_revenue`, `transaction_count`, `as_of`. | Order is identical to V1 output column definition [MonthlyRevenueBreakdownBuilder.cs:10-13]. |

### TC-2: Row Count Equivalence

**Objective:** Verify V2 produces the same number of data rows as V1 for every effective date.

| Step | Action | Expected Result |
|------|--------|-----------------|
| 2.1 | Run V2 for a normal date (not Oct 31, e.g., 2024-10-15). Count data rows (excluding header and trailer). | Exactly 2 data rows: `overdraft_fees` and `credit_interest_proxy`. |
| 2.2 | Run V2 for 2024-10-31. Count data rows (excluding header and trailer). | Exactly 4 data rows: 2 daily + 2 quarterly summary. |
| 2.3 | Run V1 for the same dates and compare row counts. | Row counts match V2 for both normal and Oct 31 dates. |
| 2.4 | Verify the trailer `{row_count}` token reflects the correct data row count. | Normal days: `TRAILER\|2\|{date}`. Oct 31: `TRAILER\|4\|{date}`. |

### TC-3: Data Content Equivalence

**Objective:** Verify V2 output data matches V1 for all effective dates.

| Step | Action | Expected Result |
|------|--------|-----------------|
| 3.1 | Run both V1 and V2 for 2024-10-01. Compare output files (ignoring the output path difference). | Data content is identical. |
| 3.2 | Run both V1 and V2 for 2024-10-31 (Oct 31 quarterly boundary). Compare output files. | Data content is identical, including the 2 QUARTERLY_TOTAL rows. |
| 3.3 | Run both V1 and V2 for a weekend date (e.g., 2024-10-05 Saturday). Compare output files. | Data content is identical. Both produce non-empty output on weekends. |
| 3.4 | Run Proofmark against the final V1 and V2 output files (last effective date's Overwrite output). | 100% match. |
| 3.5 | Verify `revenue_source` values: `"overdraft_fees"` and `"credit_interest_proxy"` on all dates. | Exact string match to V1. |
| 3.6 | Verify `revenue_source` values on Oct 31 include `"QUARTERLY_TOTAL_overdraft_fees"` and `"QUARTERLY_TOTAL_credit_interest_proxy"`. | Exact string match to V1 quarterly row labels. |
| 3.7 | Verify `total_revenue` values match V1 to 2 decimal places (banker's rounding). | Values are identical between V1 and V2. |
| 3.8 | Verify `transaction_count` values are integer counts matching V1. | Values are identical between V1 and V2. |
| 3.9 | Verify `as_of` column equals `__maxEffectiveDate` for all rows on a given date. | `as_of` matches the effective date, sourced from shared state (not data rows). |

**W-code notes:**
- W3c (quarterly boundary) affects Oct 31 output. Tested in TC-3.2 and TC-3.6.
- W5 (banker's rounding) affects `total_revenue`. Tested in TC-3.7.
- W9 (Overwrite mode) means only the last effective date's output survives. Tested in TC-4.

### TC-4: Writer Configuration

**Objective:** Verify all CsvFileWriter settings match V1.

| Step | Action | Expected Result |
|------|--------|-----------------|
| 4.1 | Inspect V2 config: `writeMode` field. | Value is `"Overwrite"`. |
| 4.2 | Inspect V2 config: `includeHeader` field. | Value is `true`. |
| 4.3 | Inspect V2 config: `lineEnding` field. | Value is `"LF"`. |
| 4.4 | Inspect V2 config: `trailerFormat` field. | Value is `"TRAILER\|{row_count}\|{date}"`. |
| 4.5 | Inspect V2 config: `outputFile` field. | Value is `"Output/double_secret_curated/monthly_revenue_breakdown.csv"`. |
| 4.6 | Verify output file uses LF line endings (not CRLF). | `od -c` or `file` command confirms LF-only. |
| 4.7 | Verify the trailer row is the last line of the file. | Last line matches `TRAILER\|{row_count}\|{date}` pattern. |
| 4.8 | Run V2 for 2 consecutive dates. Verify that only the second date's output survives (Overwrite mode). | File contains only the second date's data, header, and trailer. First date's output was overwritten. |

### TC-5: Anti-Pattern Elimination Verification

**Objective:** Confirm all identified AP-codes are eliminated in V2.

| Step | Action | Expected Result |
|------|--------|-----------------|
| 5.1 | **AP1 -- Dead-end sourcing.** Inspect V2 config for `customers` DataSourcing entry. | No `customers` DataSourcing entry exists. Only `overdraft_events` and `transactions` are sourced. |
| 5.2 | **AP1 verification.** Count DataSourcing modules in V2 config. | Exactly 2 (was 3 in V1). |
| 5.3 | **AP3 -- Unnecessary External module (partial).** Verify V2 uses a Transformation SQL step for aggregation instead of doing it in the External module. | V2 config includes a `Transformation` module with `resultName: "revenue_aggregates"` containing SQL with SUM/COUNT aggregation. |
| 5.4 | **AP3 verification.** Verify the V2 External module does NOT iterate over `overdraft_events` or `transactions` DataFrames directly. | External module reads only the pre-aggregated `revenue_aggregates` DataFrame (1 row, 4 columns). |
| 5.5 | **AP4 -- Unused columns.** Verify V2 `overdraft_events` DataSourcing sources only `fee_amount` and `fee_waived`. | No `overdraft_id`, `account_id`, or `customer_id` columns. |
| 5.6 | **AP4 verification.** Verify V2 `transactions` DataSourcing sources only `txn_type` and `amount`. | No `transaction_id` or `account_id` columns. |
| 5.7 | **AP6 -- Row-by-row iteration.** Verify V2 External module does not contain `foreach` loops over source data rows. | No `foreach` on `overdraft_events` or `transactions`. The External module processes only the single aggregated row. |
| 5.8 | **AP7 -- Magic values.** Verify the Oct 31 fiscal quarter boundary uses named constants. | Code uses `FiscalQ3EndMonth = 10` and `FiscalQ3EndDay = 31` (not inline literals). |
| 5.9 | **AP9 -- Misleading names.** Verify the misleading job name ("monthly" but actually daily) is documented. | FSD or V2 code contains a comment noting the naming discrepancy. |

### TC-6: Edge Cases

**Objective:** Verify correct behavior under edge conditions.

| Step | Action | Expected Result |
|------|--------|-----------------|
| 6.1 | **October 31 quarterly rows.** Run V2 for 2024-10-31. Verify exactly 4 data rows are produced. | Rows: `overdraft_fees`, `credit_interest_proxy`, `QUARTERLY_TOTAL_overdraft_fees`, `QUARTERLY_TOTAL_credit_interest_proxy`. |
| 6.2 | **Quarterly values duplicate daily.** On Oct 31 output, verify `QUARTERLY_TOTAL_overdraft_fees` has the same `total_revenue` and `transaction_count` as `overdraft_fees`. | Quarterly row values = daily row values (not accumulated quarter totals). |
| 6.3 | **Quarterly values duplicate daily (credit).** On Oct 31 output, verify `QUARTERLY_TOTAL_credit_interest_proxy` has the same `total_revenue` and `transaction_count` as `credit_interest_proxy`. | Same as 6.2 for credit. |
| 6.4 | **Weekend data.** Run V2 for a weekend date (e.g., 2024-10-05 Saturday). | Produces 2 data rows with non-zero revenue/counts (both tables have weekend data per BRD). |
| 6.5 | **Non-October-31 dates.** Run V2 for 2024-11-30 (end of November, not a fiscal quarter boundary). | Produces exactly 2 data rows. No quarterly summary rows appended. |
| 6.6 | **Overwrite obliterates prior output.** Run V2 for 2024-10-01, then run for 2024-10-02. | File contains only 2024-10-02's data. 2024-10-01's output is gone. |
| 6.7 | **Empty DataFrame fallback.** If `overdraft_events` or `transactions` returns 0 rows for a date, verify the External module defaults to 0 revenue and 0 count (BR-10). | Output still has 2 rows; revenue = 0.00, count = 0 for the empty source. (Note: unlikely in the 2024-10-01 to 2024-12-31 range, but code must handle it.) |
| 6.8 | **Trailer row count.** Verify the trailer `{row_count}` is 2 on normal days and 4 on Oct 31. | Trailer accurately reflects data row count. |
| 6.9 | **Trailer date.** Verify the trailer `{date}` matches the current effective date (not hardcoded). | Framework CsvFileWriter uses `__maxEffectiveDate` for `{date}` token. |
| 6.10 | **First effective date.** Verify V2 config `firstEffectiveDate` is `2024-10-01`. | Matches V1's `firstEffectiveDate`. |
| 6.11 | **as_of from shared state, not data rows.** Verify the `as_of` column value comes from `__maxEffectiveDate` per BR-9. | On any date, `as_of` = effective date. Confirm via code inspection that the External module reads `sharedState["__maxEffectiveDate"]`. |

### TC-7: Proofmark Configuration

**Objective:** Verify the Proofmark YAML config is correct for this job.

| Step | Action | Expected Result |
|------|--------|-----------------|
| 7.1 | Verify `comparison_target` is `"monthly_revenue_breakdown"`. | Correct. |
| 7.2 | Verify `reader` is `"csv"`. | Correct. |
| 7.3 | Verify `threshold` is `100.0`. | Correct -- strict full match required. |
| 7.4 | Verify `csv.header_rows` is `1`. | Correct -- one header row. |
| 7.5 | Verify `csv.trailer_rows` is `1`. | Correct -- Overwrite mode produces exactly one trailer at end of file (CONFIG_GUIDE Example 3 pattern). |
| 7.6 | Verify no `columns.excluded` entries exist. | Correct -- no non-deterministic fields (BRD: "None identified"). |
| 7.7 | Verify no `columns.fuzzy` entries exist. | Correct -- starting strict per best practices. Both V1 and V2 use `Math.Round` with `MidpointRounding.ToEven` in C#, so no epsilon divergence is expected. |
| 7.8 | Run Proofmark with this config against the last effective date's V1 and V2 output files. | Proofmark reports 100% match. |

## W-Code Test Cases

### TC-W3c: End-of-Quarter Boundary

**Objective:** Verify V2 correctly reproduces V1's October 31 fiscal quarter boundary behavior.

| Step | Action | Expected Result |
|------|--------|-----------------|
| W3c.1 | Run V2 for 2024-10-31. | Job completes successfully with 4 data rows. |
| W3c.2 | Verify output row order: (1) `overdraft_fees`, (2) `credit_interest_proxy`, (3) `QUARTERLY_TOTAL_overdraft_fees`, (4) `QUARTERLY_TOTAL_credit_interest_proxy`. | Row order matches V1 [MonthlyRevenueBreakdownBuilder.cs:53-94]. |
| W3c.3 | Verify `QUARTERLY_TOTAL_overdraft_fees.total_revenue` equals `overdraft_fees.total_revenue`. | Quarterly duplicates daily value -- not an accumulated quarter total. V1 evidence: [MonthlyRevenueBreakdownBuilder.cs:75] `decimal qOverdraftRevenue = overdraftRevenue;`. |
| W3c.4 | Verify `QUARTERLY_TOTAL_overdraft_fees.transaction_count` equals `overdraft_fees.transaction_count`. | Same as W3c.3 for count. |
| W3c.5 | Verify `QUARTERLY_TOTAL_credit_interest_proxy.total_revenue` equals `credit_interest_proxy.total_revenue`. | Quarterly duplicates daily value for credit as well. |
| W3c.6 | Verify `QUARTERLY_TOTAL_credit_interest_proxy.transaction_count` equals `credit_interest_proxy.transaction_count`. | Same as W3c.5 for count. |
| W3c.7 | Verify all 4 rows have the same `as_of` value (2024-10-31). | All rows share the effective date. |
| W3c.8 | Run V2 for 2024-10-30 (day before Q3 end). | Only 2 data rows. No quarterly summary appended. |
| W3c.9 | Run V2 for 2024-11-01 (day after Q3 end). | Only 2 data rows. No quarterly summary appended. |
| W3c.10 | Verify the V2 External module uses named constants (`FiscalQ3EndMonth`, `FiscalQ3EndDay`) for the boundary check instead of inline `10` and `31`. | Code review confirms AP7 compliance. |
| W3c.11 | Compare V2 Oct 31 output with V1 Oct 31 output. | Byte-for-byte identical data content. |

### TC-W5: Banker's Rounding

**Objective:** Verify V2 applies `MidpointRounding.ToEven` to revenue values, matching V1.

| Step | Action | Expected Result |
|------|--------|-----------------|
| W5.1 | Inspect V2 External module source code for `Math.Round` calls. | Code uses `Math.Round(value, 2, MidpointRounding.ToEven)` for both `overdraftRevenue` and `creditRevenue`. |
| W5.2 | Run V2 for 2024-10-01. Verify `total_revenue` values are rounded to exactly 2 decimal places. | All `total_revenue` values have exactly 2 decimal digits (e.g., `1234.56`, `0.00`). |
| W5.3 | Compare V2 `total_revenue` values with V1 for 5 sampled dates across the range. | All values match V1 exactly. |
| W5.4 | Verify rounding is applied AFTER aggregation (SUM), not to individual row values before summing. | Code review: SQL Transformation does `SUM()` first, then External module rounds the sum. Matches V1 pattern [MonthlyRevenueBreakdownBuilder.cs:30,46,58,64]. |
| W5.5 | Verify rounding is NOT done in the SQL Transformation step (SQLite ROUND uses wrong rounding mode). | SQL contains no `ROUND()` function. Raw sums are passed to the External module for banker's rounding. |

### TC-W9: Wrong writeMode (Overwrite)

**Objective:** Verify V2 reproduces V1's Overwrite behavior, where each run replaces the file and prior days' data is lost.

| Step | Action | Expected Result |
|------|--------|-----------------|
| W9.1 | Inspect V2 config `writeMode`. | Value is `"Overwrite"` -- matches V1. |
| W9.2 | Run V2 for 2024-10-01. Record the file content. | File contains: 1 header + 2 data rows + 1 trailer = 4 lines. |
| W9.3 | Run V2 for 2024-10-02. Record the file content. | File contains: 1 header + 2 data rows + 1 trailer = 4 lines. Data is for 2024-10-02 only. 2024-10-01 data is gone. |
| W9.4 | Verify the header is present after each Overwrite. | Overwrite mode always writes the header (file is recreated each time). |
| W9.5 | Run V2 for 2024-10-31. Verify file has 4 data rows (not 2). | File contains: 1 header + 4 data rows + 1 trailer = 6 lines. Oct 31 quarterly rows included. |

## Notes

- **Tier 2 justification:** The External module is needed for (1) banker's rounding via `MidpointRounding.ToEven` which is unavailable in SQLite, (2) `as_of` injection from `__maxEffectiveDate` shared state which is not accessible in SQL, and (3) conditional October 31 quarterly row generation. All filtering and aggregation is done in the SQL Transformation step.
- **SQLite REAL vs C# decimal (OQ-3):** The SQL Transformation aggregates using SQLite REAL (IEEE 754 double), while V1 accumulates with C# `decimal`. The V2 External module converts the double result back to `decimal` via `Convert.ToDecimal` before rounding. For simple sums of financial amounts, this should produce identical results. If Proofmark detects a mismatch, add a `fuzzy` tolerance on `total_revenue` with evidence, or escalate to Tier 3 by moving aggregation into the External module.
- **Empty DataFrame edge case (OQ-1):** If `overdraft_events` or `transactions` has zero rows for a date, the Transformation module will skip SQLite table creation, causing the SQL to fail. The V2 External module must handle a missing `revenue_aggregates` DataFrame by defaulting to 0 revenue and 0 count. This matches V1's null-check behavior [MonthlyRevenueBreakdownBuilder.cs:15-16,23,39].
- **Quarterly summary is a stub/bug (OQ-2):** The QUARTERLY_TOTAL rows duplicate the daily values instead of accumulating across the quarter. This is V1's behavior and V2 reproduces it exactly. The FSD documents this as a potential bug but preserves it for output equivalence.
- **Misleading job name (AP9):** The job is called "MonthlyRevenueBreakdown" but produces daily output with a quarterly event. Cannot rename per V2 rules. Documented in FSD.
- **Comparison notes:** Since V2 uses `writeMode: Overwrite`, Proofmark comparison should target the final effective date's output file (only the last run's data survives). Both V1 and V2 files will contain the same last date's data.
