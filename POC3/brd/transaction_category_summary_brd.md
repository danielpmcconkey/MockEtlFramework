# TransactionCategorySummary — Business Requirements Document

## Overview
Produces a summary of transaction volume and amounts grouped by transaction type (Debit/Credit) and date, including total, count, and average per category. Output is a CSV with an `END` trailer, appended per effective date.

## Output Type
CsvFileWriter

## Writer Configuration
- **source**: `txn_cat_summary`
- **outputFile**: `Output/curated/transaction_category_summary.csv`
- **includeHeader**: true
- **trailerFormat**: `END|{row_count}`
- **writeMode**: Append
- **lineEnding**: LF

## Source Tables

| Table | Columns | Filters | Evidence |
|-------|---------|---------|----------|
| datalake.transactions | transaction_id, account_id, txn_type, amount | Effective date range injected by executor via shared state | [transaction_category_summary.json:6-10] |
| datalake.segments | segment_id, segment_name, segment_code | Effective date range injected by executor via shared state | [transaction_category_summary.json:12-17] |

Note: The `segments` table is sourced but **not used** in the transformation SQL. It is registered as a SQLite table but never referenced in the query.

## Business Rules

BR-1: Transactions are aggregated by `txn_type` and `as_of` date.
- Confidence: HIGH
- Evidence: [transaction_category_summary.json:22] `GROUP BY txn_type, as_of` in the outer query

BR-2: The SQL uses a CTE `txn_stats` with window functions `ROW_NUMBER()` and `COUNT()` partitioned by `txn_type, as_of`. However, these window function results (`rn`, `type_count`) are **not used** in the final output — the outer query re-aggregates with its own `GROUP BY`.
- Confidence: HIGH
- Evidence: [transaction_category_summary.json:22] CTE computes `ROW_NUMBER() OVER (PARTITION BY txn_type, as_of ORDER BY transaction_id) AS rn, COUNT(*) OVER (PARTITION BY txn_type, as_of) AS type_count` but the outer SELECT only uses `txn_type, as_of, SUM(amount), COUNT(*), AVG(amount)`

BR-3: `total_amount` is `ROUND(SUM(amount), 2)` per category per date.
- Confidence: HIGH
- Evidence: [transaction_category_summary.json:22] `ROUND(SUM(amount), 2) AS total_amount`

BR-4: `transaction_count` is `COUNT(*)` per category per date.
- Confidence: HIGH
- Evidence: [transaction_category_summary.json:22] `COUNT(*) AS transaction_count`

BR-5: `avg_amount` is `ROUND(AVG(amount), 2)` per category per date.
- Confidence: HIGH
- Evidence: [transaction_category_summary.json:22] `ROUND(AVG(amount), 2) AS avg_amount`

BR-6: Results are ordered by `as_of ASC, txn_type ASC`.
- Confidence: HIGH
- Evidence: [transaction_category_summary.json:22] `ORDER BY as_of, txn_type`

BR-7: The trailer follows format `END|{row_count}` where `{row_count}` is the number of data rows.
- Confidence: HIGH
- Evidence: [transaction_category_summary.json:29]; [Architecture.md:241] trailer token definitions

## Output Schema

| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| txn_type | transactions.txn_type | Direct (from GROUP BY) | [transaction_category_summary.json:22] |
| as_of | transactions.as_of | Direct (from GROUP BY) | [transaction_category_summary.json:22] |
| total_amount | transactions.amount | `ROUND(SUM(amount), 2)` | [transaction_category_summary.json:22] |
| transaction_count | transactions | `COUNT(*)` | [transaction_category_summary.json:22] |
| avg_amount | transactions.amount | `ROUND(AVG(amount), 2)` | [transaction_category_summary.json:22] |

## Non-Deterministic Fields
None identified.

## Write Mode Implications
- **Append** mode: Each effective date run appends rows plus an `END` trailer to the CSV.
- The header is only written on the **first run** (when the file does not yet exist). On subsequent Append runs the header is suppressed (`if (_includeHeader && !append)`). The trailer IS written on every run. Multi-day output structure: header + day1 data + trailer1 + day2 data + trailer2 + ... [Lib/Modules/CsvFileWriter.cs:42,47,58-68]
- Typically produces 2 data rows per run (one for "Credit", one for "Debit") since the database contains exactly these two txn_type values.

## Edge Cases

1. **No transactions for an effective date**: The GROUP BY produces zero rows. The CSV writes a header and an `END|0` trailer.

2. **Only one transaction type on a date**: Only one row is output for that date (either "Credit" or "Debit").

3. **Unused CTE window functions**: The `ROW_NUMBER()` and `COUNT() OVER` in the CTE add computational overhead but do not affect the output. The outer query's `GROUP BY` already performs the same aggregation. This appears to be vestigial complexity.

4. **Segments table sourced but unused**: Same pattern as other jobs — the segments DataFrame is registered but never referenced in the SQL.

5. **Order guarantees**: With `ORDER BY as_of, txn_type`, "Credit" will appear before "Debit" alphabetically for any given date.

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|------------------|
| Group by txn_type, as_of | [transaction_category_summary.json:22] |
| ROUND(SUM(amount), 2) for total_amount | [transaction_category_summary.json:22] |
| COUNT(*) for transaction_count | [transaction_category_summary.json:22] |
| ROUND(AVG(amount), 2) for avg_amount | [transaction_category_summary.json:22] |
| ORDER BY as_of, txn_type | [transaction_category_summary.json:22] |
| Unused window functions in CTE | [transaction_category_summary.json:22] |
| END trailer format | [transaction_category_summary.json:29] |
| Append write mode | [transaction_category_summary.json:30] |
| LF line ending | [transaction_category_summary.json:31] |
| firstEffectiveDate = 2024-10-01 | [transaction_category_summary.json:3] |
| Unused segments source | [transaction_category_summary.json:12-17] vs SQL |

## Open Questions

1. **Vestigial CTE window functions**: The `ROW_NUMBER()` and `COUNT() OVER` in the CTE serve no purpose since the outer query re-aggregates. This may be left over from a prior version that used row-level or ranked output.
- Confidence: MEDIUM (no conflicting evidence, but structurally unnecessary)
