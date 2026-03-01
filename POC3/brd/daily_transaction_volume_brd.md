# DailyTransactionVolume — Business Requirements Document

## Overview
Produces daily aggregate transaction volume metrics across all accounts — total transaction count, total amount, and average amount per transaction. Output is a CSV with a control trailer, appended per effective date.

## Output Type
CsvFileWriter

## Writer Configuration
- **source**: `daily_vol`
- **outputFile**: `Output/curated/daily_transaction_volume.csv`
- **includeHeader**: true
- **trailerFormat**: `CONTROL|{date}|{row_count}|{timestamp}`
- **writeMode**: Append
- **lineEnding**: CRLF

## Source Tables

| Table | Columns | Filters | Evidence |
|-------|---------|---------|----------|
| datalake.transactions | transaction_id, account_id, txn_type, amount | Effective date range injected by executor via shared state | [daily_transaction_volume.json:6-10] |

## Business Rules

BR-1: Transactions are aggregated by `as_of` date across all accounts (no account-level breakout).
- Confidence: HIGH
- Evidence: [daily_transaction_volume.json:15] `GROUP BY as_of`

BR-2: `total_transactions` is the count of all transactions for each date.
- Confidence: HIGH
- Evidence: [daily_transaction_volume.json:15] `COUNT(*) AS total_transactions`

BR-3: `total_amount` is the sum of all transaction amounts for each date, rounded to 2 decimal places.
- Confidence: HIGH
- Evidence: [daily_transaction_volume.json:15] `ROUND(SUM(amount), 2) AS total_amount`

BR-4: `avg_amount` is the average transaction amount for each date, rounded to 2 decimal places.
- Confidence: HIGH
- Evidence: [daily_transaction_volume.json:15] `ROUND(AVG(amount), 2) AS avg_amount`

BR-5: The CTE computes `min_amount` and `max_amount` but these are **not included** in the final SELECT output.
- Confidence: HIGH
- Evidence: [daily_transaction_volume.json:15] CTE selects `MIN(amount) AS min_amount, MAX(amount) AS max_amount` but outer SELECT only picks `as_of, total_transactions, total_amount, avg_amount`

BR-6: Results are ordered by `as_of ASC`.
- Confidence: HIGH
- Evidence: [daily_transaction_volume.json:15] `ORDER BY as_of`

BR-7: The trailer follows format `CONTROL|{date}|{row_count}|{timestamp}` where `{date}` = `__maxEffectiveDate`, `{row_count}` = number of data rows, `{timestamp}` = UTC now in ISO 8601.
- Confidence: HIGH
- Evidence: [daily_transaction_volume.json:22]; [Architecture.md:241] trailer token definitions

BR-8: Line endings are **CRLF** (Windows-style).
- Confidence: HIGH
- Evidence: [daily_transaction_volume.json:24] `"lineEnding": "CRLF"`

## Output Schema

| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| as_of | transactions.as_of | Direct (from GROUP BY) | [daily_transaction_volume.json:15] |
| total_transactions | transactions | `COUNT(*)` | [daily_transaction_volume.json:15] |
| total_amount | transactions.amount | `ROUND(SUM(amount), 2)` | [daily_transaction_volume.json:15] |
| avg_amount | transactions.amount | `ROUND(AVG(amount), 2)` | [daily_transaction_volume.json:15] |

## Non-Deterministic Fields
- **Trailer `{timestamp}` token**: The `{timestamp}` in the trailer is UTC now at time of writing, making the trailer line non-deterministic across runs even with the same input data.
  - Evidence: [Architecture.md:241] `{timestamp}` = UTC now, ISO 8601

## Write Mode Implications
- **Append** mode: Each effective date run appends new rows plus a trailer to the CSV. Over multiple days the file accumulates data.
- The header is only written on the **first run** (when the file does not yet exist). On subsequent Append runs the header is suppressed (`if (_includeHeader && !append)`). The trailer IS written on every run. Multi-day output structure: header + day1 data + trailer1 + day2 data + trailer2 + ... [Lib/Modules/CsvFileWriter.cs:42,47,58-68]
- The CRLF line ending applies to all lines including header, data, and trailer.

## Edge Cases

1. **No transactions for an effective date**: The GROUP BY produces zero rows. The CSV will contain a header and a `CONTROL` trailer with `row_count = 0`.

2. **Single date in effective range**: Produces exactly one data row per daily run (since transactions are grouped by `as_of` and typically min/max effective dates span a single day during auto-advance).

3. **CTE computes unused columns**: `min_amount` and `max_amount` are computed in the CTE but dropped by the outer SELECT. This may be intentional (computed for potential future use) or vestigial.

4. **All transactions same amount**: `avg_amount` equals `total_amount / total_transactions` which equals the common amount.

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|------------------|
| Group by as_of (daily aggregate) | [daily_transaction_volume.json:15] |
| COUNT(*) for total_transactions | [daily_transaction_volume.json:15] |
| ROUND(SUM(amount), 2) for total_amount | [daily_transaction_volume.json:15] |
| ROUND(AVG(amount), 2) for avg_amount | [daily_transaction_volume.json:15] |
| min/max computed but not output | [daily_transaction_volume.json:15] |
| ORDER BY as_of | [daily_transaction_volume.json:15] |
| CONTROL trailer format | [daily_transaction_volume.json:22] |
| CRLF line ending | [daily_transaction_volume.json:24] |
| Append write mode | [daily_transaction_volume.json:23] |
| firstEffectiveDate = 2024-10-01 | [daily_transaction_volume.json:3] |

## Open Questions
None.
