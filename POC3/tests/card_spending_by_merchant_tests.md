# CardSpendingByMerchant — Test Plan

## Traceability

| Test ID | BRD Requirement | Description |
|---------|----------------|-------------|
| TC-01   | BR-1           | Transactions grouped by merchant_category_code — one row per MCC |
| TC-02   | BR-2           | txn_count is COUNT of transactions per MCC |
| TC-03   | BR-3           | total_spending is SUM of amount per MCC |
| TC-04   | BR-4           | MCC description lookup with empty string fallback for unknown codes |
| TC-05   | BR-5           | as_of value taken from first row of card_transactions |
| TC-06   | BR-6           | No authorization_status filter — Approved and Declined both included |
| TC-07   | BR-7           | risk_level is NOT in the output schema |
| TC-08   | BR-8           | No weekend fallback — weekend transactions aggregated normally |
| TC-09   | BR-9           | Empty card_transactions input produces zero data rows |
| TC-10   | BR-10          | Unused sourced columns eliminated in V2 (AP4) |
| TC-11   | Writer Config  | Parquet output: numParts=1, Overwrite mode, correct directory |
| TC-12   | BR-5, Edge Case 1 | as_of consistency — all output rows share same as_of value |
| TC-13   | BR-4, Edge Case 2 | MCC codes not in merchant_categories get empty description |
| TC-14   | Edge Case 3    | Merchant category duplicates across as_of dates |
| TC-15   | Edge Case 4    | Declined transactions included in spending totals |
| TC-16   | FSD Proofmark  | Proofmark config correctness — strict matching, no exclusions |
| TC-17   | FSD SQL        | Column order matches V1 output schema exactly |
| TC-18   | BR-1           | Zero-row scenario — no transactions for a given date |
| TC-19   | BR-3           | total_spending precision — decimal vs double accumulation |
| TC-20   | FSD SQL Note 6 | Row ordering — V2 ORDER BY vs V1 Dictionary iteration order |

## Test Cases

### TC-01: Group By Merchant Category Code
- **Traces to:** BR-1
- **Input conditions:** card_transactions data with multiple transactions across several distinct merchant_category_code values.
- **Expected output:** Exactly one output row per distinct merchant_category_code. The number of output rows equals the number of distinct MCC codes present in the input transactions.
- **Verification method:** Count distinct merchant_category_code values in the source data for the date range. Compare against the row count of the output. Verify no duplicate mcc_code values exist in the output. Cross-check with V1 output via Proofmark.

### TC-02: Transaction Count Per MCC
- **Traces to:** BR-2
- **Input conditions:** card_transactions with known counts per MCC. For example, MCC 5411 has 150 transactions for a given date.
- **Expected output:** The `txn_count` column for MCC 5411 equals 150. Every MCC's txn_count matches the raw count of transactions for that MCC in the source data.
- **Verification method:** For each MCC in the output, run `SELECT merchant_category_code, COUNT(*) FROM datalake.card_transactions WHERE as_of = '{date}' GROUP BY merchant_category_code` and compare the counts against the output's txn_count values. Both V1 and V2 should produce identical counts.

### TC-03: Total Spending Per MCC
- **Traces to:** BR-3
- **Input conditions:** card_transactions with known amounts per MCC.
- **Expected output:** The `total_spending` column for each MCC equals the sum of all `amount` values for transactions with that MCC in the source data.
- **Verification method:** For each MCC in the output, run `SELECT merchant_category_code, SUM(amount) FROM datalake.card_transactions WHERE as_of = '{date}' GROUP BY merchant_category_code` and compare sums against the output's total_spending values. Note the FSD identifies a potential precision concern (V1 uses decimal accumulation, V2 uses SQLite REAL/double) — verify values match exactly or within acceptable epsilon.

### TC-04: MCC Description Lookup
- **Traces to:** BR-4
- **Input conditions:** Output rows with mcc_code values that exist in merchant_categories, plus (theoretically) MCC codes that don't exist in the lookup.
- **Expected output:** For MCC codes present in merchant_categories, the `mcc_description` column contains the corresponding description string. For MCC codes NOT in merchant_categories, `mcc_description` is an empty string.
- **Verification method:** Join the output mcc_code values against `datalake.merchant_categories` and verify descriptions match. Per BRD, all 17 transaction MCCs are in the categories table, so the empty-string fallback is a defensive check. Verify via `SELECT DISTINCT ct.merchant_category_code FROM datalake.card_transactions ct LEFT JOIN datalake.merchant_categories mc ON ct.merchant_category_code = mc.mcc_code WHERE mc.mcc_code IS NULL` — expect zero rows.

### TC-05: as_of From First Row
- **Traces to:** BR-5
- **Input conditions:** DataSourcing returns card_transactions ordered by as_of. The first row's as_of value is the minimum date in the effective date range.
- **Expected output:** All output rows have the same `as_of` value, equal to the minimum as_of date present in the card_transactions data for the effective date range. For single-day runs (minDate == maxDate), this is that single date.
- **Verification method:** Read the output and verify all rows share a single as_of value. For a single-day run, confirm it equals the run date. For a multi-day range (if applicable), confirm it equals `SELECT MIN(as_of) FROM datalake.card_transactions WHERE as_of BETWEEN '{minDate}' AND '{maxDate}'`.

### TC-06: No Authorization Status Filter
- **Traces to:** BR-6
- **Input conditions:** card_transactions containing both 'Approved' and 'Declined' transactions.
- **Expected output:** Both Approved and Declined transactions contribute to txn_count and total_spending for their respective MCC codes. No transactions are excluded based on authorization_status.
- **Verification method:** Query `SELECT merchant_category_code, COUNT(*) FROM datalake.card_transactions WHERE as_of = '{date}' GROUP BY merchant_category_code` (without any authorization_status filter) and verify the counts match the output's txn_count. If a filter were incorrectly applied, the counts would be lower.

### TC-07: risk_level Not in Output
- **Traces to:** BR-7
- **Input conditions:** Standard job run.
- **Expected output:** The output Parquet file schema contains exactly these columns: `mcc_code, mcc_description, txn_count, total_spending, as_of`. The column `risk_level` is absent.
- **Verification method:** Read the output Parquet schema and confirm risk_level is not present. Confirm the column count is exactly 5.

### TC-08: No Weekend Fallback
- **Traces to:** BR-8
- **Input conditions:** Run for a date range that includes weekend dates (Saturday/Sunday).
- **Expected output:** If transactions exist on weekend dates, they are included in the aggregation like any other day. No fallback to Friday or skipping of weekend data.
- **Verification method:** For a run date that falls on a weekend, check if the output includes transactions from that weekend date. Confirm the as_of value (from first row) reflects the actual date, not a substituted weekday.

### TC-09: Empty Input Handling
- **Traces to:** BR-9
- **Input conditions:** card_transactions returns zero rows for the given effective date (e.g., a date with no data in the datalake).
- **Expected output:** The output contains zero data rows. V1 returns an empty DataFrame with the correct schema. V2 should produce the same.
- **Verification method:** If a zero-data date exists in the test range, run for that date and verify empty output. Note the FSD identifies a risk: if DataSourcing returns zero rows, the Transformation's `RegisterTable` may skip table registration, causing a SQL error. Monitor for this during Phase D. If triggered, this test documents the expected behavior for the resolution agent.

### TC-10: Unused Columns Eliminated (AP4)
- **Traces to:** BR-10
- **Input conditions:** V2 job config sources only `merchant_category_code` and `amount` from card_transactions, and `mcc_code` and `mcc_description` from merchant_categories.
- **Expected output:** The job produces correct output despite not sourcing `card_txn_id`, `card_id`, `customer_id`, `merchant_name`, `txn_timestamp`, `authorization_status`, or `risk_level`. These columns are irrelevant to the aggregation logic.
- **Verification method:** Verify V2 job config JSON sources only the necessary columns. Confirm the output matches V1 via Proofmark — proving the eliminated columns were truly unused.

### TC-11: Parquet Output Format Verification
- **Traces to:** Writer Configuration (BRD)
- **Input conditions:** Standard job run.
- **Expected output:**
  - Output format: Parquet
  - Output directory: `Output/double_secret_curated/card_spending_by_merchant/`
  - Number of part files: 1 (numParts = 1)
  - Write mode: Overwrite — running the job twice produces output from only the second run
- **Verification method:**
  - Verify output directory exists at expected path
  - Count part files in the directory — should be exactly 1
  - Run job twice for different dates and confirm only the second run's data is present (Overwrite behavior)
  - Verify file format is valid Parquet (can be read by Proofmark with `reader: parquet`)

### TC-12: as_of Consistency Across All Output Rows
- **Traces to:** BR-5, BRD Edge Case 1
- **Input conditions:** Run for a date range that produces multiple MCC rows in the output.
- **Expected output:** Every single output row has the identical as_of value. The as_of is NOT per-group — it is a single global value taken from the first row of card_transactions.
- **Verification method:** Read all output rows and verify `COUNT(DISTINCT as_of) = 1`. Compare the as_of value against what the first row of card_transactions would have for the given effective date range.

### TC-13: Unknown MCC Code — Empty Description Fallback
- **Traces to:** BR-4, BRD Edge Case 2
- **Input conditions:** A card_transactions row with a merchant_category_code that does not exist in merchant_categories (theoretical per BRD — all 17 MCCs are currently covered).
- **Expected output:** The output row for that MCC has mcc_description = '' (empty string), not NULL.
- **Verification method:** Verify via LEFT JOIN + COALESCE logic in the SQL. In production data, confirm all output mcc_description values are non-NULL (empty string is acceptable, NULL is not). If a future data change introduces an unknown MCC, this test documents expected behavior.

### TC-14: Merchant Category Duplicates Across as_of Dates
- **Traces to:** BRD Edge Case 3
- **Input conditions:** merchant_categories data where the same mcc_code appears in multiple as_of date snapshots. V1's dictionary overwrites with the last-seen value; V2's SQL uses GROUP BY subquery to deduplicate.
- **Expected output:** V2 produces the same mcc_description for each mcc_code as V1. Per the BRD, descriptions are consistent across as_of dates in the test data, so no mismatch is expected.
- **Verification method:** Query `SELECT mcc_code, COUNT(DISTINCT mcc_description) FROM datalake.merchant_categories GROUP BY mcc_code HAVING COUNT(DISTINCT mcc_description) > 1` — expect zero rows (confirming descriptions are consistent). If any inconsistencies exist, compare V1 and V2 output for those MCC codes.

### TC-15: Declined Transactions in Spending Totals
- **Traces to:** BRD Edge Case 4
- **Input conditions:** card_transactions containing 'Declined' authorization_status rows with non-zero amounts.
- **Expected output:** Declined transactions contribute to both txn_count and total_spending. There is no filter excluding them. The total_spending may therefore include amounts from transactions that were not actually completed.
- **Verification method:** Query `SELECT merchant_category_code, COUNT(*), SUM(amount) FROM datalake.card_transactions WHERE as_of = '{date}' AND authorization_status = 'Declined' GROUP BY merchant_category_code` and verify these counts/sums are included (not subtracted) in the output totals. The all-inclusive count should match V1's output.

### TC-16: Proofmark Configuration Correctness
- **Traces to:** FSD Section 8
- **Input conditions:** Proofmark config for this job.
- **Expected output:** Config specifies:
  - `comparison_target: "card_spending_by_merchant"`
  - `reader: parquet`
  - `threshold: 100.0`
  - No EXCLUDED columns
  - No FUZZY columns
  - No CSV-specific settings (header_rows, trailer_rows) since this is Parquet
- **Verification method:** Read the Proofmark YAML config at `POC3/proofmark_configs/card_spending_by_merchant.yaml` and verify all fields match the FSD's Proofmark config design. Confirm strict matching is configured (100% threshold, no fuzzy tolerances).

### TC-17: Column Order Verification
- **Traces to:** FSD Section 4 (Output Schema)
- **Input conditions:** Standard job run.
- **Expected output:** Output columns appear in this exact order:
  1. mcc_code
  2. mcc_description
  3. txn_count
  4. total_spending
  5. as_of
- **Verification method:** Read the output Parquet schema and verify column names and order match exactly. Compare V2 schema against V1 output schema — they must be identical.

### TC-18: Zero-Row Scenario
- **Traces to:** BR-1, BR-9
- **Input conditions:** An effective date for which card_transactions has no data in the datalake.
- **Expected output:** The output should contain zero rows. V1 handles this via an early return with an empty DataFrame (CardSpendingByMerchantProcessor.cs:23-26).
- **Verification method:** If a zero-data date exists in the test range (2024-10-01 to 2024-12-31), run for that date. Otherwise, this is a theoretical test. Note the FSD's risk register flags that V2 may throw a SQL error if the card_transactions table is never registered in SQLite due to zero rows from DataSourcing. If this occurs during Phase D, document the error and escalate per the FSD's mitigation plan.

### TC-19: total_spending Precision (Decimal vs Double)
- **Traces to:** BR-3, FSD SQL Design Note 3
- **Input conditions:** card_transactions with amounts that could expose precision differences between decimal and double accumulation. For example, many transactions with amounts like $19.99 or $0.01 that, when summed over hundreds of rows, might diverge between decimal and IEEE 754 double.
- **Expected output:** V2's total_spending values match V1 exactly. The FSD notes that for typical 2-decimal-place monetary values, double has sufficient precision to match decimal. However, if precision diverges for large sums, this test documents the expected behavior for adding a Proofmark FUZZY tolerance.
- **Verification method:** Compare V1 and V2 total_spending values for every MCC via Proofmark. If any differ, measure the delta. If delta is within epsilon (e.g., < 0.01), add a FUZZY tolerance on total_spending per the FSD's fallback plan. If delta exceeds reasonable epsilon, investigate the V2 SQL implementation.

### TC-20: Row Ordering
- **Traces to:** FSD SQL Design Note 6, FSD Risk Register
- **Input conditions:** Standard job run producing multiple output rows (one per MCC).
- **Expected output:** V2 orders rows by `merchant_category_code` (ascending) per the SQL's `ORDER BY ct.merchant_category_code`. V1's order is determined by Dictionary iteration order (insertion order in modern .NET, not contractually guaranteed).
- **Verification method:** Compare V1 and V2 output row ordering via Proofmark. If Proofmark flags a row ordering mismatch, check whether V1's output is already in MCC code order (likely, since DataSourcing returns data in a consistent order). If not, adjust V2's ORDER BY to match V1's actual output order, or configure Proofmark for order-independent comparison if supported.
