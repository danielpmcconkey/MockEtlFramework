# DailyBalanceMovement — Business Requirements Document

## Overview
Computes daily debit and credit totals per account, along with the net movement (credits minus debits), by aggregating transaction data. Output is written to CSV with Overwrite mode (no trailer).

## Output Type
CsvFileWriter

## Writer Configuration
- **source**: `output`
- **outputFile**: `Output/curated/daily_balance_movement.csv`
- **includeHeader**: true
- **writeMode**: Overwrite
- **lineEnding**: LF
- **trailerFormat**: Not specified (no trailer)

## Source Tables

| Table | Columns Used | Filters | Evidence |
|-------|-------------|---------|----------|
| datalake.transactions | transaction_id, account_id, txn_type, amount | Effective date range (injected) | [daily_balance_movement.json:8-10] |
| datalake.accounts | account_id, customer_id | Effective date range (injected) | [daily_balance_movement.json:13-15] |

## Business Rules

BR-1: Transactions are aggregated per account_id. Debits and credits are summed separately based on the txn_type field ("Debit" or "Credit").
- Confidence: HIGH
- Evidence: [DailyBalanceMovementCalculator.cs:44-48] — if/else on txnType == "Debit" / "Credit"

BR-2: **KNOWN BUG — Double arithmetic instead of decimal**: Debit and credit totals are computed using `double` (floating-point) arithmetic instead of `decimal`. This introduces epsilon errors in financial calculations.
- Confidence: HIGH
- Evidence: [DailyBalanceMovementCalculator.cs:34] — `Dictionary<int, (double debitTotal, double creditTotal, ...)>`; comment "W6: Use double arithmetic instead of decimal (epsilon errors)"

BR-3: Net movement is calculated as `creditTotal - debitTotal` (credits minus debits).
- Confidence: HIGH
- Evidence: [DailyBalanceMovementCalculator.cs:59] — `double netMovement = creditTotal - debitTotal;`

BR-4: Customer_id is looked up from the accounts DataFrame via a dictionary keyed by account_id. If no match, defaults to 0.
- Confidence: HIGH
- Evidence: [DailyBalanceMovementCalculator.cs:27-32,56] — accountToCustomer dictionary with GetValueOrDefault

BR-5: The as_of value is taken from the first transaction encountered for each account_id.
- Confidence: HIGH
- Evidence: [DailyBalanceMovementCalculator.cs:42] — `stats[accountId] = (0.0, 0.0, row["as_of"])`; only set on first encounter

BR-6: If either transactions or accounts is null or empty, an empty DataFrame is produced.
- Confidence: HIGH
- Evidence: [DailyBalanceMovementCalculator.cs:18-22]

BR-7: **KNOWN ISSUE — Overwrite mode for daily data**: The code comment notes "W9: writeMode in JSON is 'Overwrite' (wrong — should be Append, loses prior days)". Each run overwrites the previous day's data.
- Confidence: HIGH
- Evidence: [DailyBalanceMovementCalculator.cs:72] — comment; [daily_balance_movement.json:29] — writeMode: "Overwrite"

BR-8: Transaction amounts are converted to double via Convert.ToDouble. No explicit rounding is applied to the output values.
- Confidence: HIGH
- Evidence: [DailyBalanceMovementCalculator.cs:39] — `double amount = Convert.ToDouble(row["amount"]);`; no Math.Round calls

## Output Schema

| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| account_id | transactions (group key) | Convert.ToInt32 | [DailyBalanceMovementCalculator.cs:37,61] |
| customer_id | accounts.customer_id | Lookup by account_id; 0 if missing | [DailyBalanceMovementCalculator.cs:56] |
| debit_total | Computed | SUM of amount where txn_type="Debit" (double) | [DailyBalanceMovementCalculator.cs:46] |
| credit_total | Computed | SUM of amount where txn_type="Credit" (double) | [DailyBalanceMovementCalculator.cs:48] |
| net_movement | Computed | credit_total - debit_total (double) | [DailyBalanceMovementCalculator.cs:59] |
| as_of | transactions.as_of | First transaction's as_of per account | [DailyBalanceMovementCalculator.cs:42] |

## Non-Deterministic Fields
None identified, though double arithmetic may produce platform-dependent precision differences.

## Write Mode Implications
- **Overwrite mode (noted as a bug)**: Each run replaces the entire CSV. On multi-day gap-fill, only the last day's data persists. The code comment explicitly flags this as wrong — the intent was likely Append mode to build a daily history.

## Edge Cases
- **Double vs decimal precision**: Financial amounts computed with double arithmetic will have floating-point errors (e.g., 1234.56 may become 1234.5600000000002).
- **Transactions on weekends**: Transactions exist every day. Accounts are weekday-only. On weekends, the accounts DataFrame may be empty, triggering the empty-input guard and producing no output.
- **Unknown txn_type**: Transactions with txn_type other than "Debit" or "Credit" are ignored (neither summed to debit nor credit totals but still counted in the group key creation).
- **No rounding**: Unlike other jobs, no Math.Round is applied to output values.
- **Output row ordering**: No explicit ordering on the output rows (Dictionary iteration order).

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|------------------|
| Group by account_id, sum debit/credit | DailyBalanceMovementCalculator.cs:34-49 |
| Net movement = credit - debit | DailyBalanceMovementCalculator.cs:59 |
| Double arithmetic bug (W6) | DailyBalanceMovementCalculator.cs:34 comment |
| Overwrite mode bug (W9) | DailyBalanceMovementCalculator.cs:72 comment, daily_balance_movement.json:29 |
| Customer lookup with default 0 | DailyBalanceMovementCalculator.cs:27-32,56 |
| First effective date 2024-10-01 | daily_balance_movement.json:3 |

## Open Questions
1. The double arithmetic issue (W6) will cause precision errors in financial values. Should this be corrected to decimal? (Confidence: HIGH — this is a known bug per code comment)
2. The Overwrite mode issue (W9) means historical data is lost. Should this be Append? (Confidence: HIGH — per code comment)
