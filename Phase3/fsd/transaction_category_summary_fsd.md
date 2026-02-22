# TransactionCategorySummary -- Functional Specification Document

## Design Approach

SQL-first. The original job already uses a Transformation module, but the SQL is needlessly complex with an unused CTE containing window functions (ROW_NUMBER, COUNT OVER). The V2 simplifies the SQL to a direct GROUP BY query and removes unused DataSourcing modules and columns.

## Anti-Patterns Eliminated

| AP Code | Present in Original? | Eliminated in V2? | How? |
|---------|---------------------|--------------------|------|
| AP-1    | Y                   | Y                  | Removed `segments` DataSourcing module (never referenced in SQL) |
| AP-2    | N                   | N/A                | No duplicated upstream logic |
| AP-3    | N                   | N/A                | Original already uses Transformation (not External module) |
| AP-4    | Y                   | Y                  | Removed `account_id` and `transaction_id` from transactions DataSourcing (neither needed by simplified SQL) |
| AP-5    | N                   | N/A                | No NULL/default handling involved |
| AP-6    | N                   | N/A                | No External module with row-by-row iteration |
| AP-7    | N                   | N/A                | No magic values |
| AP-8    | Y                   | Y                  | Removed unnecessary CTE with ROW_NUMBER and COUNT window functions; simplified to direct GROUP BY query |
| AP-9    | N                   | N/A                | Job name accurately describes what it does |
| AP-10   | N                   | N/A                | No undeclared dependencies |

## V2 Pipeline Design

1. **DataSourcing** `transactions`: Read `txn_type`, `amount` from `datalake.transactions`
2. **Transformation** `txn_cat_summary`: Simplified SQL with direct GROUP BY (no CTE, no window functions)
3. **DataFrameWriter**: Write `txn_cat_summary` to `double_secret_curated.transaction_category_summary` in Append mode

## SQL Transformation Logic

```sql
SELECT
    txn_type,
    as_of,
    ROUND(SUM(amount), 2) AS total_amount,
    COUNT(*) AS transaction_count,
    ROUND(AVG(amount), 2) AS avg_amount
FROM transactions
GROUP BY txn_type, as_of
ORDER BY as_of, txn_type
```

**Key design notes:**
- The original SQL wrapped this in a CTE with ROW_NUMBER() and COUNT() OVER() window functions that were never used in the outer query. The simplified version produces identical results.
- GROUP BY txn_type, as_of produces one row per transaction type per date (BR-1)
- SUM, COUNT, AVG all computed directly (BR-2)
- ROUND to 2 decimal places for total_amount and avg_amount (BR-2)
- ORDER BY as_of, txn_type for consistent ordering (BR-4)

## Traceability to BRD

| BRD Requirement | FSD Design Element |
|-----------------|-------------------|
| BR-1 | GROUP BY txn_type, as_of |
| BR-2 | ROUND(SUM(amount), 2), COUNT(*), ROUND(AVG(amount), 2) |
| BR-3 | No WHERE clause filtering txn_type |
| BR-4 | ORDER BY as_of, txn_type |
| BR-5 | DataFrameWriter writeMode: "Append" |
