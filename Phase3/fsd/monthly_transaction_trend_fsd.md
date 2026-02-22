# MonthlyTransactionTrend -- Functional Specification Document

## Design Approach

SQL-first with AP-2 dependency fix. The original job re-derives daily transaction statistics (count, total amount, average) from raw `datalake.transactions`, duplicating logic already computed by the upstream DailyTransactionVolume job and written to `curated.daily_transaction_volume`. The V2 reads from the upstream curated table instead, renaming columns to match the expected output schema. This eliminates duplicated computation and properly leverages the declared dependency.

## Anti-Patterns Eliminated

| AP Code | Present in Original? | Eliminated in V2? | How? |
|---------|---------------------|--------------------|------|
| AP-1    | Y                   | Y                  | Removed `branches` DataSourcing module (never referenced in SQL) |
| AP-2    | Y                   | Y                  | V2 reads from curated.daily_transaction_volume instead of re-deriving from datalake.transactions |
| AP-3    | N                   | N/A                | Original already uses Transformation (not External module) |
| AP-4    | Y                   | Y                  | Original sourced account_id and txn_type which were unused; V2 reads only needed columns from upstream table |
| AP-5    | N                   | N/A                | No NULL/default handling involved |
| AP-6    | N                   | N/A                | No External module with row-by-row iteration |
| AP-7    | Y                   | Y                  | Removed hardcoded date '2024-10-01' from WHERE clause (DataSourcing handles date filtering) |
| AP-8    | Y                   | Y                  | Removed unnecessary CTE wrapper; V2 is a simple column-renaming SELECT |
| AP-9    | Y                   | N (documented)     | Name "MonthlyTransactionTrend" is misleading (produces daily data, not monthly); cannot rename for output compatibility |
| AP-10   | N                   | N/A                | Dependency on DailyTransactionVolume already declared; V2 now actually uses upstream output |

## V2 Pipeline Design

1. **DataSourcing** `daily_volume`: Read `total_transactions`, `total_amount`, `avg_amount` from `curated.daily_transaction_volume`
2. **Transformation** `monthly_trend`: Rename columns to match expected output schema
3. **DataFrameWriter**: Write `monthly_trend` to `double_secret_curated.monthly_transaction_trend` in Append mode

## SQL Transformation Logic

```sql
SELECT
    as_of,
    total_transactions AS daily_transactions,
    total_amount AS daily_amount,
    avg_amount AS avg_transaction_amount
FROM daily_volume
ORDER BY as_of
```

**Key design notes:**
- Reads pre-computed values from curated.daily_transaction_volume (populated by upstream DailyTransactionVolume job)
- Column renaming: total_transactions -> daily_transactions, total_amount -> daily_amount, avg_amount -> avg_transaction_amount
- Values are already ROUND'd to 2 decimal places by the upstream job
- No WHERE clause needed: DataSourcing handles effective date filtering
- Depends on DailyTransactionVolume running first (SameDay dependency already declared)

## Traceability to BRD

| BRD Requirement | FSD Design Element |
|-----------------|-------------------|
| BR-1 | Reads pre-computed count, sum, avg from daily_transaction_volume (same computation, different source) |
| BR-2 | One row per as_of from upstream table (daily_transaction_volume has one row per date) |
| BR-3 | Rounding already applied by upstream job; values pass through as-is |
| BR-4 | No txn_type filter applied (upstream includes all transaction types in its aggregation) |
| BR-5 | DataFrameWriter writeMode: "Append" |
| BR-6 | ORDER BY as_of |
