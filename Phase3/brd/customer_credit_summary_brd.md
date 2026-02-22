# BRD: CustomerCreditSummary

## Overview
This job produces a per-customer credit summary that aggregates credit scores (averaged across bureaus), total loan balances, and total account balances for every customer. It writes one row per customer to `curated.customer_credit_summary` using Overwrite mode, meaning only the most recent effective date's snapshot is retained.

## Source Tables
| Table | Schema | Columns Used | Join/Filter Logic | Evidence |
|-------|--------|-------------|-------------------|----------|
| customers | datalake | id, first_name, last_name | Driver table; iterated to produce one output row per customer | [JobExecutor/Jobs/customer_credit_summary.json:7-11] DataSourcing config |
| accounts | datalake | account_id, customer_id, account_type, account_status, current_balance | Grouped by customer_id to compute total_account_balance and account_count | [JobExecutor/Jobs/customer_credit_summary.json:13-17] DataSourcing config |
| credit_scores | datalake | credit_score_id, customer_id, bureau, score | Grouped by customer_id; scores averaged to produce avg_credit_score | [JobExecutor/Jobs/customer_credit_summary.json:19-23] DataSourcing config |
| loan_accounts | datalake | loan_id, customer_id, loan_type, current_balance | Grouped by customer_id to compute total_loan_balance and loan_count | [JobExecutor/Jobs/customer_credit_summary.json:25-29] DataSourcing config |
| segments | datalake | segment_id, segment_name | Sourced into shared state but NOT used by the External module | [JobExecutor/Jobs/customer_credit_summary.json:31-35] DataSourcing config; [ExternalModules/CustomerCreditSummaryBuilder.cs] no reference to "segments" |

## Business Rules
BR-1: One output row is produced per customer (driven by the customers table).
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerCreditSummaryBuilder.cs:74] `foreach (var custRow in customers.Rows)` — iterates all customer rows
- Evidence: [curated.customer_credit_summary] `SELECT COUNT(*) = 223` matches `SELECT COUNT(*) FROM datalake.customers WHERE as_of = '2024-10-31'` = 223

BR-2: Average credit score is computed as the arithmetic mean of all bureau scores for a customer.
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerCreditSummaryBuilder.cs:32-42] Groups credit scores by customer_id into `List<decimal>`, then [line 84] `scoresByCustomer[customerId].Average()`
- Evidence: [datalake.credit_scores] Each customer has 3 rows (Equifax, TransUnion, Experian); e.g., customer 1001 has scores 843, 836, 850 -> avg = 843.0

BR-3: If a customer has no credit scores, avg_credit_score is set to DBNull (NULL in database).
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerCreditSummaryBuilder.cs:86-89] `else { avgCreditScore = DBNull.Value; }`
- Evidence: [curated.customer_credit_summary] `SELECT COUNT(*) WHERE avg_credit_score IS NULL` = 0, meaning all 223 customers currently have credit scores in the data

BR-4: Total loan balance is the sum of current_balance from loan_accounts for each customer.
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerCreditSummaryBuilder.cs:45-56] Groups loan_accounts by customer_id, sums `current_balance`
- Evidence: [curated.customer_credit_summary] Customers with no loans show `total_loan_balance = 0.00`, `loan_count = 0`

BR-5: Customers with no loans get total_loan_balance = 0 and loan_count = 0.
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerCreditSummaryBuilder.cs:92-93] Defaults `totalLoanBalance = 0m; loanCount = 0;` before checking dictionary
- Evidence: [curated.customer_credit_summary] Verified: 133 customers have no loans; they show `loan_count = 0`

BR-6: Total account balance is the sum of current_balance from accounts for each customer.
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerCreditSummaryBuilder.cs:59-70] Groups accounts by customer_id, sums `current_balance`

BR-7: Customers with no accounts get total_account_balance = 0 and account_count = 0.
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerCreditSummaryBuilder.cs:103-104] Defaults `totalAccountBalance = 0m; accountCount = 0;`

BR-8: The segments table is sourced but not used by the External module (dead data sourcing).
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerCreditSummaryBuilder.cs] No reference to variable `segments` or shared state key `"segments"` — the module only accesses `customers`, `accounts`, `credit_scores`, `loan_accounts`
- Evidence: [JobExecutor/Jobs/customer_credit_summary.json:31-35] Segments is sourced as DataSourcing module

BR-9: If any of the four required DataFrames (customers, accounts, credit_scores, loan_accounts) is null or empty, the job returns an empty DataFrame.
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerCreditSummaryBuilder.cs:22-29] Guard clause returns empty DataFrame if any input is null/empty

BR-10: The output is written using Overwrite mode, truncating the target table before each write.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/customer_credit_summary.json:48] `"writeMode": "Overwrite"`
- Evidence: [curated.customer_credit_summary] Only contains data for `as_of = 2024-10-31` (latest date)

BR-11: The as_of column is carried from the customers DataFrame row.
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerCreditSummaryBuilder.cs:122] `["as_of"] = custRow["as_of"]`

BR-12: Account balances can be negative (no filtering applied).
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerCreditSummaryBuilder.cs:64] `Convert.ToDecimal(row["current_balance"])` — no sign check
- Evidence: [curated.customer_credit_summary] Customer 1026 has `total_account_balance = -1488.00`

BR-13: All account types and statuses are included (no filtering on account_type or account_status).
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerCreditSummaryBuilder.cs:59-70] Iterates all account rows without filtering
- Evidence: [JobExecutor/Jobs/customer_credit_summary.json:14-16] Columns sourced include account_type and account_status but the External module does not filter on them

## Output Schema
| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| customer_id | customers.id | Direct mapping | [ExternalModules/CustomerCreditSummaryBuilder.cs:113] |
| first_name | customers.first_name | Null-coalesced to empty string | [ExternalModules/CustomerCreditSummaryBuilder.cs:77] `?? ""` |
| last_name | customers.last_name | Null-coalesced to empty string | [ExternalModules/CustomerCreditSummaryBuilder.cs:78] `?? ""` |
| avg_credit_score | credit_scores.score | Average of all scores per customer; NULL if no scores | [ExternalModules/CustomerCreditSummaryBuilder.cs:82-89] |
| total_loan_balance | loan_accounts.current_balance | Sum per customer; 0 if no loans | [ExternalModules/CustomerCreditSummaryBuilder.cs:92-98] |
| total_account_balance | accounts.current_balance | Sum per customer; 0 if no accounts | [ExternalModules/CustomerCreditSummaryBuilder.cs:103-109] |
| loan_count | loan_accounts | Count of loan rows per customer; 0 if no loans | [ExternalModules/CustomerCreditSummaryBuilder.cs:98] |
| account_count | accounts | Count of account rows per customer; 0 if no accounts | [ExternalModules/CustomerCreditSummaryBuilder.cs:108] |
| as_of | customers.as_of | Passed through from customers row | [ExternalModules/CustomerCreditSummaryBuilder.cs:122] |

## Edge Cases
- **NULL handling for names**: first_name and last_name are null-coalesced to empty string (`?? ""`). [ExternalModules/CustomerCreditSummaryBuilder.cs:77-78]
- **NULL avg_credit_score**: Set to `DBNull.Value` when customer has no credit score records. [ExternalModules/CustomerCreditSummaryBuilder.cs:88]
- **Zero loans/accounts**: Customers with no matching loan or account records get 0 for balance and count. [ExternalModules/CustomerCreditSummaryBuilder.cs:92-93, 103-104]
- **Weekend/holiday behavior**: The customers table only has weekday data (23 dates for Oct 2024). On weekends, the DataSourcing module would return no rows for customers, triggering the empty-DataFrame guard clause and producing zero output rows. Since writeMode is Overwrite, this would truncate the table and write nothing. However, the executor only runs for effective dates where source data exists, so this scenario is mitigated by the framework.
- **Empty input guard**: If any of the four key DataFrames is null or empty, the output is an empty DataFrame with the correct column structure. [ExternalModules/CustomerCreditSummaryBuilder.cs:22-29]
- **Negative balances**: Account balances can be negative and are included in totals without filtering. [curated.customer_credit_summary] e.g., customer 1026 total_account_balance = -1488.00

## Traceability Matrix
| Requirement | Evidence Citations |
|-------------|-------------------|
| BR-1 | [ExternalModules/CustomerCreditSummaryBuilder.cs:74], [curated.customer_credit_summary row count] |
| BR-2 | [ExternalModules/CustomerCreditSummaryBuilder.cs:32-42, 84], [datalake.credit_scores sample] |
| BR-3 | [ExternalModules/CustomerCreditSummaryBuilder.cs:86-89], [curated.customer_credit_summary NULL check] |
| BR-4 | [ExternalModules/CustomerCreditSummaryBuilder.cs:45-56] |
| BR-5 | [ExternalModules/CustomerCreditSummaryBuilder.cs:92-93], [curated.customer_credit_summary loan_count=0] |
| BR-6 | [ExternalModules/CustomerCreditSummaryBuilder.cs:59-70] |
| BR-7 | [ExternalModules/CustomerCreditSummaryBuilder.cs:103-104] |
| BR-8 | [ExternalModules/CustomerCreditSummaryBuilder.cs], [JobExecutor/Jobs/customer_credit_summary.json:31-35] |
| BR-9 | [ExternalModules/CustomerCreditSummaryBuilder.cs:22-29] |
| BR-10 | [JobExecutor/Jobs/customer_credit_summary.json:48], [curated.customer_credit_summary dates] |
| BR-11 | [ExternalModules/CustomerCreditSummaryBuilder.cs:122] |
| BR-12 | [ExternalModules/CustomerCreditSummaryBuilder.cs:64], [curated.customer_credit_summary data] |
| BR-13 | [ExternalModules/CustomerCreditSummaryBuilder.cs:59-70], [JobExecutor/Jobs/customer_credit_summary.json:14-16] |

## Open Questions
- **Unused segments sourcing**: The segments table is loaded into shared state but never used by the External module. This appears to be dead code in the job configuration. Confidence: HIGH that it is intentionally unused (or a leftover from an earlier version).
- **Cross-table date alignment**: The credit_scores, loan_accounts, and accounts tables all have the same weekday-only date pattern as customers. If data were misaligned (e.g., accounts had data for a date but customers did not), the guard clause would produce an empty output. This is unlikely given the current data but is a theoretical edge case. Confidence: MEDIUM that no cross-table date misalignment occurs.
