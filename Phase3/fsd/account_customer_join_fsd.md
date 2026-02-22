# AccountCustomerJoin -- Functional Specification Document

## Design Approach

SQL-first. The original External module (AccountCustomerDenormalizer) performs a LEFT JOIN between accounts and customers by customer_id, defaulting missing customer names to empty strings. This is directly expressible as a SQL LEFT JOIN with COALESCE.

## Anti-Patterns Eliminated

| AP Code | Present in Original? | Eliminated in V2? | How? |
|---------|---------------------|--------------------|------|
| AP-1    | Y                   | Y                  | Removed unused `addresses` DataSourcing module entirely |
| AP-2    | N                   | N/A                | N/A |
| AP-3    | Y                   | Y                  | Replaced External module with SQL Transformation (LEFT JOIN) |
| AP-4    | N                   | N/A                | All sourced columns from accounts and customers are used |
| AP-5    | N                   | N/A                | N/A |
| AP-6    | Y                   | Y                  | Replaced row-by-row dictionary lookup with SQL LEFT JOIN |
| AP-7    | N                   | N/A                | N/A |
| AP-8    | N                   | N/A                | N/A |
| AP-9    | N                   | N/A                | N/A |
| AP-10   | N                   | N/A                | N/A |

## V2 Pipeline Design

1. **DataSourcing** (`accounts`): Read from `datalake.accounts` with columns: account_id, customer_id, account_type, account_status, current_balance.

2. **DataSourcing** (`customers`): Read from `datalake.customers` with columns: id, first_name, last_name.

3. **Transformation** (`join_result`): LEFT JOIN accounts with customers on customer_id = id and matching as_of. COALESCE customer names to empty string for missing matches. When both accounts and customers are empty (weekends), the SQL produces zero rows naturally.

4. **DataFrameWriter**: Write `join_result` to `account_customer_join` in `double_secret_curated` schema with Overwrite mode.

## SQL Transformation Logic

```sql
SELECT
    a.account_id,
    a.customer_id,
    COALESCE(c.first_name, '') AS first_name,
    COALESCE(c.last_name, '') AS last_name,
    a.account_type,
    a.account_status,
    a.current_balance,
    a.as_of
FROM accounts a
LEFT JOIN customers c ON a.customer_id = c.id AND a.as_of = c.as_of
```

The LEFT JOIN ensures all accounts are included even if no matching customer exists. COALESCE converts NULL names to empty strings, matching the original GetValueOrDefault("", "") behavior.

Note: On weekends, both accounts and customers DataFrames are empty (no data in datalake for weekends). The SELECT from an empty accounts table produces zero rows, which matches the original behavior (empty guard returns empty output). The original External module also checks `accounts == null || accounts.Count == 0 || customers == null || customers.Count == 0` and returns empty -- since accounts is also empty on weekends, both implementations produce the same result.

## Traceability to BRD

| BRD Requirement | FSD Design Element |
|-----------------|-------------------|
| BR-1            | LEFT JOIN on a.customer_id = c.id joins each account with its customer |
| BR-2            | COALESCE(c.first_name, '') and COALESCE(c.last_name, '') default to empty string |
| BR-3            | No WHERE clause; all accounts included |
| BR-4            | SELECT lists exactly 8 columns in correct order |
| BR-5            | DataFrameWriter writeMode is "Overwrite" |
| BR-6            | When accounts is empty (weekends), SQL produces zero rows |
| BR-7            | LEFT JOIN with last-write-wins: SQLite handles duplicate customer IDs by returning whichever row the LEFT JOIN matches; in practice customer IDs are unique per as_of |
