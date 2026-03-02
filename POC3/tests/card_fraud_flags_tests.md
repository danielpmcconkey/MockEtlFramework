# CardFraudFlags — Test Plan

## Traceability

| Test ID | BRD Requirement | Description |
|---------|----------------|-------------|
| TC-01   | BR-1           | MCC risk level lookup via JOIN on merchant_category_code |
| TC-02   | BR-2           | Dual filter: risk_level = 'High' AND amount > 500 |
| TC-03   | BR-3           | Threshold is exactly $500 (not $750, not $499, not $501) |
| TC-04   | BR-4           | Banker's rounding (MidpointRounding.ToEven) on amount |
| TC-05   | BR-5           | Unknown MCC codes excluded via INNER JOIN semantics |
| TC-06   | BR-6           | No date filter within transformation — all rows in DataSourcing range evaluated |
| TC-07   | BR-7           | Only MCC codes 5094 and 7995 have risk_level = 'High' |
| TC-08   | BR-8           | mcc_description is NOT present in output |
| TC-09   | BR-9           | Empty card_transactions input produces zero data rows |
| TC-10   | BR-10          | No weekend fallback — weekend transactions processed normally |
| TC-11   | Writer Config  | Output format: CSV, header, Overwrite, LF line endings, no trailer |
| TC-12   | BR-2, BR-4     | Banker's rounding at the $500 boundary — edge cases |
| TC-13   | BR-1, Edge Case 4 | Merchant category duplicates across as_of dates |
| TC-14   | BR-6, Edge Case 3 | Declined transactions included in fraud flags |
| TC-15   | FSD Proofmark  | Proofmark comparison config correctness |
| TC-16   | BR-2           | Amount exactly $500.00 is excluded (strictly greater than) |
| TC-17   | FSD SQL        | Column order matches V1 output schema exactly |

## Test Cases

### TC-01: MCC Risk Level Lookup
- **Traces to:** BR-1
- **Input conditions:** card_transactions with known merchant_category_code values; merchant_categories with corresponding mcc_code and risk_level mappings.
- **Expected output:** Each output row's `risk_level` matches the merchant_categories entry for that transaction's merchant_category_code. For example, a transaction with MCC 5094 should have risk_level = 'High' (Precious Metals), and a transaction with MCC 5411 should not appear (risk_level is not 'High').
- **Verification method:** Query merchant_categories for the mcc_code/risk_level mapping. Verify every row in the output has risk_level = 'High' and that the mcc_code in each row maps to risk_level = 'High' in the lookup table.

### TC-02: Dual Filter — High Risk AND Amount > $500
- **Traces to:** BR-2
- **Input conditions:** Transactions spanning multiple MCCs and amounts:
  - Transaction A: MCC 5094 (High risk), amount $600 — should be included
  - Transaction B: MCC 5094 (High risk), amount $400 — should be excluded (amount <= 500)
  - Transaction C: MCC 5411 (Low risk), amount $600 — should be excluded (not High risk)
  - Transaction D: MCC 5411 (Low risk), amount $200 — should be excluded (neither condition met)
- **Expected output:** Only Transaction A appears in the output. Both conditions must be true simultaneously.
- **Verification method:** Run the job for a date range containing the above transaction profiles. Confirm only transactions meeting BOTH criteria appear. Validate against V1 output via Proofmark.

### TC-03: Threshold Value is $500
- **Traces to:** BR-3
- **Input conditions:** High-risk MCC transactions with amounts near the $500 boundary:
  - $499.99 — excluded
  - $500.00 — excluded (strictly greater than, not >=)
  - $500.01 — included
  - $501.00 — included
- **Expected output:** Only $500.01 and $501.00 transactions appear. The $500.00 transaction must NOT appear because the condition is `> 500`, not `>= 500`.
- **Verification method:** Inspect output for boundary amounts. Confirm no rows with amount = 500.00 exist. Confirm presence of rows with amounts above 500.00.

### TC-04: Banker's Rounding on Amount
- **Traces to:** BR-4
- **Input conditions:** Transactions with amounts requiring rounding to 2 decimal places:
  - $500.005 should round to $500.00 (Banker's rounding: 0.005 rounds to even, which is .00)
  - $500.015 should round to $500.02 (Banker's rounding: 0.015 rounds to .02)
  - $500.025 should round to $500.02 (Banker's rounding: 0.025 rounds to even, which is .02)
  - $500.035 should round to $500.04 (Banker's rounding: 0.035 rounds to .04)
- **Expected output:** Rounded amount values in the output column follow Banker's rounding rules. The rounding occurs BEFORE the > 500 comparison.
- **Verification method:** Compare V2 output amount values against expected Banker's rounding results. Verify via Proofmark that V2 matches V1 exactly for the same input data.

### TC-05: Unknown MCC Code Handling
- **Traces to:** BR-5
- **Input conditions:** A card_transactions row with a merchant_category_code that does not exist in the merchant_categories table.
- **Expected output:** The transaction is excluded from output entirely. The INNER JOIN in V2 drops unmatched rows, which is semantically equivalent to V1's behavior where an empty string for risk_level can never equal "High".
- **Verification method:** Confirm that the output contains zero rows for transactions with unknown MCC codes. Per BRD, all 17 distinct MCCs in card_transactions are a subset of the 20 in merchant_categories, so this is a defensive test. If data changes in the future and an unknown MCC appears, verify it is excluded.

### TC-06: No Date Filter in Transformation
- **Traces to:** BR-6
- **Input conditions:** DataSourcing returns transactions spanning multiple as_of dates within the effective date range.
- **Expected output:** All transactions across all as_of dates in the range are evaluated against the dual filter. The output should contain qualifying transactions from every date, not just a single date.
- **Verification method:** For a multi-day effective date range, verify the output contains as_of values from multiple dates (assuming qualifying transactions exist on multiple dates). Compare row count against V1 output.

### TC-07: High-Risk MCC Codes
- **Traces to:** BR-7
- **Input conditions:** Run against the full merchant_categories data.
- **Expected output:** Output rows' mcc_code values are exclusively from the set {5094, 7995} — the only MCCs with risk_level = 'High' per database evidence.
- **Verification method:** Run `SELECT DISTINCT mcc_code FROM` the output and confirm only 5094 and 7995 appear. Cross-reference with `SELECT mcc_code FROM datalake.merchant_categories WHERE risk_level = 'High'`.

### TC-08: mcc_description Not in Output
- **Traces to:** BR-8
- **Input conditions:** Standard run.
- **Expected output:** The output CSV header contains exactly these columns in order: `card_txn_id, card_id, customer_id, merchant_name, mcc_code, risk_level, amount, txn_timestamp, as_of`. The column `mcc_description` is absent.
- **Verification method:** Read the first line (header) of the output CSV. Confirm `mcc_description` does not appear. Confirm the column count is exactly 9.

### TC-09: Empty Input Handling
- **Traces to:** BR-9
- **Input conditions:** card_transactions returns zero rows for the given effective date range (e.g., a date with no transaction data).
- **Expected output:** The output CSV contains only the header row and no data rows. Zero-row output is valid.
- **Verification method:** If such a date exists in the test range, run for that date and verify the output file is header-only. If all dates have data, this is a theoretical edge case verified by code inspection of V2's SQL behavior (empty JOIN produces empty result set, CsvFileWriter writes header-only file). Note FSD identifies a risk that zero-row DataSourcing may cause a SQL error if the table is not registered in SQLite — monitor for this during Phase D.

### TC-10: No Weekend Fallback
- **Traces to:** BR-10
- **Input conditions:** Run for a date range that includes Saturday and Sunday dates.
- **Expected output:** If there are qualifying transactions on weekend dates, they appear in the output with their actual weekend as_of date. There is no logic that substitutes a Friday date or skips weekends.
- **Verification method:** Check for as_of values that fall on weekends in the output. Confirm they are present and unmodified. Compare against V1 output.

### TC-11: Output Format Verification
- **Traces to:** Writer Configuration (BRD)
- **Input conditions:** Standard job run producing output.
- **Expected output:**
  - File format: CSV
  - File location: `Output/double_secret_curated/card_fraud_flags.csv`
  - First line is a header row with column names
  - No trailer row at end of file
  - Line endings are LF (Unix-style, `\n`), NOT CRLF
  - Write mode is Overwrite — running the job twice should produce a file containing only the second run's data
- **Verification method:**
  - Verify file exists at expected path
  - Read first line and confirm it matches expected column headers
  - Read last line and confirm it is a data row (no trailer)
  - Check line endings with `xxd` or similar tool — no `\r\n` sequences
  - Run job twice for different date ranges and confirm the file only contains the second run's output (Overwrite behavior)

### TC-12: Banker's Rounding at the $500 Boundary
- **Traces to:** BR-2, BR-4
- **Input conditions:** High-risk MCC transactions with amounts that round to exactly $500.00 under Banker's rounding:
  - Raw $500.005 rounds to $500.00 → excluded (500.00 is NOT > 500)
  - Raw $500.015 rounds to $500.02 → included (500.02 > 500)
  - Raw $500.004 rounds to $500.00 → excluded
  - Raw $500.006 rounds to $500.01 → included
- **Expected output:** Only transactions whose post-rounding amount is strictly greater than 500 are included. The rounding happens before the comparison, so the rounded value determines inclusion.
- **Verification method:** Identify transactions in the test data near the $500 boundary. Trace each through Banker's rounding and confirm the output matches expected inclusion/exclusion. Cross-check with V1 output.

### TC-13: Merchant Category Duplicates Across as_of Dates
- **Traces to:** BR-1, BRD Edge Case 4
- **Input conditions:** merchant_categories data where the same mcc_code appears in multiple as_of dates. If the risk_level for a given mcc_code differs across dates, V1's dictionary overwrite means the last-seen value wins.
- **Expected output:** V2's INNER JOIN should produce results consistent with V1. Per the BRD, risk_level is consistent for a given mcc_code across all as_of dates in the test data, so this should not cause a mismatch.
- **Verification method:** Query `SELECT mcc_code, risk_level, COUNT(DISTINCT as_of) FROM datalake.merchant_categories GROUP BY mcc_code, risk_level` to confirm risk_level is consistent per mcc_code. If any mcc_code has varying risk_levels across dates, flag as a potential Proofmark mismatch and verify V2 behavior matches V1.

### TC-14: Declined Transactions Included
- **Traces to:** BR-6, BRD Edge Case 3
- **Input conditions:** card_transactions data including transactions with authorization_status = 'Declined' that also have High-risk MCC and amount > $500.
- **Expected output:** Declined transactions meeting both fraud flag criteria ARE included in the output. There is no authorization_status filter.
- **Verification method:** Query the test data for declined, high-risk, high-amount transactions. Confirm they appear in the output. Note: authorization_status is not sourced in V2 (AP4 elimination), which is correct since it is never used as a filter.

### TC-15: Proofmark Configuration Correctness
- **Traces to:** FSD Section 8
- **Input conditions:** Proofmark config for this job.
- **Expected output:** Config specifies:
  - `reader: csv`
  - `header_rows: 1`
  - `trailer_rows: 0`
  - `threshold: 100.0`
  - No EXCLUDED columns
  - No FUZZY columns
- **Verification method:** Read the Proofmark YAML config at `POC3/proofmark_configs/card_fraud_flags.yaml` and verify all fields match the FSD's Proofmark config design. Ensure the comparison_target matches the job name.

### TC-16: Amount Exactly $500.00 Excluded
- **Traces to:** BR-2
- **Input conditions:** A High-risk MCC transaction with amount exactly $500.00 (no rounding needed — already 2 decimal places).
- **Expected output:** The transaction is NOT included in the output. The filter is strictly `> 500`, not `>= 500`.
- **Verification method:** Search V1 and V2 output for any row with amount = 500.00. Confirm none exist. Query the source data for transactions with amount = 500.00 at high-risk MCCs. If any exist, confirm they are absent from output.

### TC-17: Column Order Verification
- **Traces to:** FSD Section 4 (Output Schema)
- **Input conditions:** Standard job run.
- **Expected output:** Output CSV columns appear in this exact order:
  1. card_txn_id
  2. card_id
  3. customer_id
  4. merchant_name
  5. mcc_code
  6. risk_level
  7. amount
  8. txn_timestamp
  9. as_of
- **Verification method:** Read the header line of the output CSV. Verify column names and their order match exactly. Compare V2 header against V1 header to confirm identical column ordering.
