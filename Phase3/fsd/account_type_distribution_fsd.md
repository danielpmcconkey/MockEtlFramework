# AccountTypeDistribution -- Functional Specification Document

## Design Approach

SQL-first. The original External module (AccountDistributionCalculator) performs a GROUP BY count by account_type, computes total accounts, and calculates percentage. This is expressible in SQL using a subquery for total count and arithmetic for percentage.

## Anti-Patterns Eliminated

| AP Code | Present in Original? | Eliminated in V2? | How? |
|---------|---------------------|--------------------|------|
| AP-1    | Y                   | Y                  | Removed unused `branches` DataSourcing module entirely |
| AP-2    | N                   | N/A                | N/A |
| AP-3    | Y                   | Y                  | Replaced External module with SQL Transformation (GROUP BY + subquery) |
| AP-4    | Y                   | Y                  | Removed unused columns (account_id, customer_id, account_status, current_balance) from DataSourcing |
| AP-5    | N                   | N/A                | N/A |
| AP-6    | Y                   | Y                  | Replaced row-by-row dictionary counting with SQL GROUP BY + COUNT |
| AP-7    | N                   | N/A                | N/A |
| AP-8    | N                   | N/A                | N/A |
| AP-9    | N                   | N/A                | N/A |
| AP-10   | N                   | N/A                | N/A |

## V2 Pipeline Design

1. **DataSourcing** (`accounts`): Read from `datalake.accounts` with only the column actually used: account_type. The framework automatically appends as_of.

2. **Transformation** (`distribution_result`): GROUP BY account_type, as_of with COUNT(*) for account_count, a correlated subquery for total_accounts, and arithmetic division for percentage.

3. **DataFrameWriter**: Write `distribution_result` to `account_type_distribution` in `double_secret_curated` schema with Overwrite mode.

## SQL Transformation Logic

```sql
SELECT
    account_type,
    COUNT(*) AS account_count,
    (SELECT COUNT(*) FROM accounts) AS total_accounts,
    CAST(COUNT(*) AS REAL) / (SELECT COUNT(*) FROM accounts) * 100.0 AS percentage,
    as_of
FROM accounts
GROUP BY account_type, as_of
```

The percentage is computed as floating-point division (CAST to REAL ensures non-integer division). The result is a double-precision value that, when written to PostgreSQL's NUMERIC(5,2) column, will be rounded to 2 decimal places -- matching the original behavior where C# double arithmetic produces the same precision and the database rounds on storage.

Example verification: Checking = 96/277 * 100 = 34.6570... -> stored as 34.66 in NUMERIC(5,2).

## Traceability to BRD

| BRD Requirement | FSD Design Element |
|-----------------|-------------------|
| BR-1            | GROUP BY account_type with COUNT(*) |
| BR-2            | Subquery (SELECT COUNT(*) FROM accounts) computes total |
| BR-3            | CAST(COUNT(*) AS REAL) / total * 100.0 matches C# double division |
| BR-4            | as_of included in GROUP BY; equivalent to taking from first row since all rows share same as_of |
| BR-5            | SELECT produces exactly 5 columns: account_type, account_count, total_accounts, percentage, as_of |
| BR-6            | DataFrameWriter writeMode is "Overwrite" |
| BR-7            | When accounts is empty, GROUP BY produces zero rows |
| BR-8            | NULL coalesce unnecessary since schema enforces NOT NULL on account_type |
| BR-9            | REAL division produces floating-point result; NUMERIC(5,2) in PostgreSQL rounds to 2 decimal places |
