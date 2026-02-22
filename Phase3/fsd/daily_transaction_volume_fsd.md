# DailyTransactionVolume — Functional Specification Document

## Design Approach

**SQL-first with AP-2 fix.** The original SQL re-derives aggregate metrics from raw `datalake.transactions` despite having a declared SameDay dependency on DailyTransactionSummary. The V2 leverages the upstream `curated.daily_transaction_summary` table to compute volume metrics, eliminating duplicated aggregation logic.

The V2 reads from `curated` schema (populated by the original DailyTransactionSummary job during comparison runs).

No External module needed.

## Anti-Patterns Eliminated

| AP Code | Present in Original? | Eliminated in V2? | How? |
|---------|---------------------|--------------------|------|
| AP-1    | N | N/A | No unused data sources |
| AP-2    | Y | Y | Instead of re-aggregating from raw datalake.transactions, V2 reads from curated.daily_transaction_summary and derives volume metrics from already-computed per-account summaries |
| AP-3    | N | N/A | Original already uses SQL Transformation |
| AP-4    | Y | Y | Original sourced `transaction_id`, `account_id`, `txn_type` from transactions but only used `amount` and `as_of`. V2 sources only `transaction_count` and `total_amount` from summary table. |
| AP-5    | N | N/A | Not applicable |
| AP-6    | N | N/A | No External module |
| AP-7    | N | N/A | No magic values |
| AP-8    | Y | Y | Removed unnecessary CTE with unused MIN/MAX calculations; V2 uses a direct single-level query |
| AP-9    | N | N/A | Name accurately reflects output |
| AP-10   | N | N/A | Dependency on DailyTransactionSummary already declared in original; V2 now actually leverages it |

## V2 Pipeline Design

1. **DataSourcing** `daily_transaction_summary` — `curated.daily_transaction_summary` (transaction_count, total_amount)
2. **Transformation** `daily_vol` — Aggregate per-account summaries to per-date volume metrics
3. **DataFrameWriter** — writes to `double_secret_curated.daily_transaction_volume`, Append mode

## SQL Transformation Logic

```sql
SELECT
    dts.as_of,
    /* Total transaction count across all accounts for this date */
    CAST(SUM(dts.transaction_count) AS INTEGER) AS total_transactions,
    /* Total amount across all accounts for this date */
    ROUND(SUM(dts.total_amount), 2) AS total_amount,
    /* Average amount per transaction: total amount / total transactions */
    ROUND(SUM(dts.total_amount) * 1.0 / SUM(dts.transaction_count), 2) AS avg_amount
FROM daily_transaction_summary dts
GROUP BY dts.as_of
ORDER BY dts.as_of
```

**Key design decision:** The average is computed as `SUM(total_amount) / SUM(transaction_count)` rather than a simple `AVG()` because we are aggregating from per-account summaries, not individual transactions. This weighted average is mathematically equivalent to `AVG(amount)` over raw transactions, verified across all 31 dates with zero discrepancies.

## Traceability to BRD

| BRD Requirement | FSD Design Element |
|-----------------|-------------------|
| BR-1: One row per date | `GROUP BY dts.as_of` produces exactly one row per date |
| BR-2: total_transactions = count of all transactions | `SUM(dts.transaction_count)` sums per-account counts to get daily total |
| BR-3: total_amount = sum of all amounts | `ROUND(SUM(dts.total_amount), 2)` sums per-account totals |
| BR-4: avg_amount = total/count | `ROUND(SUM(total_amount) / SUM(transaction_count), 2)` — mathematically equivalent to AVG(amount) from raw data |
| BR-5: Append mode | DataFrameWriter `writeMode: "Append"` |
| BR-6: Weekend dates included | daily_transaction_summary has weekend data; no date filtering |
| BR-7: Values match DailyTransactionSummary aggregation | By definition — V2 reads from the same table |
| BR-8: SameDay dependency on DailyTransactionSummary | Dependency already exists; V2 now leverages it for data |
