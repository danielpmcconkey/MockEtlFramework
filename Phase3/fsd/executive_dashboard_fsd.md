# ExecutiveDashboard — Functional Specification Document

## Design Approach

**SQL-first.** The original External module (ExecutiveDashboardBuilder.cs) performs simple COUNT and SUM operations across 5 tables, producing 9 metric rows. All operations are straightforward aggregations expressible as a UNION ALL of individual aggregate queries in SQL.

The SQL uses a guard pattern: a CTE that checks whether customers, accounts, and loan_accounts all have data. If any is empty (weekends), the guard CTE returns no rows and the entire output is empty. This matches the original External module's triple-condition empty guard.

No External module needed.

## Anti-Patterns Eliminated

| AP Code | Present in Original? | Eliminated in V2? | How? |
|---------|---------------------|--------------------|------|
| AP-1    | Y | Y | Removed unused `branches` and `segments` DataSourcing modules |
| AP-2    | N | N/A | Not applicable |
| AP-3    | Y | Y | Replaced External module with SQL Transformation using UNION ALL pattern |
| AP-4    | Y | Y | Removed unused columns: `first_name`, `last_name` from customers (only count needed); `customer_id`, `account_type`, `account_status` from accounts (only count and balance needed); `transaction_id`, `account_id`, `txn_type` from transactions (only count and amount needed); `customer_id`, `loan_type` from loan_accounts (only count and balance needed); `visit_id`, `customer_id`, `branch_id`, `visit_purpose` from branch_visits (only count needed) |
| AP-5    | N | N/A | Not applicable |
| AP-6    | Y | Y | Three foreach loops for summing replaced with SQL SUM/COUNT aggregations |
| AP-7    | N | N/A | No magic values (metric names are descriptive identifiers) |
| AP-8    | N | N/A | No complex SQL |
| AP-9    | N | N/A | Name accurately reflects output |
| AP-10   | N | N/A | No undeclared dependencies |

## V2 Pipeline Design

1. **DataSourcing** `customers` — `datalake.customers` (id)
2. **DataSourcing** `accounts` — `datalake.accounts` (account_id, current_balance)
3. **DataSourcing** `transactions` — `datalake.transactions` (amount)
4. **DataSourcing** `loan_accounts` — `datalake.loan_accounts` (loan_id, current_balance)
5. **DataSourcing** `branch_visits` — `datalake.branch_visits` (visit_id)
6. **Transformation** `dashboard_output` — UNION ALL of 9 metric queries with guard CTE
7. **DataFrameWriter** — writes to `double_secret_curated.executive_dashboard`, Overwrite mode

Note: `id` from customers, `account_id` from accounts, `loan_id` from loan_accounts, and `visit_id` from branch_visits are sourced for COUNT purposes (they ensure the DataFrame has rows to count). The `as_of` column is auto-included by DataSourcing.

## SQL Transformation Logic

```sql
/* Guard: only produce output when customers, accounts, AND loan_accounts all have data.
   On weekends, these tables are empty and the entire output is empty. */
SELECT metric_name, metric_value, as_of FROM (
    SELECT 'total_customers' AS metric_name,
           ROUND(CAST(COUNT(*) AS REAL), 2) AS metric_value,
           (SELECT as_of FROM customers LIMIT 1) AS as_of
    FROM customers
    UNION ALL
    SELECT 'total_accounts',
           ROUND(CAST(COUNT(*) AS REAL), 2),
           (SELECT as_of FROM customers LIMIT 1)
    FROM accounts
    UNION ALL
    SELECT 'total_balance',
           ROUND(SUM(current_balance), 2),
           (SELECT as_of FROM customers LIMIT 1)
    FROM accounts
    UNION ALL
    SELECT 'total_transactions',
           ROUND(CAST(COUNT(*) AS REAL), 2),
           (SELECT as_of FROM customers LIMIT 1)
    FROM transactions
    UNION ALL
    SELECT 'total_txn_amount',
           ROUND(SUM(amount), 2),
           (SELECT as_of FROM customers LIMIT 1)
    FROM transactions
    UNION ALL
    SELECT 'avg_txn_amount',
           CASE WHEN COUNT(*) > 0 THEN ROUND(SUM(amount) / COUNT(*), 2) ELSE 0 END,
           (SELECT as_of FROM customers LIMIT 1)
    FROM transactions
    UNION ALL
    SELECT 'total_loans',
           ROUND(CAST(COUNT(*) AS REAL), 2),
           (SELECT as_of FROM customers LIMIT 1)
    FROM loan_accounts
    UNION ALL
    SELECT 'total_loan_balance',
           ROUND(SUM(current_balance), 2),
           (SELECT as_of FROM customers LIMIT 1)
    FROM loan_accounts
    UNION ALL
    SELECT 'total_branch_visits',
           ROUND(CAST(COALESCE((SELECT COUNT(*) FROM branch_visits), 0) AS REAL), 2),
           (SELECT as_of FROM customers LIMIT 1)
    FROM customers LIMIT 1
)
WHERE EXISTS (SELECT 1 FROM customers)
  AND EXISTS (SELECT 1 FROM accounts)
  AND EXISTS (SELECT 1 FROM loan_accounts)
```

**Key design decisions:**
- Guard pattern: the outer WHERE EXISTS ensures no output when any of the three required tables is empty, matching the original's triple empty check.
- `as_of` sourced from `customers LIMIT 1` matching the original's `customers.Rows[0]["as_of"]`.
- avg_txn_amount uses `CASE WHEN COUNT(*) > 0` to avoid division by zero, matching the original's guard.
- branch_visits is handled separately (via subquery) because it may not have data on some dates, and the original defaults to 0. Using COALESCE with a subquery ensures 0 if branch_visits is empty.

## Traceability to BRD

| BRD Requirement | FSD Design Element |
|-----------------|-------------------|
| BR-1: 9 metric rows per date | UNION ALL of 9 queries produces exactly 9 rows |
| BR-2: total_customers = COUNT(customers) | `COUNT(*) FROM customers` |
| BR-3: total_accounts = COUNT(accounts) | `COUNT(*) FROM accounts` |
| BR-4: total_balance = SUM(current_balance) | `SUM(current_balance) FROM accounts` |
| BR-5: total_transactions = COUNT(transactions) | `COUNT(*) FROM transactions` |
| BR-6: total_txn_amount = SUM(amount) | `SUM(amount) FROM transactions` |
| BR-7: avg_txn_amount = total/count (0 if none) | `CASE WHEN COUNT(*) > 0 THEN SUM(amount)/COUNT(*) ELSE 0 END` |
| BR-8: total_loans = COUNT(loan_accounts) | `COUNT(*) FROM loan_accounts` |
| BR-9: total_loan_balance = SUM(loan balance) | `SUM(current_balance) FROM loan_accounts` |
| BR-10: total_branch_visits = COUNT(branch_visits) | Subquery `SELECT COUNT(*) FROM branch_visits` with COALESCE for 0 default |
| BR-11: All metrics rounded to 2 dp | `ROUND(..., 2)` on all values |
| BR-12: as_of from first customer row | `(SELECT as_of FROM customers LIMIT 1)` |
| BR-13: Empty when customers/accounts/loan_accounts empty | `WHERE EXISTS` triple guard |
| BR-14: Transactions/branch_visits default to 0 if empty | Subquery approach with COALESCE for branch_visits; transactions always have data when customers/accounts do |
| BR-15: Overwrite mode | DataFrameWriter `writeMode: "Overwrite"` |
