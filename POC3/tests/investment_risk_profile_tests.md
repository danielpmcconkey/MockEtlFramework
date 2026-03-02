# InvestmentRiskProfile — V2 Test Plan

## Job Info
- **V2 Config**: `investment_risk_profile_v2.json`
- **Tier**: Tier 1 (Framework Only)
- **External Module**: None (V1 used `ExternalModules.InvestmentRiskClassifier`; V2 replaces with Transformation SQL)

## Pre-Conditions
- Data sources needed:
  - `datalake.investments` — columns: `investment_id`, `customer_id`, `account_type`, `current_value`, `risk_profile`, `as_of` (auto-appended by DataSourcing)
- V1 also sourced `datalake.customers` (AP1: dead-end sourcing — never used by External module), which V2 drops entirely
- Effective date range: `firstEffectiveDate` = `2024-10-01`, auto-advanced through `2024-12-31`
- V1 baseline output must exist at `Output/curated/investment_risk_profile.csv`
- V2 output writes to `Output/double_secret_curated/investment_risk_profile.csv`
- Investments table is weekday-only; weekend effective dates produce empty output

## Test Cases

### TC-1: Output Schema Validation
- **Expected columns** (exact order from FSD Section 4):
  1. `investment_id` (INTEGER — `CAST(investment_id AS INTEGER)`)
  2. `customer_id` (INTEGER — `CAST(customer_id AS INTEGER)`)
  3. `account_type` (TEXT — `COALESCE(account_type, '')`)
  4. `current_value` (NUMERIC — `COALESCE(current_value, 0)`)
  5. `risk_profile` (TEXT — `COALESCE(risk_profile, 'Unknown')`)
  6. `risk_tier` (TEXT — computed CASE expression on current_value thresholds)
  7. `as_of` (TEXT/DATE — passthrough from investments.as_of)
- Verify the header row is `investment_id,customer_id,account_type,current_value,risk_profile,risk_tier,as_of`
- Verify no extra columns are present (notably, no customer-related columns from the removed dead-end source)

### TC-2: Row Count Equivalence
- V1 vs V2 must produce identical row counts
- Row count equals the total number of investment rows within the effective date range — this is a 1:1 mapping (BR-1), no filtering is performed
- Every investment row produces exactly one output row, regardless of current_value or any other field

### TC-3: Data Content Equivalence
- All values must be byte-identical to V1 output
- Key comparison areas:
  - `investment_id` and `customer_id` are cast to INTEGER — must match V1's `Convert.ToInt32` behavior
  - `account_type` must be empty string (not NULL) when source is NULL (AP5 asymmetric NULLs)
  - `current_value` must be 0 (not NULL) when source is NULL (AP5 asymmetric NULLs)
  - `risk_profile` must be "Unknown" (not NULL, not empty) when source is NULL (AP5 asymmetric NULLs)
  - `risk_tier` must use the correct thresholds: > 200000 = "High Value", > 50000 = "Medium Value", else "Low Value"
  - `as_of` must come from each investment row's own `as_of` value, NOT from `__maxEffectiveDate` (BR-5)
- **CRITICAL: BRD threshold correction** — BRD BR-2 states the High Value threshold is `> 250000`. The FSD corrected this based on V1 source code: the actual threshold is `> 200000` [InvestmentRiskClassifier.cs:40]. V2 follows V1 ground truth (200000). Verify the V2 output matches V1 output, not the BRD text.
- **No W-codes affect this job** — byte-identical match is expected without any special handling
- Row ordering: V1 iterates rows in DataSourcing order (`ORDER BY as_of`). V2 SQL has no ORDER BY. Within a single as_of date, row order depends on SQLite internal order. Verify ordering matches V1 or confirm Proofmark handles unordered comparison.

### TC-4: Writer Configuration
- **includeHeader**: `true` — verify header row is present as first line
- **writeMode**: `Overwrite` — verify file is replaced on each run (only last effective date's data persists)
- **lineEnding**: `LF` — verify all line endings are `\n` (not `\r\n`)
- **trailerFormat**: Not specified — verify NO trailer row exists in the output file
- **encoding**: UTF-8 without BOM
- **outputFile**: `Output/double_secret_curated/investment_risk_profile.csv`

### TC-5: Anti-Pattern Elimination Verification
| AP-Code | What to Verify |
|---------|----------------|
| AP1 | V2 config does NOT contain a DataSourcing module for `customers`. V1 sourced `datalake.customers` (id, first_name, last_name) but the External module never referenced it. Confirm the `customers` DataSourcing entry is completely absent from V2 config. |
| AP3 | V2 config does NOT contain an External module. The chain is DataSourcing + Transformation + CsvFileWriter. The V1 External module `InvestmentRiskClassifier` is entirely replaced by SQL. |
| AP4 | V2 does not source the customers table at all (subsumed by AP1 fix). The investments DataSourcing sources only the 5 needed columns: `investment_id`, `customer_id`, `account_type`, `current_value`, `risk_profile`. |
| AP6 | No row-by-row iteration exists. The V1 `foreach (var row in investments.Rows)` loop is replaced by a single set-based SQL SELECT. |
| AP7 | V2 SQL documents the magic value thresholds (200000, 50000) with comments explaining their business meaning. The values themselves are unchanged (output equivalence), but they are no longer unexplained magic numbers. |

### TC-6: Edge Cases
1. **NULL current_value**: Must default to 0 via `COALESCE(current_value, 0)`. A NULL current_value results in risk_tier = "Low Value" because 0 <= 50000. Verify this matches V1's behavior [InvestmentRiskClassifier.cs:32-34].
2. **NULL risk_profile**: Must default to "Unknown" via `COALESCE(risk_profile, 'Unknown')`. This is asymmetric with current_value (which defaults to 0) and account_type (which defaults to ""). Verify "Unknown" appears in output, not NULL or empty string.
3. **NULL account_type**: Must default to empty string via `COALESCE(account_type, '')`. This is the third asymmetric NULL treatment. Verify empty string appears in output, not NULL or "Unknown".
4. **Boundary: current_value = 200000**: Must be classified as "Medium Value", NOT "High Value". The threshold is strictly greater than (`> 200000`), so 200000 itself falls into the Medium Value range.
5. **Boundary: current_value = 50000**: Must be classified as "Low Value", NOT "Medium Value". The threshold is strictly greater than (`> 50000`), so 50000 itself falls into the Low Value range.
6. **Negative current_value**: Must be classified as "Low Value" (negative < 50000). No validation or special handling for negative values exists in V1.
7. **Empty investments table**: SQL returns zero rows. CsvFileWriter writes header-only file. V1 returns empty DataFrame early [InvestmentRiskClassifier.cs:18-22], then framework CsvFileWriter writes header-only file. Both should match.
8. **Weekend effective dates**: Investments table has no weekend data in the datalake. DataSourcing returns empty DataFrame, SQL returns zero rows, output is header-only CSV. Both V1 and V2 produce the same empty result.
9. **Multi-day auto-advance with Overwrite mode**: Since writeMode is Overwrite, each day's run replaces the file. Only the final effective date's output persists. Verify the final output matches V1's final output.
10. **risk_tier vs risk_profile independence**: The `risk_tier` column is computed from `current_value` only; it must NOT be influenced by the `risk_profile` column. An investment with `risk_profile = "Aggressive"` and `current_value = 100` must still get `risk_tier = "Low Value"`.

### TC-7: Proofmark Configuration
- **Expected proofmark settings** (from FSD Section 8):
  ```yaml
  comparison_target: "investment_risk_profile"
  reader: csv
  threshold: 100.0
  csv:
    header_rows: 1
    trailer_rows: 0
  ```
- **Threshold**: 100.0 (all values must be a perfect match)
- **Excluded columns**: None (all columns are deterministic; no runtime timestamps or UUIDs)
- **Fuzzy columns**: None (no floating-point accumulation; `current_value` is a passthrough via COALESCE, not accumulated or computed)

## W-Code Test Cases

No W-codes apply to this job. The FSD explicitly analyzed and ruled out all W-codes:

| W-Code | Why Not Applicable |
|--------|-------------------|
| W1/W2 | No special weekend logic. Empty output on weekends is natural (no data in datalake). |
| W3a/W3b/W3c | No boundary summary rows (weekly, monthly, or quarterly). |
| W4 | No integer division or percentage calculations. |
| W5 | No rounding operations. |
| W6 | No floating-point accumulation. `current_value` is a passthrough. |
| W7/W8 | No trailer in this job. |
| W9 | Overwrite mode is correct and intentional for this job. Not a wrong-write-mode bug. |
| W10 | CSV output, not Parquet. numParts is not applicable. |
| W12 | Overwrite mode, not Append. No repeated headers. |

Since no W-codes apply, there are no TC-W test cases for this job. The absence of wrinkles means V2 output should be straightforwardly byte-identical to V1.

## Notes
- **BRD threshold discrepancy is the highest-risk item** for this job. BRD BR-2 says the High Value threshold is `> 250000`, but V1 source code uses `> 200000`. The FSD follows V1 ground truth. If someone implements from the BRD alone (without reading the FSD correction), they will produce WRONG output. The test plan must verify against V1 actual output, which uses the 200000 threshold.
- **Asymmetric NULL handling (AP5)** is the second-highest-risk item. Three columns have three different NULL defaults: `current_value -> 0`, `risk_profile -> "Unknown"`, `account_type -> ""`. Getting any one of these wrong will cause byte-level mismatches.
- **Row ordering**: V1 does not explicitly sort. Row order depends on DataSourcing's iteration order (`ORDER BY as_of`). V2's SQL has no ORDER BY. If SQLite returns rows in a different order, Proofmark comparison may fail on ordering even though data content is identical. If this occurs, add an ORDER BY clause to the V2 SQL or configure Proofmark for order-independent comparison.
- **Output path difference**: The only intentional V1-vs-V2 difference is the directory (`curated` vs `double_secret_curated`). Proofmark handles this via `comparison_target` mapping.
- **Dead-end customers source**: The V1 config sources `datalake.customers` but the External module never references `sharedState["customers"]`. V2 correctly removes this. If someone adds it back "for safety," it wastes resources but does not affect output.
