# PaymentChannelMix — Business Requirements Document

## Overview
Produces a daily breakdown of transaction volume and amounts across three payment channels: regular transactions, card transactions, and wire transfers. The three channels are aggregated independently by date and combined via UNION ALL. Output is a single Parquet file, overwritten each run.

## Output Type
ParquetFileWriter

## Writer Configuration
- **source**: `output`
- **outputDirectory**: `Output/curated/payment_channel_mix/`
- **numParts**: 1
- **writeMode**: Overwrite

## Source Tables

| Table | Columns | Filters | Evidence |
|-------|---------|---------|----------|
| datalake.transactions | transaction_id, account_id, amount, description | Effective date range injected by executor via shared state | [payment_channel_mix.json:6-10] |
| datalake.card_transactions | card_txn_id, card_id, amount, merchant_name | Effective date range injected by executor via shared state | [payment_channel_mix.json:12-17] |
| datalake.wire_transfers | wire_id, customer_id, amount, counterparty_bank | Effective date range injected by executor via shared state | [payment_channel_mix.json:19-24] |

Note: Several sourced columns are not used in the SQL — `transaction_id`, `account_id`, `description` from transactions; `card_txn_id`, `card_id`, `merchant_name` from card_transactions; `wire_id`, `customer_id`, `counterparty_bank` from wire_transfers. Only `amount` and `as_of` are referenced.

## Business Rules

BR-1: Three payment channels are defined via literal labels: `'transaction'`, `'card'`, and `'wire'`.
- Confidence: HIGH
- Evidence: [payment_channel_mix.json:29] Three SELECT blocks with string literals `'transaction'`, `'card'`, `'wire'`

BR-2: Each channel is aggregated independently by `as_of` date using `GROUP BY`.
- Confidence: HIGH
- Evidence: [payment_channel_mix.json:29] Each SELECT block has `GROUP BY {table}.as_of`

BR-3: `txn_count` is `COUNT(*)` for each channel per date.
- Confidence: HIGH
- Evidence: [payment_channel_mix.json:29] `COUNT(*) AS txn_count` in each SELECT

BR-4: `total_amount` is `ROUND(SUM(amount), 2)` for each channel per date.
- Confidence: HIGH
- Evidence: [payment_channel_mix.json:29] `ROUND(SUM(amount), 2) AS total_amount` in each SELECT

BR-5: The three channel results are combined via `UNION ALL` (duplicates preserved, not deduplicated).
- Confidence: HIGH
- Evidence: [payment_channel_mix.json:29] `UNION ALL` between each SELECT block

BR-6: There is **no ORDER BY** clause — output row order depends on SQLite's UNION ALL execution order.
- Confidence: HIGH
- Evidence: [payment_channel_mix.json:29] No ORDER BY in the SQL

BR-7: The `as_of` column is qualified with the table name in each SELECT (`transactions.as_of`, `card_transactions.as_of`, `wire_transfers.as_of`).
- Confidence: HIGH
- Evidence: [payment_channel_mix.json:29] Explicit table-qualified `as_of` references

## Output Schema

| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| payment_channel | Literal string | `'transaction'`, `'card'`, or `'wire'` | [payment_channel_mix.json:29] |
| txn_count | Source table rows | `COUNT(*)` per channel per date | [payment_channel_mix.json:29] |
| total_amount | Source table amount | `ROUND(SUM(amount), 2)` per channel per date | [payment_channel_mix.json:29] |
| as_of | Source table as_of | Direct (from GROUP BY) | [payment_channel_mix.json:29] |

## Non-Deterministic Fields
- **Row ordering**: No ORDER BY clause means row order depends on SQLite's UNION ALL execution, which is typically first-SELECT-first but not guaranteed to be stable across SQLite versions.

## Write Mode Implications
- **Overwrite** mode: Each run completely replaces the output directory. Only the latest effective date's results persist.
- With `numParts: 1`, a single `part-00000.parquet` file is produced.
- On multi-day auto-advance, each subsequent day overwrites the previous day's output. Only the final day's data survives.

## Edge Cases

1. **No transactions in one channel**: That channel's SELECT produces zero rows via `GROUP BY`. The other channels' rows still appear in the UNION ALL output.

2. **No data in any channel**: The entire UNION ALL produces zero rows. The Parquet file will contain no data rows but will have the correct column schema.

3. **Multiple dates in effective range**: If the executor injects a multi-day range, each date appears as a separate row per channel (up to 3 rows per date). However, with Overwrite mode, only the last run's output persists.

4. **Unused sourced columns**: Many columns are sourced from each table but only `amount` and `as_of` are used in the SQL. This does not affect correctness but means more data is loaded than necessary.

5. **Card transactions vs regular transactions overlap**: The channels are independent aggregations. A transaction that exists in both `transactions` and `card_transactions` (if such overlap exists in the data model) would be counted in both channels.

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|------------------|
| Three payment channel labels | [payment_channel_mix.json:29] |
| Per-channel GROUP BY as_of | [payment_channel_mix.json:29] |
| COUNT(*) for txn_count | [payment_channel_mix.json:29] |
| ROUND(SUM(amount), 2) for total_amount | [payment_channel_mix.json:29] |
| UNION ALL combination | [payment_channel_mix.json:29] |
| No ORDER BY | [payment_channel_mix.json:29] |
| Overwrite write mode | [payment_channel_mix.json:36] |
| 1 Parquet part | [payment_channel_mix.json:35] |
| firstEffectiveDate = 2024-10-01 | [payment_channel_mix.json:3] |

## Open Questions
None.
