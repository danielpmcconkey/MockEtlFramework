# DailyTransactionSummary — Business Requirements Document

## Overview
Produces a daily summary of transaction activity per account, including total amount, transaction count, and debit/credit breakdowns. Output is a CSV file with a trailer line, appended per effective date.

## Output Type
CsvFileWriter

## Writer Configuration
- **source**: `daily_txn_summary`
- **outputFile**: `Output/curated/daily_transaction_summary.csv`
- **includeHeader**: true
- **trailerFormat**: `TRAILER|{row_count}|{date}`
- **writeMode**: Append
- **lineEnding**: LF

## Source Tables

| Table | Columns | Filters | Evidence |
|-------|---------|---------|----------|
| datalake.transactions | transaction_id, account_id, txn_timestamp, txn_type, amount, description | Effective date range injected by executor via shared state | [daily_transaction_summary.json:6-11] |
| datalake.branches | branch_id, branch_name | Effective date range injected by executor via shared state | [daily_transaction_summary.json:13-18] |

Note: The `branches` table is sourced but **not used** in the transformation SQL. It is registered as a SQLite table but never referenced in the query.

## Business Rules

BR-1: Transactions are aggregated by `account_id` and `as_of` date.
- Confidence: HIGH
- Evidence: [daily_transaction_summary.json:22] SQL `GROUP BY t.account_id, t.as_of`

BR-2: `total_amount` is computed as the sum of debit amounts plus the sum of credit amounts, rounded to 2 decimal places.
- Confidence: HIGH
- Evidence: [daily_transaction_summary.json:22] `ROUND(SUM(CASE WHEN t.txn_type = 'Debit' THEN t.amount ELSE 0 END) + SUM(CASE WHEN t.txn_type = 'Credit' THEN t.amount ELSE 0 END), 2)`

BR-3: `transaction_count` is the total count of all transactions per account per day (both Debit and Credit).
- Confidence: HIGH
- Evidence: [daily_transaction_summary.json:22] `COUNT(*) AS transaction_count`

BR-4: `debit_total` sums only transactions where `txn_type = 'Debit'`, rounded to 2 decimal places. `credit_total` sums only transactions where `txn_type = 'Credit'`, rounded to 2 decimal places.
- Confidence: HIGH
- Evidence: [daily_transaction_summary.json:22] CASE WHEN expressions for each type

BR-5: Results are ordered by `as_of ASC, account_id ASC`.
- Confidence: HIGH
- Evidence: [daily_transaction_summary.json:22] `ORDER BY sub.as_of, sub.account_id`

BR-6: The SQL uses a subquery wrapping pattern (inner SELECT with GROUP BY, outer SELECT reordering columns). The outer SELECT does not add any further transformation.
- Confidence: HIGH
- Evidence: [daily_transaction_summary.json:22] `SELECT sub.account_id, sub.as_of, ... FROM (...) sub ORDER BY ...`

BR-7: The trailer line follows format `TRAILER|{row_count}|{date}` where `{row_count}` is the number of data rows and `{date}` is the effective date from `__maxEffectiveDate`.
- Confidence: HIGH
- Evidence: [daily_transaction_summary.json:29] trailerFormat; [Architecture.md:241] trailer token definitions

## Output Schema

| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| account_id | transactions.account_id | Direct (from GROUP BY) | [daily_transaction_summary.json:22] |
| as_of | transactions.as_of | Direct (from GROUP BY) | [daily_transaction_summary.json:22] |
| total_amount | transactions.amount | `ROUND(SUM(debit) + SUM(credit), 2)` | [daily_transaction_summary.json:22] |
| transaction_count | transactions | `COUNT(*)` | [daily_transaction_summary.json:22] |
| debit_total | transactions.amount | `ROUND(SUM(CASE WHEN txn_type='Debit' THEN amount ELSE 0 END), 2)` | [daily_transaction_summary.json:22] |
| credit_total | transactions.amount | `ROUND(SUM(CASE WHEN txn_type='Credit' THEN amount ELSE 0 END), 2)` | [daily_transaction_summary.json:22] |

## Non-Deterministic Fields
None identified. All computations are deterministic given the same input data and effective date.

## Write Mode Implications
- **Append** mode: Each effective date run appends rows plus a trailer to the CSV file. Over multiple days, the file grows with accumulated data and trailers.
- The header is only written on the **first run** (when the file does not yet exist). On subsequent Append runs the header is suppressed because `CsvFileWriter` guards header output with `if (_includeHeader && !append)` where `append = _writeMode == WriteMode.Append && File.Exists(resolvedPath)`. [Lib/Modules/CsvFileWriter.cs:42,47]
- The trailer IS written on every run (no append guard on trailer logic). [Lib/Modules/CsvFileWriter.cs:58-68]
- Multi-day output structure: header + day1 data + trailer1 + day2 data + trailer2 + ... + dayN data + trailerN.
- The trailer's `{date}` token reflects `__maxEffectiveDate` for that run.

## Edge Cases

1. **No transactions for an effective date**: The GROUP BY produces zero rows. The CsvFileWriter will write a header and a trailer with `row_count = 0`.

2. **Account with only Debits**: `credit_total` will be 0.00; `total_amount` equals `debit_total`.

3. **Account with only Credits**: `debit_total` will be 0.00; `total_amount` equals `credit_total`.

4. **Branches table sourced but unused**: The `branches` DataFrame is registered in the SQLite context but the transformation SQL never references it. This is harmless but noteworthy — it may be a vestigial data source from a prior version or intended for future use.

5. **ROUND to 2 decimal places**: All monetary columns use SQLite ROUND(..., 2), which follows standard rounding rules.

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|------------------|
| Group by account_id, as_of | [daily_transaction_summary.json:22] |
| total_amount = debit + credit sums | [daily_transaction_summary.json:22] |
| transaction_count via COUNT(*) | [daily_transaction_summary.json:22] |
| Debit/Credit CASE WHEN split | [daily_transaction_summary.json:22] |
| Sort by as_of, account_id | [daily_transaction_summary.json:22] |
| Trailer format | [daily_transaction_summary.json:29] |
| Append write mode | [daily_transaction_summary.json:30] |
| LF line ending | [daily_transaction_summary.json:31] |
| includeHeader = true | [daily_transaction_summary.json:28] |
| firstEffectiveDate = 2024-10-01 | [daily_transaction_summary.json:3] |
| Unused branches source | [daily_transaction_summary.json:13-18] vs SQL query |

## Open Questions

1. **Why is the branches table sourced?** It is registered but never referenced in the SQL. This could be a vestigial artifact or intentional for future use.
- Confidence: MEDIUM (no conflicting evidence, but the inclusion is unexplained)
