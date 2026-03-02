# InterAccountTransfers — V2 Test Plan

## Job Info
- **V2 Config**: `inter_account_transfers_v2.json`
- **Tier**: Tier 2 (Framework + Minimal External — SCALPEL)
- **External Module**: `ExternalModules.InterAccountTransfersV2Processor`

## Pre-Conditions
- **Data sources**: `datalake.transactions` (transaction_id, account_id, txn_timestamp, txn_type, amount, as_of)
- **Effective date range**: Injected by executor via `__minEffectiveDate` / `__maxEffectiveDate`
- **V1 baseline output**: `Output/curated/inter_account_transfers/` must exist for Proofmark comparison
- **Note**: V1 also sources `datalake.accounts` (account_id, customer_id), but V2 eliminates this (AP1). Tests must confirm the accounts table is NOT referenced.

## Traceability

| Test ID | BRD Requirement | Description |
|---------|----------------|-------------|
| TC-01   | FSD Section 4  | Output schema: 7 columns in exact order with correct types |
| TC-02   | All            | Row count equivalence between V1 and V2 |
| TC-03   | All            | Data content equivalence between V1 and V2 |
| TC-04   | Writer Config  | ParquetFileWriter: 1 part, Overwrite mode |
| TC-05   | AP1, AP3, AP4, AP6 | Anti-pattern elimination verification |
| TC-06   | BR-7, Edge Cases | Edge case handling |
| TC-07   | FSD Section 8  | Proofmark configuration correctness |
| TC-W9   | W9             | Overwrite mode in multi-day gap-fill |
| TC-08   | BR-1           | Debit/Credit separation by txn_type |
| TC-09   | BR-2           | Match conditions: amount, timestamp, account_id |
| TC-10   | BR-3           | Single credit match (first-match-wins) |
| TC-11   | BR-4           | Single debit match (break after first) |
| TC-12   | BR-5           | as_of from debit row, not credit |
| TC-13   | BR-6, AP1      | Accounts table not sourced or used |
| TC-14   | BR-8           | Iteration-order determinism |
| TC-15   | Edge Case 1    | Multiple credits matching same debit |
| TC-16   | Edge Case 2    | Same-account debit-credit pairs excluded |
| TC-17   | Edge Case 3    | Cross-date matches allowed |
| TC-18   | Edge Case 4    | Timestamp string comparison semantics |
| TC-19   | Edge Case 6    | Unmatched transactions silently dropped |

## Test Cases

### TC-01: Output Schema Validation
- **Traces to:** FSD Section 4
- **Input conditions:** Standard job run producing at least one matched pair.
- **Expected output:** Output Parquet file contains exactly 7 columns in this order:
  1. `debit_txn_id` (integer)
  2. `credit_txn_id` (integer)
  3. `from_account_id` (integer)
  4. `to_account_id` (integer)
  5. `amount` (decimal)
  6. `txn_timestamp` (string)
  7. `as_of` (date)
- **Verification method:** Read the output Parquet file schema. Confirm column names, order, and types match exactly. Compare against V1 output schema to verify no drift.

### TC-02: Row Count Equivalence
- **Traces to:** All BRs
- **Input conditions:** Run V1 and V2 for the same effective date range.
- **Expected output:** V2 output row count equals V1 output row count for every effective date in the test range.
- **Verification method:** Proofmark comparison at 100.0% threshold. Additionally, count rows in both output files and compare. Any difference indicates a matching algorithm divergence — investigate per FSD Section 8 Risk Assessment.

### TC-03: Data Content Equivalence
- **Traces to:** All BRs
- **Input conditions:** Run V1 and V2 for the same effective date range.
- **Expected output:** Every row in V2 output matches a corresponding row in V1 output across all columns. No W-codes introduce expected differences — all columns are deterministic.
- **Verification method:** Proofmark strict comparison (threshold 100.0, no excluded columns, no fuzzy columns). If any mismatch occurs, investigate whether it is a legitimate alternate pairing per BR-8 (see TC-14 and FSD Section 8 Risk Assessment).

### TC-04: Writer Configuration
- **Traces to:** BRD Writer Config, FSD Section 7
- **Input conditions:** Standard job run.
- **Expected output:**
  - Output format: Parquet
  - Output location: `Output/double_secret_curated/inter_account_transfers/`
  - numParts: 1 (single Parquet file in the output directory)
  - writeMode: Overwrite
- **Verification method:**
  - Verify output directory exists at expected path
  - Confirm exactly 1 Parquet part file in the directory
  - Run the job twice for different dates and confirm only the second run's data is present (Overwrite behavior)

### TC-05: Anti-Pattern Elimination Verification
- **Traces to:** FSD Section 3 (AP1, AP3, AP4, AP6)
- **Input conditions:** Review V2 job config and External module source code.
- **Expected verifications:**
  - **AP1 (Dead-end sourcing):** V2 config has NO DataSourcing entry for `datalake.accounts`. Only `transactions` is sourced. V1 config had `accounts` as a second DataSourcing entry — confirm it is absent from V2.
  - **AP3 (Unnecessary External — partial):** V2 module chain is `DataSourcing -> Transformation -> External -> ParquetFileWriter`. The External module does NOT perform data retrieval, filtering, or debit/credit separation — those are handled by SQL Transformation. The External module performs ONLY the greedy matching assignment.
  - **AP4 (Unused columns — via AP1):** The entire `accounts` DataSourcing entry is removed. All columns sourced from `transactions` (transaction_id, account_id, txn_timestamp, txn_type, amount) are used in the SQL or matching logic.
  - **AP6 (Row-by-row iteration — partial):** V1's foreach loop for debit/credit separation is replaced by SQL WHERE clause. V1's O(n^2) nested loop is replaced by a pre-joined candidate set with O(n) scan in the External module. The External still iterates candidate rows, but this is necessary for greedy assignment semantics.
- **Verification method:** Code review of V2 config JSON and External module source. Diff V2 config against V1 config to confirm accounts DataSourcing removal. Inspect External module to confirm it reads `candidates` (not `transactions` directly) and does not perform debit/credit separation.

### TC-06: Edge Cases
- **Traces to:** BR-7, Edge Cases 1-6
- **Input conditions:** See individual edge case test cases TC-15 through TC-19.
- **Expected output:** See individual test cases.
- **Verification method:** This is an umbrella case. See TC-15 through TC-19 for specific verification.

### TC-07: Proofmark Configuration
- **Traces to:** FSD Section 8
- **Input conditions:** Proofmark config for this job.
- **Expected output:** Config specifies:
  - `comparison_target: "inter_account_transfers"`
  - `reader: parquet`
  - `threshold: 100.0`
  - No `csv` section (Parquet output)
  - No EXCLUDED columns
  - No FUZZY columns
- **Verification method:** Read the Proofmark YAML config at `POC3/proofmark_configs/inter_account_transfers.yaml` and verify all fields match the FSD's Proofmark config design. No overrides are needed because all output columns are deterministic — no runtime timestamps, UUIDs, or floating-point accumulation.

## W-Code Test Cases

### TC-W9: Wrong writeMode (W9 — Noted)
- **Traces to:** W9, FSD Section 7
- **W-Code behavior:** V1 uses Overwrite mode for Parquet output. In multi-day gap-fill (auto-advance across multiple effective dates), each day's run overwrites the previous day's output. Only the last day's data survives.
- **V2 handling:** V2 reproduces Overwrite mode exactly. This is V1 behavior, not a V2 bug. Documented with comment.
- **Input conditions:** Run V2 for a multi-day effective date range (e.g., 2024-10-01 through 2024-10-03) with auto-advance.
- **Expected output:** After all days are processed, the output directory contains ONLY the last effective date's matched pairs. Prior days' output is overwritten.
- **Verification method:** Run V2 for a 3-day range. After completion, read the output Parquet file and verify all `as_of` values correspond to the last date in the range (or to debit rows from various dates if cross-date matching occurred on the last run). Confirm that data from prior days' runs does not persist in the output.

## Additional Test Cases

### TC-08: Debit/Credit Separation by txn_type
- **Traces to:** BR-1
- **Input conditions:** Transactions table containing rows with txn_type values: "Debit", "Credit", and potentially other values (e.g., "Transfer", "Fee", NULL).
- **Expected output:** Only transactions with txn_type exactly "Debit" or "Credit" participate in matching. Rows with other txn_type values are silently excluded — they never appear in the output as either debit_txn_id or credit_txn_id.
- **Verification method:** Query `datalake.transactions` for distinct txn_type values. If non-Debit/Credit types exist, verify none of those transaction_ids appear in the output. V2's SQL JOIN condition (`d.txn_type = 'Debit' AND c.txn_type = 'Credit'`) enforces this at the SQL level.

### TC-09: Match Conditions — Amount, Timestamp, Account
- **Traces to:** BR-2
- **Input conditions:** Transactions with:
  - Pair A: Debit and Credit with same amount ($100), same timestamp, different accounts — should match
  - Pair B: Debit and Credit with different amounts ($100 vs $200), same timestamp, different accounts — should NOT match
  - Pair C: Debit and Credit with same amount, different timestamps, different accounts — should NOT match
  - Pair D: Debit and Credit with same amount, same timestamp, same account — should NOT match (Edge Case 2)
- **Expected output:** Only Pair A appears in the output as a matched transfer.
- **Verification method:** Identify candidate transaction sets in the test data that exercise each condition. Verify each condition independently by checking which pairs appear in the output. Compare against V1 output.

### TC-10: Single Credit Match (First-Match-Wins)
- **Traces to:** BR-3
- **Input conditions:** A scenario where multiple credits could match the same debit (same amount, same timestamp, different accounts). For example:
  - Debit D1 (amount $500, timestamp T1, account A1)
  - Credit C1 (amount $500, timestamp T1, account A2)
  - Credit C2 (amount $500, timestamp T1, account A3)
- **Expected output:** D1 matches with exactly one credit — the one with the lowest credit transaction_id (per V2's ORDER BY). The other credit remains unmatched. The matched credit cannot be reused by any other debit.
- **Verification method:** Query the test data for ambiguous match scenarios. Verify the output contains exactly one pair per debit. Confirm the matched credit_txn_id is the lowest-ID eligible credit. Verify via Proofmark that V1 and V2 produce the same pairing.

### TC-11: Single Debit Match (Break After First)
- **Traces to:** BR-4
- **Input conditions:** A scenario where one debit could match multiple credits, and one credit could match multiple debits:
  - Debit D1 and D2 both qualify to match Credit C1
  - ORDER BY ensures D1 is processed first
- **Expected output:** D1 matches C1. D2 does not match C1 (already consumed). D2 may match a different credit if one exists, or remain unmatched.
- **Verification method:** Query for candidate pairs where multiple debits compete for the same credit. Verify the lower-ID debit wins. Compare against V1 output.

### TC-12: as_of from Debit Row
- **Traces to:** BR-5
- **Input conditions:** A matched pair where the debit and credit have different as_of dates (cross-date match per Edge Case 3).
- **Expected output:** The output row's `as_of` value equals the debit row's as_of, NOT the credit row's as_of.
- **Verification method:** If cross-date matches exist in the test data, identify a matched pair where `debit.as_of != credit.as_of`. Verify the output row's as_of matches the debit's as_of. If no cross-date matches exist, verify by code inspection that `debit_as_of` is the column written as `as_of` in the External module.

### TC-13: Accounts Table Not Sourced
- **Traces to:** BR-6, AP1
- **Input conditions:** Review V2 job config JSON.
- **Expected output:** The V2 config does NOT contain any DataSourcing entry for the `accounts` table. The only DataSourcing entry is for `transactions`.
- **Verification method:** Read `inter_account_transfers_v2.json`. Confirm there is exactly one DataSourcing module entry with table = "transactions". Confirm there is no entry with table = "accounts". Confirm the External module source code does not reference "accounts" in any shared state lookup.

### TC-14: Iteration-Order Determinism
- **Traces to:** BR-8
- **Input conditions:** Run V2 multiple times for the same effective date range.
- **Expected output:** Output is identical across runs. The matching is deterministic because:
  1. DataSourcing returns rows ordered by as_of (framework behavior)
  2. SQL orders candidates by (debit_txn_id, credit_txn_id)
  3. External module iterates in that order
  4. transaction_id is a monotonically increasing PK, so ordering by transaction_id is equivalent to V1's iteration order
- **Verification method:** Run V2 twice for the same date. Diff the output files — they must be byte-identical. Run Proofmark against V1 — if the pairings match, the ordering is equivalent.
- **Risk note:** If Proofmark comparison fails, the most likely cause is ordering divergence. Follow the resolution path in FSD Section 8: identify differing pairs, determine if the difference is a legitimate alternate pairing vs. a bug.

### TC-15: Multiple Credits for Same Debit
- **Traces to:** Edge Case 1
- **Input conditions:** A debit D1 that has 3 eligible credits (C1, C2, C3) with matching amount, timestamp, and different accounts.
- **Expected output:** D1 is paired with exactly one credit — the one with the lowest transaction_id among the eligible candidates. C2 and C3 remain available for other debits.
- **Verification method:** Query candidates SQL for debit_txn_ids that appear with multiple credit_txn_ids. Verify the output contains only the lowest-credit-ID pairing for each such debit. Compare against V1.

### TC-16: Same-Account Pairs Excluded
- **Traces to:** Edge Case 2
- **Input conditions:** A debit and credit on the SAME account_id with matching amount and timestamp.
- **Expected output:** This pair does NOT appear in the output. The SQL JOIN condition `d.account_id != c.account_id` excludes it.
- **Verification method:** Query `datalake.transactions` for debit-credit pairs where account_id is the same, amount matches, and timestamp matches (as strings). Verify none of these transaction_id pairs appear in the output.

### TC-17: Cross-Date Matches Allowed
- **Traces to:** Edge Case 3
- **Input conditions:** A multi-day effective date range where a debit on day X and a credit on day Y have the same amount, same timestamp, and different accounts.
- **Expected output:** The cross-date pair IS matched. The SQL JOIN has no date constraint — only amount, timestamp-as-string, and account inequality.
- **Verification method:** Query for matched pairs in the output where the debit's as_of differs from the credit's as_of (the output only has debit as_of, so cross-reference credit_txn_id back to source data to check the credit's as_of). If no cross-date matches exist in the test data, verify by SQL inspection that no date constraint exists in the JOIN.

### TC-18: Timestamp String Comparison
- **Traces to:** Edge Case 4
- **Input conditions:** Debit and credit transactions with timestamps that differ only in formatting when converted to strings (e.g., trailing zeros, timezone offset notation).
- **Expected output:** The match depends on string equality after `CAST(txn_timestamp AS TEXT)` in SQLite, which must replicate V1's `.ToString()` behavior. If the string representations differ, the pair will NOT match — even if the underlying timestamps are logically equal.
- **Verification method:** Inspect the `datalake.transactions` data for timestamp formatting consistency. Query the candidates SQL to verify the CAST behavior matches V1's .ToString() output. If any pairs are missed due to formatting differences, compare against V1 to confirm the same pairs are missed.

### TC-19: Unmatched Transactions Dropped
- **Traces to:** Edge Case 6
- **Input conditions:** Standard run where some debits have no eligible credit and some credits have no eligible debit.
- **Expected output:** Unmatched transactions do not appear in the output. The output contains ONLY successfully matched pairs.
- **Verification method:** Count total debits and credits in the source data. Count matched pairs in the output. Verify that `output_rows <= min(total_debits, total_credits)`. Cross-reference with V1 output to confirm the same transactions are unmatched.

## Notes

- **Ordering risk is the primary concern for this job.** The greedy first-match-wins algorithm is iteration-order-dependent (BR-8). If V2's candidate ordering diverges from V1's row iteration order, different pairings will result. Both outputs may be "correct" (valid pairings), but they must be identical for Proofmark to pass. The SQL `ORDER BY d.transaction_id, c.transaction_id` should match V1's natural iteration order since transaction_id is the PK and DataSourcing orders by as_of then natural PK order.
- **No fuzzy comparison needed.** All values are exact — amounts use decimal (no float accumulation), timestamps are strings, IDs are integers, as_of is a date. No W-codes introduce numeric drift.
- **AP1 elimination is the highest-impact anti-pattern fix.** Removing the unused `accounts` DataSourcing entry reduces unnecessary database I/O and memory usage with zero output impact.
- **The External module is minimal and justified.** It performs ONLY the greedy assignment — all data retrieval, filtering, and candidate generation are handled by DataSourcing and Transformation SQL. This is a textbook Tier 2 (SCALPEL) implementation.
