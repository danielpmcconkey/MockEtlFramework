# LargeTransactionLog -- Functional Specification Document

## Design Approach

SQL-first. The original External module (LargeTransactionProcessor) performs a filter (amount > 500) and two-step LEFT JOIN (transactions -> accounts -> customers). This is a standard multi-table SQL JOIN. The V2 replaces the External module with a Transformation step and removes unused DataSourcing modules.

## Anti-Patterns Eliminated

| AP Code | Present in Original? | Eliminated in V2? | How? |
|---------|---------------------|--------------------|------|
| AP-1    | Y                   | Y                  | Removed `addresses` DataSourcing module (never referenced by External module) |
| AP-2    | N                   | N/A                | No duplicated upstream logic |
| AP-3    | Y                   | Y                  | Replaced External module with SQL Transformation |
| AP-4    | Y                   | Y                  | Reduced accounts columns from 9 to 2 (only `account_id`, `customer_id` needed) |
| AP-5    | N                   | N/A                | NULL/default handling is consistent (COALESCE to '' for names, 0 for missing customer_id) |
| AP-6    | Y                   | Y                  | Three foreach loops replaced by set-based SQL JOINs |
| AP-7    | Y                   | Y (documented)     | Threshold 500 documented in SQL comment; value retained for output match |
| AP-8    | N                   | N/A                | No overly complex SQL in original (it was C# code) |
| AP-9    | N                   | N/A                | Job name accurately describes what it does |
| AP-10   | N                   | N/A                | No undeclared dependencies |

## V2 Pipeline Design

1. **DataSourcing** `transactions`: Read `transaction_id`, `account_id`, `txn_type`, `amount`, `description`, `txn_timestamp` from `datalake.transactions`
2. **DataSourcing** `accounts`: Read `account_id`, `customer_id` from `datalake.accounts`
3. **DataSourcing** `customers`: Read `id`, `first_name`, `last_name` from `datalake.customers`
4. **Transformation** `large_txn_result`: SQL query that filters amount > 500, LEFT JOINs transactions -> accounts -> customers
5. **DataFrameWriter**: Write `large_txn_result` to `double_secret_curated.large_transaction_log` in Append mode

## SQL Transformation Logic

```sql
SELECT
    t.transaction_id,
    t.account_id,
    -- Default customer_id to 0 if no matching account found (BR-3)
    COALESCE(a.customer_id, 0) AS customer_id,
    -- Default names to empty string if no matching customer found (BR-4)
    COALESCE(c.first_name, '') AS first_name,
    COALESCE(c.last_name, '') AS last_name,
    t.txn_type,
    t.amount,
    t.description,
    t.txn_timestamp,
    t.as_of
FROM transactions t
LEFT JOIN accounts a
    ON t.account_id = a.account_id
    AND t.as_of = a.as_of
LEFT JOIN customers c
    ON a.customer_id = c.id
    AND a.as_of = c.as_of
WHERE t.amount > 500  -- Large transaction threshold: transactions exceeding $500 (BR-1)
```

**Key design notes:**
- Two-step LEFT JOIN chain: transactions -> accounts (by account_id) -> customers (by customer_id)
- COALESCE(a.customer_id, 0) reproduces the original GetValueOrDefault(accountId, 0) behavior (BR-3)
- COALESCE for names reproduces GetValueOrDefault(customerId, ("", "")) (BR-4)
- Append mode means rows accumulate across dates

## Traceability to BRD

| BRD Requirement | FSD Design Element |
|-----------------|-------------------|
| BR-1 | WHERE clause: `t.amount > 500` |
| BR-2 | Two-step LEFT JOIN chain: transactions -> accounts -> customers |
| BR-3 | COALESCE(a.customer_id, 0) for missing account lookup |
| BR-4 | COALESCE(c.first_name, '') and COALESCE(c.last_name, '') for missing customer |
| BR-5 | DataFrameWriter writeMode: "Append" |
| BR-6 | Framework handles empty DataFrames natively; SQL produces empty result if no rows match |
