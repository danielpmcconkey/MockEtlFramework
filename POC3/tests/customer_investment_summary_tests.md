# CustomerInvestmentSummary — V2 Test Plan

## Job Info
- **V2 Config**: `customer_investment_summary_v2.json`
- **Tier**: Tier 1 — Framework Only (DataSourcing -> Transformation -> CsvFileWriter)
- **External Module**: None (V1 used `CustomerInvestmentSummaryBuilder.cs`; eliminated as AP3)

## Pre-Conditions
- Data sources needed:
  - `datalake.investments` — columns: `customer_id`, `current_value`, `as_of`
  - `datalake.customers` — columns: `id`, `first_name`, `last_name`, `as_of`
- V1 also sources `datalake.securities` (dead-end sourcing, AP1) and additional unused columns from investments (`investment_id`, `account_type`, `advisor_id`) and customers (`birthdate`) — all eliminated in V2 (AP1, AP4)
- Effective date range: starts at `2024-10-01`, framework injects `__minEffectiveDate` / `__maxEffectiveDate`
- Both investments and customers tables are weekday-only in the datalake

## Test Cases

### TC-1: Output Schema Validation
- **Expected columns (exact order per FSD Section 4):**
  1. `customer_id` — INTEGER
  2. `first_name` — TEXT
  3. `last_name` — TEXT
  4. `investment_count` — INTEGER
  5. `total_value` — REAL
  6. `as_of` — TEXT
- **Verification:** Read V2 CSV output and confirm column names from header row, order, and data types match
- **Pass criteria:** All 6 columns present in exact order; no extra columns; header row matches V1 header exactly

### TC-2: Row Count Equivalence
- Run V1 (`CustomerInvestmentSummary`) and V2 (`CustomerInvestmentSummaryV2`) for the same effective date range
- Compare row counts from V1 output (`Output/curated/customer_investment_summary.csv`) vs V2 output (`Output/double_secret_curated/customer_investment_summary.csv`)
- Row count should exclude the header row (1 header row in both V1 and V2)
- **Pass criteria:** Identical data row counts for every effective date in the range
- **Note:** On weekend dates, both V1 and V2 should produce empty output (header-only CSV) since both investments and customers tables lack weekend data

### TC-3: Data Content Equivalence
- All values must be byte-identical to V1 output
- **W5 (Banker's rounding)** applies to `total_value`: V1 uses `Math.Round(totalValue, 2, MidpointRounding.ToEven)`, V2 uses SQLite `ROUND(SUM(current_value), 2)` which also implements Banker's rounding. Values should match exactly.
- **as_of column:** V1 reads from `__maxEffectiveDate` shared state; V2 uses `MAX(i.as_of)` from the data. For daily auto-advance (single-day runs), these are equivalent. For multi-day ranges, `MAX(i.as_of)` equals `__maxEffectiveDate`.
- **Row ordering concern (BR-6):** V1 iterates dictionary insertion order (first-encountered customer_id in investments data). V2 uses `ORDER BY i.customer_id` (ascending integer sort). These match if investments are naturally ordered by ascending customer_id. If Proofmark fails on row order, investigate V1's actual ordering behavior.
- **Pass criteria:** Proofmark comparison at 100% threshold passes

### TC-4: Writer Configuration
- **Writer type:** CsvFileWriter
- **Expected settings:**
  - `source`: `output`
  - `outputFile`: `Output/double_secret_curated/customer_investment_summary.csv` (V2 path; V1 was `Output/curated/customer_investment_summary.csv`)
  - `includeHeader`: `true`
  - `writeMode`: `Overwrite`
  - `lineEnding`: `LF`
  - `trailerFormat`: not specified (no trailer)
- **Verification:** Confirm V2 config JSON matches these settings exactly
- **Pass criteria:** All writer parameters match; output CSV has exactly 1 header row, LF line endings, no trailer row

### TC-5: Anti-Pattern Elimination Verification

| AP-Code | What to Verify | Pass Criteria |
|---------|---------------|---------------|
| AP1 | V2 config does NOT source `datalake.securities` | No DataSourcing entry for `securities` table in `customer_investment_summary_v2.json` |
| AP3 | V2 config contains NO External module entry | No `"type": "External"` in V2 config; all logic expressed in SQL Transformation |
| AP4 | V2 investments DataSourcing excludes `investment_id`, `account_type`, `advisor_id`; V2 customers DataSourcing excludes `birthdate` | Column lists: investments = `["customer_id", "current_value"]`; customers = `["id", "first_name", "last_name"]` |
| AP6 | No row-by-row iteration; logic is set-based SQL | V2 uses a single Transformation module with SQL using GROUP BY, LEFT JOIN, COALESCE, ROUND instead of C# foreach loops and dictionary accumulation |

### TC-6: Edge Cases

| Edge Case | Description | Expected Behavior | Source |
|-----------|-------------|-------------------|--------|
| Empty investments input | No investment rows for effective date (e.g., weekend) | V1: empty DataFrame with schema. V2: SQL may fail on missing `investments` table (empty DataFrames are not registered by Transformation). Overwrite mode means the output file is replaced, so previous day's data is cleared. | BR-2; FSD Section 5 Note 6 |
| Customer with no investments | Customer exists in `customers` but has no rows in `investments` | Customer does NOT appear in output. GROUP BY iterates investments only, so customers without investments are excluded. | BRD Edge Case 1 |
| Investment with no matching customer | `investments.customer_id` has no match in `customers.id` | Investment IS included in output. LEFT JOIN produces NULLs for name fields; COALESCE maps to empty strings. | BR-4; BRD Edge Case 2 |
| Cross-date aggregation | Effective date range spans multiple days | All investment rows across all dates in range are aggregated together per customer_id. This may inflate counts/totals if the same investment appears on multiple dates. This is V1 behavior and is intentionally replicated. | BRD Edge Case 3 |
| NULL current_value | Investment row has NULL `current_value` | `SUM()` in SQL ignores NULLs. `Convert.ToDecimal(null)` in V1 would throw. If NULL values exist in data, V2 may produce different behavior than V1. Unlikely scenario per BRD (MEDIUM confidence). | BRD Edge Case 4 |
| Weekend dates | Both investments and customers tables lack weekend data | Empty output expected from both V1 and V2 | BRD Edge Case 6 |
| Single investment per customer | Customer has exactly 1 investment row | `investment_count` = 1, `total_value` = that row's `current_value` (rounded to 2dp) | BR-1 |
| Large total_value | Sum exceeds typical decimal precision | V1 uses `decimal` accumulation (no epsilon issues per W6 assessment). V2 uses SQLite REAL (double precision). For typical financial values, no precision loss expected. Monitor for discrepancies. | FSD Section 3 (W6 not applicable) |

### TC-7: Proofmark Configuration
- **Expected Proofmark settings (FSD Section 8):**
  ```yaml
  comparison_target: "customer_investment_summary"
  reader: csv
  threshold: 100.0
  csv:
    header_rows: 1
    trailer_rows: 0
  ```
- **Threshold:** 100.0 (full strict match required)
- **Excluded columns:** None — all columns are deterministic
- **Fuzzy columns:** None — `total_value` uses Banker's rounding in both V1 and V2; should produce identical values

## W-Code Test Cases

### TC-W1: W5 — Banker's Rounding
- **What the wrinkle is:** V1 uses `Math.Round(totalValue, 2, MidpointRounding.ToEven)` for `total_value`. This is Banker's rounding (round-half-to-even), where values exactly at the midpoint (e.g., 2.345) round to the nearest even digit (2.34, not 2.35).
- **How V2 handles it:** SQLite's built-in `ROUND()` function uses Banker's rounding by default. The SQL `ROUND(SUM(i.current_value), 2)` replicates V1's behavior.
- **What to verify:**
  1. Find or construct test cases where `SUM(current_value)` produces a midpoint value (e.g., exactly X.XX5)
  2. Confirm V2 output rounds to the even digit (e.g., 100.125 -> 100.12, 100.135 -> 100.14)
  3. Compare V1 and V2 `total_value` for all customers across all effective dates
  4. **Pass criteria:** `total_value` is byte-identical between V1 and V2 for every row
- **Risk:** V1 accumulates using `decimal` (high precision), V2 accumulates using SQLite REAL (`double`, ~15-17 significant digits). For most financial values this is sufficient, but if intermediate sums hit floating-point edge cases, the rounding result could differ. Monitor Proofmark results closely on this column.

## Notes

1. **Row ordering risk:** This is the primary risk for Proofmark comparison. V1's output order depends on dictionary insertion order (order of first customer_id encounter in investments data). V2 uses `ORDER BY i.customer_id` (ascending integer sort). If the investments data is naturally ordered by customer_id within each effective date, these will match. If not, Proofmark will detect row mismatches. Resolution options: (a) adjust V2's ORDER BY to match V1's natural ordering, (b) verify Proofmark handles row-order-insensitive comparison for CSV.

2. **Decimal vs REAL precision:** V1 uses C# `decimal` (128-bit, 28-29 significant digits) for monetary accumulation. V2 uses SQLite REAL (`double`, 64-bit, 15-17 significant digits). For typical investment portfolio values, this should not cause precision differences after rounding to 2 decimal places. However, if a customer has many high-value investments and the sum exceeds ~10^15, floating-point precision could affect the 2nd decimal place. This is unlikely for realistic data but should be monitored.

3. **Securities table elimination:** V1 sources `datalake.securities` (5 columns) but the External module never references it. V2 removes this entirely (AP1). This has zero impact on output but reduces unnecessary database reads. Verify the V2 config has no securities DataSourcing entry.

4. **Cross-date aggregation behavior:** When the effective date range spans multiple days, V1 aggregates ALL investment rows across ALL dates per customer_id. This means a customer's investment appearing on 5 dates would be counted 5 times, inflating both `investment_count` and `total_value`. V2 replicates this behavior exactly (no date filtering in the GROUP BY). This is a latent V1 behavior (possibly a bug), not a V2 defect.

5. **Open questions from BRD:**
   - BRD Question 1: Why are securities sourced? Confirmed dead-end sourcing (AP1). Eliminated in V2.
   - BRD Question 2: Why is birthdate sourced? Confirmed unused column (AP4). Eliminated in V2.
   - BRD Question 3: Cross-date inflation is a latent V1 behavior. V2 replicates it for output equivalence.
