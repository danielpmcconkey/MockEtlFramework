# InterAccountTransfers — Business Requirements Document

## Overview
Detects inter-account transfers by matching debit and credit transaction pairs that share the same amount, timestamp, and different account IDs. Produces a Parquet file of matched transfer pairs per effective date.

## Output Type
ParquetFileWriter

## Writer Configuration
- **source**: `output`
- **outputDirectory**: `Output/curated/inter_account_transfers/`
- **numParts**: 1
- **writeMode**: Overwrite

## Source Tables

| Table | Columns Used | Filters | Evidence |
|-------|-------------|---------|----------|
| datalake.transactions | transaction_id, account_id, txn_timestamp, txn_type, amount | Effective date range (injected by executor) | [inter_account_transfers.json:8-12] |
| datalake.accounts | account_id, customer_id | Effective date range (injected by executor) | [inter_account_transfers.json:14-18] |

### Table Schemas (from database)

**transactions**: transaction_id (integer), account_id (integer), txn_timestamp (timestamp), txn_type (varchar: Credit/Debit), amount (numeric), description (varchar), as_of (date).

**accounts**: account_id (integer), customer_id (integer), account_type (varchar), account_status (varchar), open_date (date), current_balance (numeric), interest_rate (numeric), credit_limit (numeric), apr (numeric), as_of (date).

## Business Rules

BR-1: Transactions are separated into debit and credit lists based on `txn_type` field value. Only rows with exactly "Debit" or "Credit" are processed; any other txn_type is silently ignored.
- Confidence: HIGH
- Evidence: [InterAccountTransferDetector.cs:38-41] — `if (txnType == "Debit")` / `else if (txnType == "Credit")`

BR-2: Transfer matching uses an O(n^2) nested loop: for each debit, scan all unmatched credits. A match requires ALL three conditions:
  1. Same `amount` (exact decimal equality)
  2. Same `txn_timestamp` (string equality after `.ToString()`)
  3. Different `account_id` (integer inequality)
- Confidence: HIGH
- Evidence: [InterAccountTransferDetector.cs:48-58] — explicit match conditions in nested loop

BR-3: Each credit can only be matched once (first-match-wins). A `HashSet<int>` of matched credit transaction IDs prevents re-use.
- Confidence: HIGH
- Evidence: [InterAccountTransferDetector.cs:45, 52, 59] — `matchedCredits` HashSet checked before match, populated after

BR-4: Each debit matches at most one credit (breaks after first match).
- Confidence: HIGH
- Evidence: [InterAccountTransferDetector.cs:72] — `break;` after successful match

BR-5: The `as_of` value in the output comes from the debit transaction row, not the credit.
- Confidence: HIGH
- Evidence: [InterAccountTransferDetector.cs:69] — `["as_of"] = debit.asOf`

BR-6: Matching is NOT constrained by customer — two accounts belonging to different customers can be matched. The `accounts` table is sourced but its `customer_id` column is NOT used by the detector.
- Confidence: HIGH
- Evidence: [InterAccountTransferDetector.cs] — `accounts` DataFrame is retrieved from shared state but never accessed in the matching logic

BR-7: Empty output (zero-row DataFrame with correct schema) is produced if transactions is null or empty.
- Confidence: HIGH
- Evidence: [InterAccountTransferDetector.cs:19-23]

BR-8: Match order is deterministic for a given input order but depends on iteration order of debits and credits (order in which rows appear in the source DataFrame).
- Confidence: HIGH
- Evidence: [InterAccountTransferDetector.cs:48-49] — nested `foreach` over lists built from row iteration order

## Output Schema

| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| debit_txn_id | transactions.transaction_id | From matched debit row | [InterAccountTransferDetector.cs:63] |
| credit_txn_id | transactions.transaction_id | From matched credit row | [InterAccountTransferDetector.cs:64] |
| from_account_id | transactions.account_id | From debit row | [InterAccountTransferDetector.cs:65] |
| to_account_id | transactions.account_id | From credit row | [InterAccountTransferDetector.cs:66] |
| amount | transactions.amount | From debit row (same as credit by match condition) | [InterAccountTransferDetector.cs:67] |
| txn_timestamp | transactions.txn_timestamp | From debit row as string | [InterAccountTransferDetector.cs:68] |
| as_of | transactions.as_of | From debit row | [InterAccountTransferDetector.cs:69] |

## Non-Deterministic Fields
None identified, assuming stable row ordering from the database. However, if the source DataFrame row order varies between runs, different debit-credit pairings could result (since the algorithm is greedy first-match-wins).

## Write Mode Implications
**Overwrite** mode: Each effective date run replaces the entire output directory. In multi-day gap-fill scenarios, only the last day's output survives.

## Edge Cases

1. **Multiple credits matching same debit**: Only the first credit encountered in iteration order is matched; remaining are left unmatched.
   - Evidence: [InterAccountTransferDetector.cs:72] — `break` after first match

2. **Same-account debit-credit pairs**: Excluded by the `debit.accountId != credit.accountId` condition.
   - Evidence: [InterAccountTransferDetector.cs:57]

3. **Cross-date matches**: If the date range spans multiple days, a debit on one day could match a credit on a different day (they share the same timestamp and amount). The matching does not enforce same-date constraint.
   - Evidence: [InterAccountTransferDetector.cs:55-56] — only checks amount, timestamp, and account_id

4. **Timestamp as string comparison**: `txn_timestamp` is compared as strings (via `.ToString()`). Format differences between debit and credit rows (e.g., trailing zeros) could prevent valid matches.
   - Evidence: [InterAccountTransferDetector.cs:35, 55] — `row["txn_timestamp"]?.ToString()`

5. **Accounts sourced but unused**: The `accounts` DataFrame is loaded but never referenced in the matching logic. It occupies memory without contributing to output.
   - Evidence: [InterAccountTransferDetector.cs:17] — retrieved but never iterated

6. **Unmatched transactions**: Debits with no matching credit and credits with no matching debit are silently dropped — they do not appear in the output.
   - Evidence: [InterAccountTransferDetector.cs:48-75] — only matched pairs are added to outputRows

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|------------------|
| BR-1: Debit/Credit separation | [InterAccountTransferDetector.cs:38-41] |
| BR-2: Match conditions (amount, timestamp, account) | [InterAccountTransferDetector.cs:55-57] |
| BR-3: Single credit match | [InterAccountTransferDetector.cs:45, 52, 59] |
| BR-4: Single debit match | [InterAccountTransferDetector.cs:72] |
| BR-5: as_of from debit | [InterAccountTransferDetector.cs:69] |
| BR-6: Accounts unused | [InterAccountTransferDetector.cs:17] |
| BR-7: Empty output guard | [InterAccountTransferDetector.cs:19-23] |
| BR-8: Iteration-order dependent | [InterAccountTransferDetector.cs:48-49] |
| Output: Parquet, 1 part, Overwrite | [inter_account_transfers.json:24-29] |

## Open Questions

1. **Why is accounts sourced?** The `accounts` DataFrame is loaded but never used. Possibly a leftover from a previous version that filtered by customer or account type.
   - Confidence: HIGH — code clearly shows it is unused

2. **Cross-date matching**: With multi-date effective ranges, is it intentional that a debit on day X can match a credit on day Y?
   - Confidence: MEDIUM — no date constraint in matching logic, but typical daily runs would have single-date ranges
