# BRD: CustomerValueScore

## Overview
This job computes a composite value score for each customer based on three weighted factors: transaction activity, account balances, and branch visit frequency. Each factor produces a sub-score (capped at 1000), and the composite score is a weighted sum. It writes to `curated.customer_value_score` using Overwrite mode.

## Source Tables
| Table | Schema | Columns Used | Join/Filter Logic | Evidence |
|-------|--------|-------------|-------------------|----------|
| customers | datalake | id, first_name, last_name | Driver table; one output row per customer | [JobExecutor/Jobs/customer_value_score.json:7-11] |
| transactions | datalake | transaction_id, account_id, txn_type, amount | Used to count transactions per customer (via account lookup) | [JobExecutor/Jobs/customer_value_score.json:13-17] |
| accounts | datalake | account_id, customer_id, current_balance | Used for account_id -> customer_id mapping AND total balance computation | [JobExecutor/Jobs/customer_value_score.json:19-23] |
| branch_visits | datalake | visit_id, customer_id, branch_id | Used to count branch visits per customer | [JobExecutor/Jobs/customer_value_score.json:25-29] |

## Business Rules
BR-1: One output row is produced per customer (driven by the customers table).
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerValueCalculator.cs:86] `foreach (var custRow in customers.Rows)`
- Evidence: [curated.customer_value_score] 223 rows for as_of = 2024-10-31

BR-2: If customers or accounts is null or empty, the output is an empty DataFrame (weekend guard).
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerValueCalculator.cs:22-26] `if (customers == null || customers.Count == 0 || accounts == null || accounts.Count == 0)`

BR-3: Transaction score = min(transaction_count * 10.0, 1000), capped at 1000.
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerValueCalculator.cs:93-94] `var transactionScore = Math.Min(txnCount * 10.0m, 1000m);`

BR-4: Balance score = min(total_account_balance / 1000.0, 1000), capped at 1000.
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerValueCalculator.cs:97-98] `var balanceScore = Math.Min(totalBalance / 1000.0m, 1000m);`

BR-5: Visit score = min(visit_count * 50.0, 1000), capped at 1000.
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerValueCalculator.cs:101-102] `var visitScore = Math.Min(visitCount * 50.0m, 1000m);`

BR-6: Composite score = transaction_score * 0.4 + balance_score * 0.35 + visit_score * 0.25.
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerValueCalculator.cs:29-31] `transactionWeight = 0.4m; balanceWeight = 0.35m; visitWeight = 0.25m;`
- Evidence: [ExternalModules/CustomerValueCalculator.cs:105-107] `compositeScore = transactionScore * transactionWeight + balanceScore * balanceWeight + visitScore * visitWeight;`

BR-7: All scores (transaction_score, balance_score, visit_score, composite_score) are rounded to 2 decimal places.
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerValueCalculator.cs:114-117] `Math.Round(transactionScore, 2)`, `Math.Round(balanceScore, 2)`, `Math.Round(visitScore, 2)`, `Math.Round(compositeScore, 2)`

BR-8: Transaction counts are computed by linking transactions to customers through the account_id -> customer_id lookup.
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerValueCalculator.cs:43-56] Same account-to-customer lookup pattern as CustomerTransactionActivity

BR-9: Transactions with account_ids not found in accounts are silently skipped.
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerValueCalculator.cs:49-50] `if (customerId == 0) continue;`

BR-10: Balance score can be negative (no floor applied, only a cap at 1000).
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerValueCalculator.cs:98] `Math.Min(totalBalance / 1000.0m, 1000m)` — no `Math.Max(0, ...)` applied
- Evidence: [curated.customer_value_score] Customer 1026 has `balance_score = -1.49` (total_account_balance = -1488.00 / 1000 = -1.488, rounded to -1.49)

BR-11: Customers with no transactions get transaction_score = 0 (count defaults to 0).
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerValueCalculator.cs:93] `txnCountByCustomer.GetValueOrDefault(customerId, 0)` -> 0 * 10 = 0

BR-12: Customers with no branch visits get visit_score = 0 (count defaults to 0).
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerValueCalculator.cs:101] `visitCountByCustomer.GetValueOrDefault(customerId, 0)` -> 0 * 50 = 0

BR-13: The output is written using Overwrite mode.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/customer_value_score.json:42] `"writeMode": "Overwrite"`
- Evidence: [curated.customer_value_score] Only as_of = 2024-10-31

BR-14: The as_of is carried from the customers row.
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerValueCalculator.cs:118] `["as_of"] = custRow["as_of"]`

## Output Schema
| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| customer_id | customers.id | Direct mapping | [ExternalModules/CustomerValueCalculator.cs:110] |
| first_name | customers.first_name | Null-coalesced to empty string | [ExternalModules/CustomerValueCalculator.cs:89] |
| last_name | customers.last_name | Null-coalesced to empty string | [ExternalModules/CustomerValueCalculator.cs:90] |
| transaction_score | Computed | min(txn_count * 10, 1000), rounded to 2 dp | [ExternalModules/CustomerValueCalculator.cs:93-94, 114] |
| balance_score | Computed | min(total_balance / 1000, 1000), rounded to 2 dp | [ExternalModules/CustomerValueCalculator.cs:97-98, 115] |
| visit_score | Computed | min(visit_count * 50, 1000), rounded to 2 dp | [ExternalModules/CustomerValueCalculator.cs:101-102, 116] |
| composite_score | Computed | txn_score*0.4 + bal_score*0.35 + visit_score*0.25, rounded to 2 dp | [ExternalModules/CustomerValueCalculator.cs:105-107, 117] |
| as_of | customers.as_of | Passed through | [ExternalModules/CustomerValueCalculator.cs:118] |

## Edge Cases
- **Negative balance scores**: Balance scores can be negative because there is no floor. Customers with negative total balances get negative balance_scores. [ExternalModules/CustomerValueCalculator.cs:98]
- **Score capping**: All three sub-scores are capped at 1000 using Math.Min. The cap applies to the upper bound only, not lower. [ExternalModules/CustomerValueCalculator.cs:94, 98, 102]
- **Weekend behavior**: Customers and accounts are weekday-only (23 dates). On weekends, the guard clause returns an empty DataFrame. With Overwrite mode, this would truncate the table.
- **Branch visits on weekends**: branch_visits varies daily (including weekends), but since customers/accounts drive the guard clause, weekend dates produce no output regardless.
- **Orphan transactions**: Same as CustomerTransactionActivity — unmatched account_ids are skipped.
- **Null transactions/visits DataFrames**: If transactions or branch_visits is null, the respective score defaults to 0 (the code checks for null before iterating). [ExternalModules/CustomerValueCalculator.cs:44, 73]

## Traceability Matrix
| Requirement | Evidence Citations |
|-------------|-------------------|
| BR-1 | [ExternalModules/CustomerValueCalculator.cs:86], [curated.customer_value_score count] |
| BR-2 | [ExternalModules/CustomerValueCalculator.cs:22-26] |
| BR-3 | [ExternalModules/CustomerValueCalculator.cs:93-94] |
| BR-4 | [ExternalModules/CustomerValueCalculator.cs:97-98] |
| BR-5 | [ExternalModules/CustomerValueCalculator.cs:101-102] |
| BR-6 | [ExternalModules/CustomerValueCalculator.cs:29-31, 105-107] |
| BR-7 | [ExternalModules/CustomerValueCalculator.cs:114-117] |
| BR-8 | [ExternalModules/CustomerValueCalculator.cs:43-56] |
| BR-9 | [ExternalModules/CustomerValueCalculator.cs:49-50] |
| BR-10 | [ExternalModules/CustomerValueCalculator.cs:98], [curated.customer_value_score data] |
| BR-11 | [ExternalModules/CustomerValueCalculator.cs:93] |
| BR-12 | [ExternalModules/CustomerValueCalculator.cs:101] |
| BR-13 | [JobExecutor/Jobs/customer_value_score.json:42] |
| BR-14 | [ExternalModules/CustomerValueCalculator.cs:118] |

## Open Questions
- **Negative composite scores**: It is theoretically possible for the composite score to be negative if the balance_score is sufficiently negative and the other scores are low. This is a design decision (no floor applied). Confidence: HIGH that this is intentional per the code.
- **Score ceiling only**: The Math.Min caps at 1000 but there is no Math.Max floor at 0. Confidence: HIGH that this is by design.
