# MonthlyTransactionTrend — Business Requirements Document

## Overview
Produces daily transaction metrics (count, total amount, average amount) across all accounts, intended to support monthly trend analysis. Output is a vanilla CSV (no trailer) appended per effective date.

## Output Type
CsvFileWriter

## Writer Configuration
- **source**: `monthly_trend`
- **outputFile**: `Output/curated/monthly_transaction_trend.csv`
- **includeHeader**: true
- **trailerFormat**: (not specified — no trailer)
- **writeMode**: Append
- **lineEnding**: LF

## Source Tables

| Table | Columns | Filters | Evidence |
|-------|---------|---------|----------|
| datalake.transactions | transaction_id, account_id, txn_type, amount | Effective date range injected by executor via shared state | [monthly_transaction_trend.json:6-10] |
| datalake.branches | branch_id, branch_name | Effective date range injected by executor via shared state | [monthly_transaction_trend.json:12-17] |

Note: The `branches` table is sourced but **not used** in the transformation SQL. It is registered as a SQLite table but never referenced in the query.

## Business Rules

BR-1: Transactions are aggregated by `as_of` date across all accounts.
- Confidence: HIGH
- Evidence: [monthly_transaction_trend.json:22] `GROUP BY as_of`

BR-2: The SQL includes a hardcoded date filter `WHERE as_of >= '2024-10-01'` in the CTE.
- Confidence: HIGH
- Evidence: [monthly_transaction_trend.json:22] `WHERE as_of >= '2024-10-01'`

BR-3: `daily_transactions` is the count of all transactions per date.
- Confidence: HIGH
- Evidence: [monthly_transaction_trend.json:22] `COUNT(*) AS daily_transactions`

BR-4: `daily_amount` is the sum of all transaction amounts per date, rounded to 2 decimal places.
- Confidence: HIGH
- Evidence: [monthly_transaction_trend.json:22] `ROUND(SUM(amount), 2) AS daily_amount`

BR-5: `avg_transaction_amount` is the average transaction amount per date, rounded to 2 decimal places.
- Confidence: HIGH
- Evidence: [monthly_transaction_trend.json:22] `ROUND(AVG(amount), 2) AS avg_transaction_amount`

BR-6: Results are ordered by `as_of ASC`.
- Confidence: HIGH
- Evidence: [monthly_transaction_trend.json:22] `ORDER BY as_of`

BR-7: No trailer line is produced (no `trailerFormat` specified).
- Confidence: HIGH
- Evidence: [monthly_transaction_trend.json:26-30] No trailerFormat key present

## Output Schema

| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| as_of | transactions.as_of | Direct (from GROUP BY) | [monthly_transaction_trend.json:22] |
| daily_transactions | transactions | `COUNT(*)` | [monthly_transaction_trend.json:22] |
| daily_amount | transactions.amount | `ROUND(SUM(amount), 2)` | [monthly_transaction_trend.json:22] |
| avg_transaction_amount | transactions.amount | `ROUND(AVG(amount), 2)` | [monthly_transaction_trend.json:22] |

## Non-Deterministic Fields
None identified.

## Write Mode Implications
- **Append** mode: Each effective date run appends rows to the CSV. Over multiple days, the file grows.
- The header is only written on the **first run** (when the file does not yet exist). On subsequent Append runs the header is suppressed (`if (_includeHeader && !append)`). [Lib/Modules/CsvFileWriter.cs:42,47]
- No trailer lines are written since no `trailerFormat` is specified.

## Edge Cases

1. **Hardcoded date filter**: The `WHERE as_of >= '2024-10-01'` clause is redundant with the `firstEffectiveDate: 2024-10-01` in the job config, since the executor will never inject dates before the first effective date. However, the hardcoded filter means even if the executor somehow sourced earlier data, only dates from 2024-10-01 onward would appear in output.

2. **No transactions for an effective date**: The GROUP BY produces zero rows. The CsvFileWriter writes just a header line.

3. **Branches table sourced but unused**: Same pattern as DailyTransactionSummary — the branches DataFrame is registered but never referenced.

4. **Single date per run**: During auto-advance, each run typically processes one effective date, producing one data row per run.

5. **CTE pass-through pattern**: The CTE `base` selects exactly the same columns the outer query re-selects, with no additional transformation. The CTE is structurally unnecessary but does not change behavior.

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|------------------|
| Group by as_of | [monthly_transaction_trend.json:22] |
| COUNT(*) for daily_transactions | [monthly_transaction_trend.json:22] |
| ROUND(SUM(amount), 2) for daily_amount | [monthly_transaction_trend.json:22] |
| ROUND(AVG(amount), 2) for avg_transaction_amount | [monthly_transaction_trend.json:22] |
| Hardcoded date filter >= 2024-10-01 | [monthly_transaction_trend.json:22] |
| ORDER BY as_of | [monthly_transaction_trend.json:22] |
| No trailer | [monthly_transaction_trend.json:26-30] |
| Append write mode | [monthly_transaction_trend.json:29] |
| LF line ending | [monthly_transaction_trend.json:30] |
| firstEffectiveDate = 2024-10-01 | [monthly_transaction_trend.json:3] |
| Unused branches source | [monthly_transaction_trend.json:12-17] vs SQL query |

## Open Questions

1. **Hardcoded date filter in SQL**: The `WHERE as_of >= '2024-10-01'` appears redundant with the executor's effective date management. Is this intentional defense-in-depth or a vestigial artifact?
- Confidence: LOW (cannot determine intent without production context)

2. **Unused branches source**: Same question as DailyTransactionSummary — why is the branches table sourced if not used?
- Confidence: MEDIUM
