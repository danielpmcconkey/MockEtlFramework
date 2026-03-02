# HighRiskMerchantActivity — V2 Test Plan

## Job Info
- **V2 Config**: `high_risk_merchant_activity_v2.json`
- **Tier**: 1 (Framework Only: DataSourcing -> Transformation -> CsvFileWriter)
- **External Module**: None (V1 External `HighRiskMerchantActivityProcessor` replaced by SQL Transformation)

## Pre-Conditions
- **Data sources required:**
  - `datalake.card_transactions` — columns: `card_txn_id`, `merchant_name`, `merchant_category_code`, `amount`, `txn_timestamp` (plus framework-injected `as_of`)
  - `datalake.merchant_categories` — columns: `mcc_code`, `mcc_description`, `risk_level` (plus framework-injected `as_of`)
- **Expected schemas:**
  - `card_transactions`: card_txn_id (integer), card_id (integer), customer_id (integer), merchant_name (varchar), merchant_category_code (integer), amount (numeric), txn_timestamp (timestamp), authorization_status (varchar), as_of (date)
  - `merchant_categories`: mcc_code (integer), mcc_description (varchar), risk_level (varchar), as_of (date)
- **Effective date range:** `firstEffectiveDate` is `2024-10-01`. Framework auto-advance controls the date range. DataSourcing injects `__minEffectiveDate` / `__maxEffectiveDate` to filter both tables by `as_of`.
- **Critical data note:** High-risk MCC codes (5094/Precious Metals, 7995/Gambling) do NOT appear in `card_transactions.merchant_category_code`. The 17 distinct MCCs in card_transactions are: {4511, 4814, 5200, 5311, 5411, 5541, 5691, 5732, 5812, 5814, 5912, 5942, 5944, 5999, 7011, 7832, 8011}. Output is expected to be empty (header only, zero data rows).

## Test Cases

### TC-1: Output Schema Validation
- **Traces to:** FSD Section 4
- **Expected columns (exact order):**
  1. `card_txn_id` — pass-through from card_transactions
  2. `merchant_name` — pass-through from card_transactions
  3. `mcc_code` — renamed from `merchant_category_code` in card_transactions
  4. `mcc_description` — joined from merchant_categories via mcc_code
  5. `amount` — pass-through from card_transactions (no rounding)
  6. `txn_timestamp` — pass-through from card_transactions
  7. `as_of` — pass-through from card_transactions (injected by DataSourcing)
- **Expected types:** All pass-through from source types; `mcc_code` is the integer `merchant_category_code` renamed in the SELECT clause
- **Verification method:** Read the header line of the output CSV. Confirm column count is exactly 7. Confirm column names and order match exactly. Compare V2 header against V1 header byte-for-byte.
- **Key exclusions:** `card_id`, `customer_id` (sourced in V1 but never output per BR-10), `risk_level` (used for filtering but excluded from output per BR-11)

### TC-2: Row Count Equivalence
- **Traces to:** BRD, FSD Appendix B
- **V1 vs V2 must produce identical row counts.**
- **Expected behavior:** Both V1 and V2 produce zero data rows. The high-risk MCC codes (5094, 7995) do not appear in `card_transactions`, so the INNER JOIN + WHERE filter produces an empty result set. The output CSV contains only the header row.
- **Verification method:** Count data rows in V1 output (`Output/curated/high_risk_merchant_activity.csv`) and V2 output (`Output/double_secret_curated/high_risk_merchant_activity.csv`). Both must be 0. Proofmark comparison handles this automatically.

### TC-3: Data Content Equivalence
- **Traces to:** FSD Section 5 (SQL Design)
- **All values must be byte-identical to V1 output.**
- **W-codes affecting comparison:** W9 (Overwrite mode) is the only wrinkle; it does not affect data content, only file lifecycle.
- **Expected behavior:** Since both V1 and V2 produce zero data rows, content equivalence is trivially satisfied. Both files should contain exactly the header row and nothing else.
- **Verification method:** Run Proofmark with `threshold: 100.0` (no tolerance). Both files should be byte-identical. Use `diff` or `cmp` as a secondary check.
- **Potential divergence scenarios (if data changes in future):**
  - **MCC dictionary overwrite vs as_of JOIN:** V1 iterates all `merchant_categories` rows and overwrites dictionary entries per `mcc_code` (last-seen wins). V2 uses `AND ct.as_of = mc.as_of` to join within the same snapshot date. If `mcc_description` or `risk_level` for a high-risk MCC code differs across snapshots, results could diverge. See TC-W1 for details.
  - **Row ordering:** V1 iterates `cardTransactions.Rows` in DataSourcing order (by `as_of`). V2 SQL has no ORDER BY. If data rows existed, ordering could differ. See TC-6 edge case 3.

### TC-4: Writer Configuration
- **Traces to:** FSD Section 7, BRD Writer Configuration
- **Verify all writer settings match V1:**

| Property | Expected Value | V1 Match |
|----------|---------------|----------|
| type | CsvFileWriter | YES |
| source | `output` | YES |
| outputFile | `Output/double_secret_curated/high_risk_merchant_activity.csv` | Path updated per V2 convention |
| includeHeader | `true` | YES |
| writeMode | `Overwrite` | YES |
| lineEnding | `LF` | YES |
| trailerFormat | (not configured) | YES — no trailer in V1 |

- **Verification method:**
  - Read `high_risk_merchant_activity_v2.json` and confirm all writer properties match the table above.
  - Verify output file has LF line endings (no `\r\n` sequences). Use `xxd` or `od` to inspect.
  - Run the job twice for different effective dates. Confirm the output file contains only the second run's data (Overwrite behavior).
  - Confirm no trailer row exists at the end of the file.

### TC-5: Anti-Pattern Elimination Verification
- **Traces to:** FSD Section 3

| AP-Code | Anti-Pattern | What to Verify |
|---------|-------------|----------------|
| AP3 | Unnecessary External | V2 config has NO External module. Module chain is DataSourcing -> DataSourcing -> Transformation -> CsvFileWriter. The V1 `HighRiskMerchantActivityProcessor.cs` is no longer referenced. |
| AP4 | Unused columns | V2 DataSourcing for `card_transactions` does NOT source `card_id` or `customer_id`. Confirm the columns list in V2 config is exactly: `["card_txn_id", "merchant_name", "merchant_category_code", "amount", "txn_timestamp"]`. |
| AP6 | Row-by-row iteration | V2 uses SQL INNER JOIN instead of C# foreach + dictionary lookup. Verify the Transformation module uses a SQL statement, not procedural code. |
| AP7 | Magic values | The `"High"` string literal in V2 SQL has a descriptive comment. Verify the SQL contains a comment explaining the filter: `-- V1 filter` or equivalent. (Note: in JSON config, inline SQL comments may not be present; verify the FSD documents the magic value.) |

- **Verification method:** Read `high_risk_merchant_activity_v2.json`. Confirm module chain structure. Confirm no External module entry. Confirm column lists match AP4 elimination.

### TC-6: Edge Cases
- **Traces to:** BRD Edge Cases, FSD Appendix B

1. **Empty output (current data):** High-risk MCC codes (5094, 7995) do not appear in `card_transactions`. Both V1 and V2 produce header-only output. Verify the header-only file is a valid CSV (single line, correct column names, LF terminated). Proofmark must handle zero-data-row comparison gracefully (exit code 0, not exit code 2).

2. **MCC code not in merchant_categories:** If a transaction has a `merchant_category_code` with no matching `mcc_code` in `merchant_categories`, it is excluded by the INNER JOIN (V2) or by the `continue` on missing dictionary key (V1). Per BRD, all 17 transaction MCCs are a subset of the 20 category MCCs, so this is currently moot. Verify the INNER JOIN handles this correctly if data changes.

3. **Row ordering (future concern):** V1 iterates rows in DataSourcing order (by `as_of`). V2 SQL has no ORDER BY clause. If the output ever contains data rows, ordering could differ. FSD notes that adding `ORDER BY ct.as_of, ct.card_txn_id` is a safe fix if Proofmark detects ordering differences. Currently not testable since output is empty.

4. **No amount rounding (BR-8):** The `amount` field passes through without any ROUND() call. Verify the SQL SELECT clause uses `ct.amount` directly, not `ROUND(ct.amount, 2)`. This differs from CardFraudFlags which applies Banker's rounding.

5. **No amount threshold (BR-6):** Unlike CardFraudFlags ($500 threshold), this job includes ALL transactions at high-risk merchants regardless of amount. Verify no amount filter exists in the SQL WHERE clause.

6. **No authorization_status filter (BRD Edge Case 4):** The job does not source or filter by `authorization_status`. Both approved and declined transactions at high-risk merchants would be included. V2 does not source `authorization_status` at all, which is correct.

7. **No weekend fallback (BR-7):** All transaction dates including weekends are processed. No date manipulation logic exists in V2's SQL.

### TC-7: Proofmark Configuration
- **Traces to:** FSD Section 8
- **Expected proofmark settings:**

```yaml
comparison_target: "high_risk_merchant_activity"
reader: csv
threshold: 100.0
csv:
  header_rows: 1
  trailer_rows: 0
```

| Setting | Expected Value | Rationale |
|---------|---------------|-----------|
| comparison_target | `high_risk_merchant_activity` | Matches V1 output filename (without extension) |
| reader | `csv` | Output is CSV via CsvFileWriter |
| threshold | `100.0` | All fields are deterministic; byte-identical match required |
| header_rows | `1` | `includeHeader: true` in writer config |
| trailer_rows | `0` | No trailer configured |
| EXCLUDED columns | None | No non-deterministic fields |
| FUZZY columns | None | No rounding or floating-point arithmetic (amount is pass-through per BR-8) |

- **Empty output consideration:** Both V1 and V2 produce header-only files. Proofmark must compare two identical header-only files as PASS. If Proofmark errors on zero-data-row files (exit code 2), this is a Proofmark config issue, not a data mismatch.

## W-Code Test Cases

### TC-W1: W9 — Wrong writeMode (Overwrite)
- **Traces to:** FSD Section 3 (W9), BRD Write Mode Implications
- **What the wrinkle is:** V1 uses `Overwrite` mode. For multi-day auto-advance runs, each effective date's run replaces the previous day's output. Only the final day's data survives in the file.
- **How V2 handles it:** V2 uses `writeMode: "Overwrite"` in CsvFileWriter, exactly matching V1 behavior.
- **What to verify:**
  1. Run the job for effective date 2024-10-01. Verify the output file exists.
  2. Run the job for effective date 2024-10-02. Verify the output file contains ONLY 2024-10-02's data (not appended to 2024-10-01's data).
  3. Confirm the V2 config has `"writeMode": "Overwrite"`.
- **Note:** Since output is currently empty (header only), both runs produce identical header-only files. The overwrite behavior is still correct but not distinguishable from append in this case.

### TC-W2: Potential Divergence — as_of JOIN Condition
- **Traces to:** FSD Appendix C, FSD SQL Design Note 2
- **What the wrinkle is:** This is not a formal W-code but a documented risk. V1 builds a dictionary from ALL `merchant_categories` rows across all `as_of` dates, overwriting per `mcc_code` (last-seen wins). V2 uses `AND ct.as_of = mc.as_of` to join within the same snapshot date.
- **How V2 handles it:** V2 uses per-snapshot-date joining, which is semantically cleaner. For reference data where `mcc_description` and `risk_level` are consistent per `mcc_code` across snapshots, this produces identical results.
- **What to verify:**
  1. Query: `SELECT mcc_code, COUNT(DISTINCT risk_level), COUNT(DISTINCT mcc_description) FROM datalake.merchant_categories GROUP BY mcc_code HAVING COUNT(DISTINCT risk_level) > 1 OR COUNT(DISTINCT mcc_description) > 1`. Result should be empty (no variation across snapshots for any MCC code).
  2. If any MCC codes have varying attributes, verify whether they are high-risk codes (5094, 7995). If so, flag as a Proofmark mismatch risk.
  3. Since output is currently empty (no transactions at high-risk MCCs), this risk is academic for the current dataset.

## Notes

1. **This job produces empty output.** The most important verification is that both V1 and V2 produce identical empty output (header-only CSV). All data-content tests (TC-2, TC-3) are trivially satisfied because there are zero data rows to compare.

2. **Proofmark zero-row handling.** If Proofmark does not gracefully handle the comparison of two header-only files, this must be resolved as a Proofmark configuration issue (not a V2 bug). Expected behavior: PASS with 0 rows compared.

3. **Future data sensitivity.** If future transaction data includes MCC codes 5094 or 7995, the job would produce non-empty output. At that point, TC-W2 (as_of JOIN condition) and TC-6 edge case 3 (row ordering) would become relevant and should be re-evaluated.

4. **Column rename.** The SQL uses `ct.merchant_category_code AS mcc_code` to rename the column. This must produce a header of `mcc_code` (not `merchant_category_code`) in the output CSV. Verify the header reflects the alias, not the source column name.

5. **AP4 column reduction.** V1 sources 7 columns from `card_transactions` (including `card_id`, `customer_id`). V2 sources only 5 columns. This is a code-quality improvement that does not affect output (the removed columns were never in the output schema).
