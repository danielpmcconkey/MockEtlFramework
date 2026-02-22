# CustomerValueScore — Functional Specification Document

## Design Approach

**SQL-first.** The original External module (CustomerValueCalculator.cs) computes three component scores from four data sources using dictionary lookups and row iteration. The entire scoring formula is expressible in SQL using subqueries for each component, then combining with arithmetic.

Score computation:
- transaction_score = MIN(txn_count * 10.0, 1000)  -- 10 points per transaction, capped at 1000
- balance_score = MIN(total_balance / 1000.0, 1000) -- $1 per $1000 of balance, capped at 1000
- visit_score = MIN(visit_count * 50.0, 1000)       -- 50 points per visit, capped at 1000
- composite_score = transaction_score * 0.4 + balance_score * 0.35 + visit_score * 0.25

In SQLite, `MIN(a, b)` is a scalar function that returns the minimum of two values, equivalent to C#'s `Math.Min()`.

No External module needed.

## Anti-Patterns Eliminated

| AP Code | Present in Original? | Eliminated in V2? | How? |
|---------|---------------------|--------------------|------|
| AP-1    | N | N/A | No unused data sources |
| AP-2    | N | N/A | Not applicable |
| AP-3    | Y | Y | Replaced External module with SQL Transformation. Scoring logic (count/sum/min/arithmetic) is standard SQL. |
| AP-4    | Y | Y | Removed unused columns: `transaction_id`, `txn_type`, `amount` from transactions (only count needed); `visit_id`, `branch_id` from branch_visits (only count needed). From accounts, kept only `account_id`, `customer_id`, `current_balance`. |
| AP-5    | N | N/A | Not applicable |
| AP-6    | Y | Y | Five foreach loops replaced with SQL subqueries and JOINs |
| AP-7    | Y | Documented | All magic values documented with SQL comments: scoring multipliers (10, 50), cap (1000), balance divisor (1000), weights (0.4, 0.35, 0.25) |
| AP-8    | N | N/A | No complex SQL |
| AP-9    | N | N/A | Name accurately reflects output |
| AP-10   | N | N/A | No undeclared dependencies |

## V2 Pipeline Design

1. **DataSourcing** `customers` — `datalake.customers` (id, first_name, last_name)
2. **DataSourcing** `transactions` — `datalake.transactions` (account_id)
3. **DataSourcing** `accounts` — `datalake.accounts` (account_id, customer_id, current_balance)
4. **DataSourcing** `branch_visits` — `datalake.branch_visits` (customer_id)
5. **Transformation** `score_output` — SQL computing all scores
6. **DataFrameWriter** — writes to `double_secret_curated.customer_value_score`, Overwrite mode

## SQL Transformation Logic

```sql
SELECT
    c.id AS customer_id,
    COALESCE(c.first_name, '') AS first_name,
    COALESCE(c.last_name, '') AS last_name,
    /* transaction_score: 10 points per transaction, capped at 1000 */
    ROUND(MIN(COALESCE(tc.txn_count, 0) * 10.0, 1000), 2) AS transaction_score,
    /* balance_score: total balance / 1000, capped at 1000 (can be negative) */
    ROUND(MIN(COALESCE(bc.total_balance, 0) / 1000.0, 1000), 2) AS balance_score,
    /* visit_score: 50 points per branch visit, capped at 1000 */
    ROUND(MIN(COALESCE(vc.visit_count, 0) * 50.0, 1000), 2) AS visit_score,
    /* composite_score: weighted sum (40% transaction + 35% balance + 25% visits) */
    ROUND(
        MIN(COALESCE(tc.txn_count, 0) * 10.0, 1000) * 0.4
        + MIN(COALESCE(bc.total_balance, 0) / 1000.0, 1000) * 0.35
        + MIN(COALESCE(vc.visit_count, 0) * 50.0, 1000) * 0.25
    , 2) AS composite_score,
    c.as_of
FROM customers c
LEFT JOIN (
    /* Transaction count per customer via account ownership */
    SELECT a.customer_id, COUNT(*) AS txn_count
    FROM transactions t
    JOIN accounts a ON t.account_id = a.account_id AND t.as_of = a.as_of
    GROUP BY a.customer_id
) tc ON c.id = tc.customer_id
LEFT JOIN (
    /* Total account balance per customer */
    SELECT customer_id, SUM(current_balance) AS total_balance
    FROM accounts
    GROUP BY customer_id
) bc ON c.id = bc.customer_id
LEFT JOIN (
    /* Branch visit count per customer */
    SELECT customer_id, COUNT(*) AS visit_count
    FROM branch_visits
    GROUP BY customer_id
) vc ON c.id = vc.customer_id
ORDER BY c.id
```

**Weekend handling:** On weekends, customers has no data, so the query returns zero rows. This matches the original External module's empty guard on customers.

**Negative balance_score:** The `MIN(total_balance/1000, 1000)` only caps the upper bound at 1000. Negative balances produce negative balance_scores, matching the original behavior.

## Traceability to BRD

| BRD Requirement | FSD Design Element |
|-----------------|-------------------|
| BR-1: One row per customer | Main query FROM customers, one row per customer_id |
| BR-2: transaction_score = MIN(count * 10, 1000) | `MIN(COALESCE(tc.txn_count, 0) * 10.0, 1000)` |
| BR-3: balance_score = MIN(balance / 1000, 1000) | `MIN(COALESCE(bc.total_balance, 0) / 1000.0, 1000)` |
| BR-4: visit_score = MIN(count * 50, 1000) | `MIN(COALESCE(vc.visit_count, 0) * 50.0, 1000)` |
| BR-5: composite_score = weighted sum | `txn_score * 0.4 + bal_score * 0.35 + visit_score * 0.25` |
| BR-6: All scores rounded to 2 dp | `ROUND(..., 2)` on all four score columns |
| BR-7: Defaults to 0 for no transactions/visits | `COALESCE(..., 0)` handles NULL from LEFT JOIN |
| BR-8: Empty when customers or accounts empty | Weekend: customers empty means zero rows; accounts empty means all scores default to 0 |
| BR-9: Overwrite mode | DataFrameWriter `writeMode: "Overwrite"` |
| BR-10: Negative balance_score allowed | `MIN(negative / 1000, 1000)` allows negative values |
