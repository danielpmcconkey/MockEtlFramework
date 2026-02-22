# CustomerValueScore — Business Requirements Document

## Overview

The CustomerValueScore job computes a composite value score for each customer based on three weighted components: transaction activity, account balances, and branch visit frequency. Output uses Overwrite mode, retaining only the most recent effective date's data.

## Source Tables

| Table | Alias in Config | Columns Sourced | Purpose |
|-------|----------------|-----------------|---------|
| `datalake.customers` | customers | id, first_name, last_name | Customer identity; drives output (one row per customer) |
| `datalake.transactions` | transactions | transaction_id, account_id, txn_type, amount | Transaction count per customer (via account lookup) |
| `datalake.accounts` | accounts | account_id, customer_id, current_balance | Maps account_id to customer_id; total balance per customer |
| `datalake.branch_visits` | branch_visits | visit_id, customer_id, branch_id | Visit count per customer |

- Join logic: The External module builds a `account_id -> customer_id` lookup from accounts, then counts transactions per customer via that lookup. Branch visits are counted directly by customer_id. All lookups are dictionary-based in C#.
- Evidence: [ExternalModules/CustomerValueCalculator.cs:34-82]

## Business Rules

BR-1: One output row is produced per customer present in the customers table for that effective date.
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerValueCalculator.cs:86-87] Iterates `customers.Rows`.
- Evidence: [curated.customer_value_score] 223 rows for as_of = 2024-10-31, matching customer count.

BR-2: transaction_score = MIN(transaction_count * 10.0, 1000), where transaction_count is the number of transactions linked to the customer's accounts.
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerValueCalculator.cs:93-94] `var transactionScore = Math.Min(txnCount * 10.0m, 1000m);`

BR-3: balance_score = MIN(total_balance / 1000.0, 1000), where total_balance is the sum of current_balance across all of the customer's accounts.
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerValueCalculator.cs:97-98] `var balanceScore = Math.Min(totalBalance / 1000.0m, 1000m);`

BR-4: visit_score = MIN(visit_count * 50.0, 1000), where visit_count is the number of branch visits for the customer.
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerValueCalculator.cs:101-102] `var visitScore = Math.Min(visitCount * 50.0m, 1000m);`

BR-5: composite_score = transaction_score * 0.4 + balance_score * 0.35 + visit_score * 0.25.
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerValueCalculator.cs:29-31] Weights defined as constants: `transactionWeight = 0.4m`, `balanceWeight = 0.35m`, `visitWeight = 0.25m`.
- Evidence: [ExternalModules/CustomerValueCalculator.cs:105-107] `compositeScore = transactionScore * transactionWeight + balanceScore * balanceWeight + visitScore * visitWeight`

BR-6: All scores are rounded to 2 decimal places.
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerValueCalculator.cs:114-117] `Math.Round(..., 2)` applied to all four score values.

BR-7: Customers with no transactions get transaction_score = 0. Customers with no branch visits get visit_score = 0.
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerValueCalculator.cs:93,101] `GetValueOrDefault(customerId, 0)` returns 0 when no data exists.

BR-8: Customers with no accounts do not appear in the output (empty guard on both customers and accounts).
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerValueCalculator.cs:22-25] `if (customers == null || customers.Count == 0 || accounts == null || accounts.Count == 0)` returns empty DataFrame.

BR-9: Output uses Overwrite write mode.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/customer_value_score.json:42] `"writeMode": "Overwrite"`
- Evidence: [curated.customer_value_score] Only has data for as_of = 2024-10-31 (single date).

BR-10: Balance_score can be negative if total_balance is negative (no floor at 0).
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerValueCalculator.cs:98] `Math.Min(totalBalance / 1000.0m, 1000m)` — Min only caps at 1000, doesn't floor at 0.
- Evidence: [curated.customer_value_score] Customer 1026 has balance_score = -1.49, customer 1027 has balance_score = -0.81 (negative balances exist).

## Output Schema

| Column | Type | Source | Transformation |
|--------|------|--------|---------------|
| customer_id | integer | `customers.id` | Convert.ToInt32 |
| first_name | varchar(100) | `customers.first_name` | ToString with empty string fallback |
| last_name | varchar(100) | `customers.last_name` | ToString with empty string fallback |
| transaction_score | numeric(8,2) | Calculated | MIN(txn_count * 10, 1000), rounded to 2 dp |
| balance_score | numeric(8,2) | Calculated | MIN(total_balance / 1000, 1000), rounded to 2 dp |
| visit_score | numeric(8,2) | Calculated | MIN(visit_count * 50, 1000), rounded to 2 dp |
| composite_score | numeric(8,2) | Calculated | Weighted sum of three scores, rounded to 2 dp |
| as_of | date | `customers.as_of` | Passthrough |

## Edge Cases

- **Weekend dates:** The customers and accounts tables have no weekend data. When run for a weekend date, the empty guard triggers (accounts empty), producing an empty DataFrame. Branch visits and transactions DO have weekend data but cannot be processed without customers/accounts.
- **Negative balances:** balance_score can be negative. The MIN function only caps the upper bound at 1000 — there is no lower bound cap. Evidence: actual output shows negative balance_scores.
- **Orphan transactions:** Transactions whose account_id is not in the accounts table are silently skipped (customerId = 0, continue). Evidence: [ExternalModules/CustomerValueCalculator.cs:50]
- **Score cap at 1000:** Each component score is capped at 1000 via Math.Min, meaning composite_score has a theoretical maximum of 1000 * 0.4 + 1000 * 0.35 + 1000 * 0.25 = 1000.
- **Customers with no transactions and no visits:** Get transaction_score = 0, visit_score = 0, composite_score = balance_score * 0.35.

## Anti-Patterns Identified

- **AP-3: Unnecessary External Module** — The scoring logic (count transactions per customer, sum balances, count visits, compute weighted score) is expressible in SQL using JOINs, GROUP BY, CASE/LEAST for capping, and arithmetic. V2 approach: Replace with SQL Transformation using subqueries or CTEs for each component score, then combine with a weighted sum.

- **AP-4: Unused Columns Sourced** — From `transactions`: `transaction_id`, `txn_type`, and `amount` are sourced but only the count of transactions matters (amount is not used in scoring). From `accounts`: `current_balance` is used but `account_id` and `customer_id` are used for lookups. From `branch_visits`: `visit_id` and `branch_id` are sourced but only the count matters. V2 approach: Source only necessary columns — for transactions, only `account_id` is needed; for branch_visits, only `customer_id`.

- **AP-6: Row-by-Row Iteration in External Module** — Five separate foreach loops [ExternalModules/CustomerValueCalculator.cs:36,46,62,75,86] build lookups and iterate customers. All are set-based operations. V2 approach: SQL with JOINs and GROUP BY.

- **AP-7: Hardcoded Magic Values** — Multiple unexplained constants: scoring multipliers (10.0, 50.0, 1000.0), weight percentages (0.4, 0.35, 0.25), and the balance divisor (1000.0). [ExternalModules/CustomerValueCalculator.cs:29-31,94,98,102]. V2 approach: Keep values but add SQL comments explaining business meaning (e.g., "10 points per transaction, capped at 1000", "40% weight for transaction activity").

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|-------------------|
| BR-1 | [ExternalModules/CustomerValueCalculator.cs:86-87], curated 223 rows |
| BR-2 | [ExternalModules/CustomerValueCalculator.cs:93-94] |
| BR-3 | [ExternalModules/CustomerValueCalculator.cs:97-98] |
| BR-4 | [ExternalModules/CustomerValueCalculator.cs:101-102] |
| BR-5 | [ExternalModules/CustomerValueCalculator.cs:29-31,105-107] |
| BR-6 | [ExternalModules/CustomerValueCalculator.cs:114-117] |
| BR-7 | [ExternalModules/CustomerValueCalculator.cs:93,101] GetValueOrDefault |
| BR-8 | [ExternalModules/CustomerValueCalculator.cs:22-25] |
| BR-9 | [JobExecutor/Jobs/customer_value_score.json:42], curated single date |
| BR-10 | [ExternalModules/CustomerValueCalculator.cs:98], curated negative values |

## Open Questions

- **Balance_score floor:** The lack of a lower bound on balance_score (can be negative) may be intentional (penalizing customers with negative balances) or a bug. This affects composite_score as well.
  - Confidence: MEDIUM — the code behavior is clear (no floor), but the business intent is ambiguous. The V2 must reproduce this behavior to match output.

- **txn_type and amount unused in scoring:** The transactions table sources `txn_type` and `amount` but the scoring only counts transactions (ignores whether they are debits or credits, and ignores amounts). This may be an intentional simplification or an oversight.
  - Confidence: MEDIUM — sourcing unused columns is clearly AP-4, but whether the scoring SHOULD consider amounts is a business question.
