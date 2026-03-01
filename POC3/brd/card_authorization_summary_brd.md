# CardAuthorizationSummary — Business Requirements Document

## Overview
Produces a daily summary of card transaction authorization outcomes (approved vs declined) grouped by card type. The output enables monitoring of authorization approval rates across different card types over time.

## Output Type
CsvFileWriter

## Writer Configuration
- **outputFile**: `Output/curated/card_authorization_summary.csv`
- **includeHeader**: true
- **trailerFormat**: `TRAILER|{row_count}|{date}`
- **writeMode**: Overwrite
- **lineEnding**: LF

## Source Tables

| Table | Columns Used | Filters | Evidence |
|-------|-------------|---------|----------|
| datalake.card_transactions | card_txn_id, card_id, customer_id, amount, authorization_status | Effective date range via DataSourcing (as_of between __minEffectiveDate and __maxEffectiveDate) | [card_authorization_summary.json:8-11] |
| datalake.cards | card_id, customer_id, card_type | Effective date range via DataSourcing | [card_authorization_summary.json:14-17] |

## Business Rules

BR-1: Transactions are joined to cards on `card_id` to obtain `card_type` for each transaction.
- Confidence: HIGH
- Evidence: [card_authorization_summary.json:22] SQL `INNER JOIN cards c ON ct.card_id = c.card_id`

BR-2: Results are grouped by `card_type` and `as_of` date, producing one row per card type per date.
- Confidence: HIGH
- Evidence: [card_authorization_summary.json:22] SQL `GROUP BY td.card_type, td.as_of`

BR-3: `approved_count` is calculated by counting transactions where `authorization_status = 'Approved'`.
- Confidence: HIGH
- Evidence: [card_authorization_summary.json:22] SQL `SUM(CASE WHEN td.authorization_status = 'Approved' THEN 1 ELSE 0 END) AS approved_count`

BR-4: `declined_count` is calculated by counting transactions where `authorization_status = 'Declined'`.
- Confidence: HIGH
- Evidence: [card_authorization_summary.json:22] SQL `SUM(CASE WHEN td.authorization_status = 'Declined' THEN 1 ELSE 0 END) AS declined_count`

BR-5: `approval_rate` is calculated as integer division of approved_count / total_count (truncating, not rounding). This uses `CAST(... AS INTEGER) / CAST(... AS INTEGER)` which produces integer division in SQLite, yielding 0 for rates below 100% and 1 for 100% approval.
- Confidence: HIGH
- Evidence: [card_authorization_summary.json:22] SQL `CAST(SUM(CASE WHEN td.authorization_status = 'Approved' THEN 1 ELSE 0 END) AS INTEGER) / CAST(COUNT(*) AS INTEGER) AS approval_rate`

BR-6: The SQL uses a CTE `txn_detail` with `ROW_NUMBER() OVER (PARTITION BY c.card_type ORDER BY ct.card_txn_id)` which assigns row numbers per card type. This column `rn` is computed but never used in the final SELECT — it is dead code.
- Confidence: HIGH
- Evidence: [card_authorization_summary.json:22] `ROW_NUMBER()` computed as `rn` in CTE but not referenced in final SELECT

BR-7: The SQL contains a second CTE `unused_summary` that is defined but never referenced in the final SELECT. This is dead code.
- Confidence: HIGH
- Evidence: [card_authorization_summary.json:22] `unused_summary AS (SELECT card_type, COUNT(*) AS cnt FROM txn_detail GROUP BY card_type)` — defined but unreferenced

BR-8: Only two authorization_status values exist in the data: 'Approved' and 'Declined'.
- Confidence: HIGH
- Evidence: [DB query: `SELECT DISTINCT authorization_status FROM datalake.card_transactions`] returns exactly {Approved, Declined}

BR-9: Only two card_type values exist: 'Credit' and 'Debit'.
- Confidence: HIGH
- Evidence: [DB query: `SELECT DISTINCT card_type FROM datalake.cards`] returns exactly {Credit, Debit}

BR-10: The join between card_transactions and cards is INNER JOIN, meaning transactions without a matching card_id in the cards table are excluded.
- Confidence: HIGH
- Evidence: [card_authorization_summary.json:22] SQL `INNER JOIN cards c ON ct.card_id = c.card_id`

## Output Schema

| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| card_type | cards.card_type | Grouped by | [card_authorization_summary.json:22] |
| total_count | card_transactions | COUNT(*) per card_type/as_of group | [card_authorization_summary.json:22] |
| approved_count | card_transactions.authorization_status | SUM(CASE WHEN 'Approved' THEN 1 ELSE 0 END) | [card_authorization_summary.json:22] |
| declined_count | card_transactions.authorization_status | SUM(CASE WHEN 'Declined' THEN 1 ELSE 0 END) | [card_authorization_summary.json:22] |
| approval_rate | Derived | Integer division: approved_count / total_count (always 0 or 1) | [card_authorization_summary.json:22] |
| as_of | card_transactions.as_of | Grouped by (pass-through from DataSourcing date range) | [card_authorization_summary.json:22] |

## Non-Deterministic Fields
None identified. All fields are deterministic given the same input data and effective date range.

## Write Mode Implications
- **Overwrite** mode: The CSV file is completely replaced on each run.
- For multi-day auto-advance runs, the file will be overwritten on each effective date iteration. Only the last effective date's output will survive.
- The DataSourcing modules source data for the full effective date range (min to max), so a single run can include multiple as_of dates in the output. However, since the executor gap-fills one day at a time, each run processes a single effective date, producing one as_of date's worth of output per run.
- The trailer `{date}` token reflects `__maxEffectiveDate`, which matches the single effective date per run.

## Edge Cases

1. **Integer division for approval_rate**: Because the SQL casts both numerator and denominator to INTEGER before dividing, the approval_rate will always be 0 (if any transactions are declined) or 1 (only if 100% are approved). This appears to be a bug — the rate loses all granularity.
   - Confidence: HIGH
   - Evidence: [card_authorization_summary.json:22] `CAST(... AS INTEGER) / CAST(COUNT(*) AS INTEGER)`

2. **Weekend data**: card_transactions has data for all 7 days, while cards only has weekday data. On weekends, the INNER JOIN may produce zero rows if the DataSourcing effective date range includes only a weekend date and the cards table has no matching as_of. However, since both DataSourcing modules use the same date range, and the JOIN is on card_id (not as_of), the cross-date JOIN may still produce results if rows from different as_of dates are mixed in the registered SQLite tables.
   - Confidence: MEDIUM
   - Evidence: [DB queries showing cards has weekday-only data; card_transactions has daily data]

3. **Zero transactions for a card_type**: If all transactions in the date range are for one card type only, the other card type simply won't appear in the output (no zero-fill).
   - Confidence: HIGH
   - Evidence: [card_authorization_summary.json:22] GROUP BY produces rows only for groups with data

4. **Trailer format**: The trailer line is `TRAILER|{row_count}|{date}` where row_count is data rows only (excluding header and trailer), and date is the max effective date.
   - Confidence: HIGH
   - Evidence: [card_authorization_summary.json:29] trailerFormat definition; [Architecture.md:241] token definitions

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|------------------|
| BR-1: Join on card_id | card_authorization_summary.json:22 (SQL INNER JOIN) |
| BR-2: Group by card_type, as_of | card_authorization_summary.json:22 (SQL GROUP BY) |
| BR-3: Approved count | card_authorization_summary.json:22 (SQL CASE expression) |
| BR-4: Declined count | card_authorization_summary.json:22 (SQL CASE expression) |
| BR-5: Integer division approval_rate | card_authorization_summary.json:22 (SQL CAST/division) |
| BR-6: Dead code ROW_NUMBER | card_authorization_summary.json:22 (SQL CTE) |
| BR-7: Dead code unused_summary CTE | card_authorization_summary.json:22 (SQL CTE) |
| BR-8: Authorization status values | DB query on datalake.card_transactions |
| BR-9: Card type values | DB query on datalake.cards |
| BR-10: INNER JOIN exclusion | card_authorization_summary.json:22 (SQL INNER JOIN) |
| Writer config | card_authorization_summary.json:25-32 |
| Trailer format | card_authorization_summary.json:29 |

## Open Questions

1. **Is the integer division approval_rate intentional?** The calculation yields only 0 or 1, losing all granularity. This appears likely to be a bug in the original SQL. A correct fractional rate would use `CAST(... AS REAL)` or multiply by 100.0 before dividing. Confidence: HIGH that this is the actual behavior; MEDIUM that it is a bug (could be intentional for a binary flag).

2. **Cross-date JOIN behavior**: When the effective date range spans multiple days, both DataSourcing modules load all dates into their DataFrames. The SQL JOIN on card_id (not as_of) may produce a cross product across dates. The GROUP BY on as_of uses only `td.as_of` (from card_transactions), so the cards table's as_of is ignored. This means a card_transaction row will join to ALL as_of snapshots of that card in the cards table, potentially inflating counts. The degree of impact depends on whether the effective date range typically spans a single day (gap-fill advances one day at a time) or multiple days.
   - Confidence: MEDIUM — in practice, gap-fill processes one day at a time, so both tables would have a single as_of, making this a non-issue for normal runs. But for date-range overrides, this could produce incorrect results.
