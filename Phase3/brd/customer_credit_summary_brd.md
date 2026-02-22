# CustomerCreditSummary — Business Requirements Document

## Overview

Produces a per-customer financial summary combining average credit score, total loan balance, total account (deposit) balance, and counts of loans and accounts. One row per customer in the customers table for the effective date. Output uses Overwrite mode.

## Source Tables

| Table | Schema | Columns Used | Purpose |
|-------|--------|-------------|---------|
| `datalake.customers` | datalake | id, first_name, last_name | Customer list and name; drives output (one row per customer) |
| `datalake.accounts` | datalake | account_id, customer_id, account_type, account_status, current_balance | Account balance and count aggregation |
| `datalake.credit_scores` | datalake | credit_score_id, customer_id, bureau, score | Credit score averaging |
| `datalake.loan_accounts` | datalake | loan_id, customer_id, loan_type, current_balance | Loan balance and count aggregation |
| `datalake.segments` | datalake | segment_id, segment_name | **SOURCED BUT NEVER USED** — not referenced by the External module |

## Business Rules

BR-1: One output row is produced for every customer in the customers table for the effective date, regardless of whether they have accounts, loans, or credit scores.
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerCreditSummaryBuilder.cs:74-75] Iteration is over `customers.Rows`
- Evidence: [curated.customer_credit_summary] 223 rows matches 223 customers in datalake.customers

BR-2: avg_credit_score is the arithmetic mean of all bureau scores for the customer. If the customer has no credit scores, it is NULL.
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerCreditSummaryBuilder.cs:82-89] `scoresByCustomer[customerId].Average()` if exists, else `DBNull.Value`

BR-3: total_loan_balance is the sum of current_balance across all loan accounts for the customer. Defaults to 0 if no loans.
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerCreditSummaryBuilder.cs:92-99] Sum of loan balances; defaults to 0m

BR-4: loan_count is the number of loan account records for the customer. Defaults to 0 if no loans.
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerCreditSummaryBuilder.cs:98] `loanData.count`; default 0

BR-5: total_account_balance is the sum of current_balance across all deposit/credit accounts for the customer. Defaults to 0 if no accounts.
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerCreditSummaryBuilder.cs:102-109] Sum of account balances; defaults to 0m

BR-6: account_count is the number of account records for the customer. Defaults to 0 if no accounts.
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerCreditSummaryBuilder.cs:108] `acctData.count`; default 0

BR-7: When any of the four input DataFrames (customers, accounts, credit_scores, loan_accounts) are empty, an empty output is produced.
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerCreditSummaryBuilder.cs:22-29] Null/empty checks for all four DataFrames

BR-8: The as_of value comes from each customer's row in the customers DataFrame.
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerCreditSummaryBuilder.cs:121] `["as_of"] = custRow["as_of"]`

BR-9: Output uses Overwrite mode — all data is replaced on each run.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/customer_credit_summary.json:49] `"writeMode": "Overwrite"`

BR-10: All accounts are included in the balance/count regardless of account_type or account_status.
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerCreditSummaryBuilder.cs:59-69] No filtering on accounts — all rows are included

BR-11: All loans are included regardless of loan_type or loan_status.
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerCreditSummaryBuilder.cs:46-56] No filtering on loan_accounts — all rows are included

## Output Schema

| Column | Source | Transformation |
|--------|--------|----------------|
| customer_id | customers.id | Direct |
| first_name | customers.first_name | Direct (default empty string) |
| last_name | customers.last_name | Direct (default empty string) |
| avg_credit_score | credit_scores.score | AVG across all bureaus; NULL if no scores |
| total_loan_balance | loan_accounts.current_balance | SUM; 0 if no loans |
| total_account_balance | accounts.current_balance | SUM; 0 if no accounts |
| loan_count | loan_accounts | COUNT; 0 if no loans |
| account_count | accounts | COUNT; 0 if no accounts |
| as_of | customers.as_of | From customer DataFrame row |

## Edge Cases

- **Customer with no credit scores**: avg_credit_score is NULL (BR-2).
- **Customer with no loans**: total_loan_balance = 0, loan_count = 0 (BR-3, BR-4).
- **Customer with no accounts**: total_account_balance = 0, account_count = 0 (BR-5, BR-6).
- **Weekend behavior**: Customers, accounts, credit_scores, and loan_accounts all skip weekends. When any is empty, output is empty (BR-7). So no output on weekends.
- **All four must have data**: The AND condition means if credit_scores or loan_accounts are empty for a date but customers and accounts exist, output is still empty. This seems overly strict — a customer could legitimately have no loans.

## Anti-Patterns Identified

- **AP-1: Redundant Data Sourcing** — The `segments` table is sourced via DataSourcing (segment_id, segment_name) but never referenced by the External module `CustomerCreditSummaryBuilder`. Evidence: [JobExecutor/Jobs/customer_credit_summary.json:34-38] segments sourced; [ExternalModules/CustomerCreditSummaryBuilder.cs] no reference to "segments". V2 approach: Remove the segments DataSourcing module.

- **AP-3: Unnecessary External Module** — The logic is: for each customer, compute AVG of credit scores, SUM/COUNT of loans, SUM/COUNT of accounts. This is expressible in SQL with LEFT JOINs and GROUP BY + aggregate functions. Evidence: [ExternalModules/CustomerCreditSummaryBuilder.cs:32-123] Row-by-row grouping of scores, loans, and accounts. V2 approach: Replace with a SQL Transformation using LEFT JOINs on subqueries.

- **AP-4: Unused Columns Sourced** — From accounts: `account_id`, `account_type`, `account_status` are sourced but only `customer_id` and `current_balance` are used. From credit_scores: `credit_score_id` and `bureau` are sourced but only `customer_id` and `score` are used. From loan_accounts: `loan_id` and `loan_type` are sourced but only `customer_id` and `current_balance` are used. Evidence: [JobExecutor/Jobs/customer_credit_summary.json] column lists vs [ExternalModules/CustomerCreditSummaryBuilder.cs] actual usage. V2 approach: Source only needed columns.

- **AP-5: Asymmetric NULL/Default Handling** — avg_credit_score defaults to NULL when a customer has no scores, but total_loan_balance defaults to 0 and loan_count defaults to 0 when no loans exist. Similarly, total_account_balance and account_count default to 0. The asymmetry: numeric aggregates default to 0, but the average defaults to NULL. Evidence: [ExternalModules/CustomerCreditSummaryBuilder.cs:81-89 vs 92-99]. V2 approach: This asymmetry is arguably correct (NULL average is semantically appropriate when there's nothing to average, while 0 balance/count means "none"), but it should be documented. Reproduce same behavior.

- **AP-6: Row-by-Row Iteration in External Module** — Three separate foreach loops build lookup dictionaries, then a fourth iterates customers. All of this is GROUP BY + LEFT JOIN in SQL. Evidence: [ExternalModules/CustomerCreditSummaryBuilder.cs:32-123]. V2 approach: Replace with SQL.

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|-------------------|
| BR-1 | [ExternalModules/CustomerCreditSummaryBuilder.cs:74-75] |
| BR-2 | [ExternalModules/CustomerCreditSummaryBuilder.cs:82-89] |
| BR-3 | [ExternalModules/CustomerCreditSummaryBuilder.cs:92-99] |
| BR-4 | [ExternalModules/CustomerCreditSummaryBuilder.cs:98] |
| BR-5 | [ExternalModules/CustomerCreditSummaryBuilder.cs:102-109] |
| BR-6 | [ExternalModules/CustomerCreditSummaryBuilder.cs:108] |
| BR-7 | [ExternalModules/CustomerCreditSummaryBuilder.cs:22-29] |
| BR-8 | [ExternalModules/CustomerCreditSummaryBuilder.cs:121] |
| BR-9 | [JobExecutor/Jobs/customer_credit_summary.json:49] |
| BR-10 | [ExternalModules/CustomerCreditSummaryBuilder.cs:59-69] |
| BR-11 | [ExternalModules/CustomerCreditSummaryBuilder.cs:46-56] |

## Open Questions

- **Empty-guard strictness**: The module returns empty output when ANY of the four DataFrames is empty (BR-7). This means on a date where loan_accounts has no data but customers, accounts, and credit_scores do, the output is still empty. This seems overly strict — it conflates "no data available" with "all customers have zero loans." Confidence: MEDIUM that this is an unintentional strictness rather than intentional behavior. However, V2 must reproduce the same behavior for equivalence.
