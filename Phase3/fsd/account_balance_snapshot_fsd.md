# AccountBalanceSnapshot -- Functional Specification Document

## Design Approach

SQL-first. The original External module (AccountSnapshotBuilder) performs a trivial row-by-row copy of 6 columns from the accounts DataFrame. This is a simple SELECT statement and requires no procedural logic.

## Anti-Patterns Eliminated

| AP Code | Present in Original? | Eliminated in V2? | How? |
|---------|---------------------|--------------------|------|
| AP-1    | Y                   | Y                  | Removed unused `branches` DataSourcing module entirely |
| AP-2    | N                   | N/A                | N/A |
| AP-3    | Y                   | Y                  | Replaced External module with SQL Transformation |
| AP-4    | Y                   | Y                  | Removed unused columns (open_date, interest_rate, credit_limit) from DataSourcing |
| AP-5    | N                   | N/A                | N/A |
| AP-6    | Y                   | Y                  | Replaced row-by-row iteration with single SELECT statement |
| AP-7    | N                   | N/A                | N/A |
| AP-8    | N                   | N/A                | N/A |
| AP-9    | N                   | N/A                | N/A |
| AP-10   | N                   | N/A                | N/A |

## V2 Pipeline Design

1. **DataSourcing** (`accounts`): Read from `datalake.accounts` with only the 5 columns actually used: account_id, customer_id, account_type, account_status, current_balance. The framework automatically appends as_of.

2. **Transformation** (`snapshot_result`): Simple SELECT of all 6 columns (5 sourced + as_of) from the accounts DataFrame.

3. **DataFrameWriter**: Write `snapshot_result` to `account_balance_snapshot` in `double_secret_curated` schema with Append mode.

## SQL Transformation Logic

```sql
SELECT
    account_id,
    customer_id,
    account_type,
    account_status,
    current_balance,
    as_of
FROM accounts
```

No filtering, no joins, no aggregations. All accounts for the effective date are included. The framework handles effective date injection via DataSourcing.

## Traceability to BRD

| BRD Requirement | FSD Design Element |
|-----------------|-------------------|
| BR-1            | SELECT has no WHERE clause; all accounts included |
| BR-2            | SELECT lists exactly 6 columns matching output schema |
| BR-3            | DataFrameWriter writeMode is "Append" |
| BR-4            | Framework DataSourcing handles effective dates; weekday-only data in datalake.accounts means no weekend rows |
| BR-5            | When accounts has zero rows, the SQL produces zero rows, and DataFrameWriter writes nothing |
