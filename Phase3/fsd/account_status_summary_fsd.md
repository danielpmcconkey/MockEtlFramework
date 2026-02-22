# AccountStatusSummary -- Functional Specification Document

## Design Approach

SQL-first. The original External module (AccountStatusCounter) performs a GROUP BY count on (account_type, account_status). This is a textbook SQL aggregation.

## Anti-Patterns Eliminated

| AP Code | Present in Original? | Eliminated in V2? | How? |
|---------|---------------------|--------------------|------|
| AP-1    | Y                   | Y                  | Removed unused `segments` DataSourcing module entirely |
| AP-2    | N                   | N/A                | N/A |
| AP-3    | Y                   | Y                  | Replaced External module with SQL GROUP BY Transformation |
| AP-4    | Y                   | Y                  | Removed unused columns (account_id, customer_id, current_balance) from DataSourcing |
| AP-5    | N                   | N/A                | N/A |
| AP-6    | Y                   | Y                  | Replaced row-by-row dictionary counting with SQL GROUP BY + COUNT |
| AP-7    | N                   | N/A                | N/A |
| AP-8    | N                   | N/A                | N/A |
| AP-9    | N                   | N/A                | N/A |
| AP-10   | N                   | N/A                | N/A |

## V2 Pipeline Design

1. **DataSourcing** (`accounts`): Read from `datalake.accounts` with only the 2 columns actually used: account_type, account_status. The framework automatically appends as_of.

2. **Transformation** (`summary_result`): GROUP BY account_type, account_status, as_of with COUNT(*) to produce account_count.

3. **DataFrameWriter**: Write `summary_result` to `account_status_summary` in `double_secret_curated` schema with Overwrite mode.

## SQL Transformation Logic

```sql
SELECT
    account_type,
    account_status,
    COUNT(*) AS account_count,
    as_of
FROM accounts
GROUP BY account_type, account_status, as_of
```

The original External module takes as_of from the first row and applies it uniformly. Since all rows from DataSourcing for a single effective date have the same as_of value, grouping by as_of produces the same result -- a single as_of value per group.

The original module also applies `?.ToString() ?? ""` to coalesce NULLs, but the datalake.accounts schema enforces NOT NULL on account_type and account_status, so this is defensive-only and does not affect output.

## Traceability to BRD

| BRD Requirement | FSD Design Element |
|-----------------|-------------------|
| BR-1            | GROUP BY account_type, account_status with COUNT(*) |
| BR-2            | as_of included in GROUP BY; since all rows share same as_of per effective date, this matches taking as_of from first row |
| BR-3            | No WHERE clause; all accounts included in aggregation |
| BR-4            | SELECT produces exactly 4 columns: account_type, account_status, account_count, as_of |
| BR-5            | DataFrameWriter writeMode is "Overwrite" |
| BR-6            | When accounts is empty (weekends), GROUP BY produces zero rows |
| BR-7            | NULL coalesce is unnecessary since schema enforces NOT NULL; SQL behavior matches -- GROUP BY on non-NULL values |
