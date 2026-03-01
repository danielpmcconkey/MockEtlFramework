# TransactionSizeBuckets — Business Requirements Document

## Overview
Classifies transactions into size buckets based on amount ranges and produces per-bucket aggregates (count, total, average) by date. Output is a CSV file, overwritten each run.

## Output Type
CsvFileWriter

## Writer Configuration
- **source**: `size_buckets`
- **outputFile**: `Output/curated/transaction_size_buckets.csv`
- **includeHeader**: true
- **trailerFormat**: (not specified — no trailer)
- **writeMode**: Overwrite
- **lineEnding**: LF

## Source Tables

| Table | Columns | Filters | Evidence |
|-------|---------|---------|----------|
| datalake.transactions | transaction_id, account_id, txn_type, amount | Effective date range injected by executor via shared state | [transaction_size_buckets.json:6-10] |
| datalake.accounts | account_id, customer_id, account_type | Effective date range injected by executor via shared state | [transaction_size_buckets.json:12-17] |

Note: The `accounts` table is sourced but **not used** in the transformation SQL. It is registered as a SQLite table but never referenced.

## Business Rules

BR-1: Transactions are classified into five size buckets based on `amount`:
- `0-25`: amount >= 0 AND amount < 25
- `25-100`: amount >= 25 AND amount < 100
- `100-500`: amount >= 100 AND amount < 500
- `500-1000`: amount >= 500 AND amount < 1000
- `1000+`: all other amounts (effectively amount >= 1000)
- Confidence: HIGH
- Evidence: [transaction_size_buckets.json:22] CASE WHEN expressions in `bucketed` CTE

BR-2: Aggregation is performed per `amount_bucket` and `as_of` date.
- Confidence: HIGH
- Evidence: [transaction_size_buckets.json:22] `GROUP BY b.amount_bucket, b.as_of` in `summary` CTE

BR-3: `txn_count` is `COUNT(*)` per bucket per date.
- Confidence: HIGH
- Evidence: [transaction_size_buckets.json:22] `COUNT(*) AS txn_count`

BR-4: `total_amount` is `ROUND(SUM(b.amount), 2)` per bucket per date.
- Confidence: HIGH
- Evidence: [transaction_size_buckets.json:22] `ROUND(SUM(b.amount), 2) AS total_amount`

BR-5: `avg_amount` is `ROUND(AVG(b.amount), 2)` per bucket per date.
- Confidence: HIGH
- Evidence: [transaction_size_buckets.json:22] `ROUND(AVG(b.amount), 2) AS avg_amount`

BR-6: Results are ordered by `as_of ASC, amount_bucket ASC` (string sort).
- Confidence: HIGH
- Evidence: [transaction_size_buckets.json:22] `ORDER BY s.as_of, s.amount_bucket`

BR-7: The SQL uses a `ROW_NUMBER() OVER (PARTITION BY t.account_id ORDER BY t.amount DESC)` in the `txn_detail` CTE, but the `rn` column is **not used** in subsequent CTEs or the final output. It does not filter or affect the results.
- Confidence: HIGH
- Evidence: [transaction_size_buckets.json:22] `ROW_NUMBER() OVER (PARTITION BY t.account_id ORDER BY t.amount DESC) AS rn` computed in `txn_detail` but never referenced in `bucketed` or `summary`

BR-8: The bucket boundaries use `>=` on the lower bound and `<` on the upper bound (half-open intervals). The `1000+` bucket catches everything not matched by the other CASE WHEN conditions (i.e., amount >= 1000).
- Confidence: HIGH
- Evidence: [transaction_size_buckets.json:22] `WHEN td.amount >= 0 AND td.amount < 25 THEN '0-25'` etc., with `ELSE '1000+'`

BR-9: The `0-25` bucket also catches negative amounts (if any exist) only if amount >= 0. Amounts below 0 would fall through to the `ELSE '1000+'` bucket. However, actual data shows minimum amount is 20.00.
- Confidence: MEDIUM
- Evidence: [transaction_size_buckets.json:22] CASE WHEN logic; database query shows `MIN(amount) = 20.00`

## Output Schema

| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| amount_bucket | transactions.amount | CASE WHEN classification into 5 buckets | [transaction_size_buckets.json:22] |
| txn_count | transactions | `COUNT(*)` per bucket per date | [transaction_size_buckets.json:22] |
| total_amount | transactions.amount | `ROUND(SUM(amount), 2)` per bucket per date | [transaction_size_buckets.json:22] |
| avg_amount | transactions.amount | `ROUND(AVG(amount), 2)` per bucket per date | [transaction_size_buckets.json:22] |
| as_of | transactions.as_of | Direct (from GROUP BY) | [transaction_size_buckets.json:22] |

## Non-Deterministic Fields
None identified.

## Write Mode Implications
- **Overwrite** mode: Each run completely replaces the CSV file. Only the latest effective date's results persist.
- On multi-day auto-advance, each subsequent day overwrites the previous. Only the final day's data survives.

## Edge Cases

1. **No transactions for an effective date**: All CTEs produce zero rows. The CSV contains only a header line.

2. **All transactions in one bucket**: Only one row per date in the output; other buckets do not appear (no zero-count filler rows).

3. **Amount exactly on bucket boundary**: Amounts at bucket boundaries are assigned to the higher bucket due to `>=` on lower bound. E.g., amount = 25 goes to `25-100`, amount = 100 goes to `100-500`.

4. **String sort on amount_bucket**: `ORDER BY amount_bucket` uses string comparison, so the order is: `0-25`, `100-500`, `1000+`, `25-100`, `500-1000`. This is lexicographic, not numeric.

5. **Unused ROW_NUMBER()**: The `rn` window function in `txn_detail` adds no value — it is computed but never referenced. This is vestigial complexity.

6. **Unused accounts source**: The accounts DataFrame is sourced but never joined or referenced in the SQL.

7. **Negative amounts**: The CASE WHEN first branch requires `amount >= 0`, so negative amounts (if any) would fall to the `ELSE '1000+'` bucket. Current data minimum is 20.00 so this is theoretical.

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|------------------|
| Five amount buckets (CASE WHEN) | [transaction_size_buckets.json:22] |
| Group by amount_bucket, as_of | [transaction_size_buckets.json:22] |
| COUNT(*) for txn_count | [transaction_size_buckets.json:22] |
| ROUND(SUM(amount), 2) for total_amount | [transaction_size_buckets.json:22] |
| ROUND(AVG(amount), 2) for avg_amount | [transaction_size_buckets.json:22] |
| ORDER BY as_of, amount_bucket (string) | [transaction_size_buckets.json:22] |
| Unused ROW_NUMBER() in CTE | [transaction_size_buckets.json:22] |
| No trailer | [transaction_size_buckets.json:26-30] |
| Overwrite write mode | [transaction_size_buckets.json:29] |
| LF line ending | [transaction_size_buckets.json:30] |
| firstEffectiveDate = 2024-10-01 | [transaction_size_buckets.json:3] |
| Unused accounts source | [transaction_size_buckets.json:12-17] vs SQL |

## Open Questions

1. **String sort on amount_bucket**: The ORDER BY uses string comparison which produces a non-intuitive order (`0-25`, `100-500`, `1000+`, `25-100`, `500-1000`). Is this intentional or a bug?
- Confidence: LOW (cannot determine intent; behavior is clearly defined by the SQL)
