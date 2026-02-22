# HighBalanceAccounts -- Functional Specification Document

## Design Approach

SQL-first. The original External module (HighBalanceFilter) performs a simple filter (balance > 10000) and LEFT JOIN (accounts to customers). This is a single SQL query with no procedural logic required. The V2 replaces the External module with a Transformation step.

## Anti-Patterns Eliminated

| AP Code | Present in Original? | Eliminated in V2? | How? |
|---------|---------------------|--------------------|------|
| AP-1    | N                   | N/A                | No redundant DataSourcing in original |
| AP-2    | N                   | N/A                | No duplicated upstream logic |
| AP-3    | Y                   | Y                  | Replaced External module with SQL Transformation |
| AP-4    | Y                   | Y                  | Removed `account_status` from accounts DataSourcing columns |
| AP-5    | N                   | N/A                | NULL handling is consistent (COALESCE to '' for both name fields) |
| AP-6    | Y                   | Y                  | Row-by-row foreach replaced by set-based SQL JOIN + WHERE |
| AP-7    | Y                   | Y (documented)     | Threshold 10000 documented in SQL comment; value retained for output match |
| AP-8    | N                   | N/A                | Original SQL was not overly complex (it was C# code) |
| AP-9    | N                   | N/A                | Job name accurately describes what it does |
| AP-10   | N                   | N/A                | No undeclared dependencies |

## V2 Pipeline Design

1. **DataSourcing** `accounts`: Read `account_id`, `customer_id`, `account_type`, `current_balance` from `datalake.accounts`
2. **DataSourcing** `customers`: Read `id`, `first_name`, `last_name` from `datalake.customers`
3. **Transformation** `high_balance_result`: SQL query that filters accounts with balance > 10000, LEFT JOINs to customers, and COALESCEs names to empty string
4. **DataFrameWriter**: Write `high_balance_result` to `double_secret_curated.high_balance_accounts` in Overwrite mode

## SQL Transformation Logic

```sql
SELECT
    a.account_id,
    a.customer_id,
    a.account_type,
    a.current_balance,
    -- Default to empty string if no matching customer found (BR-3)
    COALESCE(c.first_name, '') AS first_name,
    COALESCE(c.last_name, '') AS last_name,
    a.as_of
FROM accounts a
LEFT JOIN customers c
    ON a.customer_id = c.id
    AND a.as_of = c.as_of
WHERE a.current_balance > 10000  -- High-balance threshold: accounts exceeding $10,000 (BR-1)
```

**Key design notes:**
- The LEFT JOIN with as_of matching ensures date-aligned lookups (same effective date for both tables)
- COALESCE to '' reproduces the original GetValueOrDefault(("", "")) behavior (BR-3)
- No filter on account_type or account_status (BR-5)

## Traceability to BRD

| BRD Requirement | FSD Design Element |
|-----------------|-------------------|
| BR-1 | WHERE clause: `a.current_balance > 10000` |
| BR-2 | LEFT JOIN to customers + SELECT first_name, last_name |
| BR-3 | COALESCE(c.first_name, '') and COALESCE(c.last_name, '') |
| BR-4 | DataFrameWriter writeMode: "Overwrite" |
| BR-5 | No WHERE clause on account_type or account_status |
| BR-6 | Framework handles empty DataFrames natively; SQL produces empty result if no rows match |
