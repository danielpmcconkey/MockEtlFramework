# CustomerCreditSummary -- Business Requirements Document

## Overview
Produces a per-customer credit summary combining average credit score, total loan balance, total account balance, and counts of both. Iterates all customers, enriching each with aggregated data from credit scores, loan accounts, and regular accounts. Output is a CSV file with LF line endings.

## Output Type
CsvFileWriter

## Writer Configuration
- **source**: `output`
- **outputFile**: `Output/curated/customer_credit_summary.csv`
- **includeHeader**: true
- **writeMode**: Overwrite
- **lineEnding**: LF
- **trailerFormat**: (not configured -- no trailer)

## Source Tables

| Table | Columns Used | Filters | Evidence |
|-------|-------------|---------|----------|
| datalake.customers | id, first_name, last_name | Effective date range (injected by executor) | [customer_credit_summary.json:8-10] |
| datalake.accounts | account_id, customer_id, account_type, account_status, current_balance | Effective date range (injected by executor) | [customer_credit_summary.json:14-17] |
| datalake.credit_scores | credit_score_id, customer_id, bureau, score | Effective date range (injected by executor) | [customer_credit_summary.json:21-24] |
| datalake.loan_accounts | loan_id, customer_id, loan_type, current_balance | Effective date range (injected by executor) | [customer_credit_summary.json:28-31] |
| datalake.segments | segment_id, segment_name | Effective date range (injected by executor) | [customer_credit_summary.json:35-37] |

## Business Rules

BR-1: ALL four primary data sources (customers, accounts, credit_scores, loan_accounts) must be non-null and non-empty for any output to be produced. If any one is null/empty, the output is empty.
- Confidence: HIGH
- Evidence: [CustomerCreditSummaryBuilder.cs:22-29] -- compound null/empty guard checks all four DataFrames.

BR-2: Average credit score is computed per customer across all bureau scores. If a customer has no credit scores, the value is `DBNull.Value`.
- Confidence: HIGH
- Evidence: [CustomerCreditSummaryBuilder.cs:82-89] -- `scoresByCustomer[customerId].Average()` or `DBNull.Value`.

BR-3: Total loan balance and loan count are aggregated per customer from the loan_accounts table.
- Confidence: HIGH
- Evidence: [CustomerCreditSummaryBuilder.cs:45-56] -- `loansByCustomer` dictionary accumulates balance and count.

BR-4: Total account balance and account count are aggregated per customer from the accounts table (all account types and statuses included, no filtering).
- Confidence: HIGH
- Evidence: [CustomerCreditSummaryBuilder.cs:59-70] -- `accountsByCustomer` dictionary accumulates balance and count.

BR-5: Customers with no loans get total_loan_balance=0 and loan_count=0.
- Confidence: HIGH
- Evidence: [CustomerCreditSummaryBuilder.cs:93-99] -- default values 0m and 0.

BR-6: Customers with no accounts get total_account_balance=0 and account_count=0.
- Confidence: HIGH
- Evidence: [CustomerCreditSummaryBuilder.cs:102-109] -- default values 0m and 0.

BR-7: The `as_of` column is taken from the customer row's as_of value.
- Confidence: HIGH
- Evidence: [CustomerCreditSummaryBuilder.cs:121] -- `custRow["as_of"]`.

BR-8: Iteration is customer-driven. Every customer in the customers DataFrame produces an output row.
- Confidence: HIGH
- Evidence: [CustomerCreditSummaryBuilder.cs:74] -- `foreach (var custRow in customers.Rows)`.

BR-9: The segments table is sourced but NOT used by the External module.
- Confidence: HIGH
- Evidence: [CustomerCreditSummaryBuilder.cs:17-20] -- only customers, accounts, credit_scores, and loan_accounts are retrieved from shared state. No reference to segments.

BR-10: Some sourced columns (account_id, account_type, account_status, credit_score_id, bureau, loan_id, loan_type) are loaded but not individually used in output. Only balances and counts derived from those rows are used.
- Confidence: HIGH
- Evidence: [CustomerCreditSummaryBuilder.cs] -- code only accesses customer_id and current_balance/score from account/loan/credit_score rows.

## Output Schema

| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| customer_id | customers.id | Cast to int via Convert.ToInt32 | [CustomerCreditSummaryBuilder.cs:76] |
| first_name | customers.first_name | ToString with null coalesce to "" | [CustomerCreditSummaryBuilder.cs:77] |
| last_name | customers.last_name | ToString with null coalesce to "" | [CustomerCreditSummaryBuilder.cs:78] |
| avg_credit_score | credit_scores.score | Average of all scores for customer, or DBNull.Value if no scores | [CustomerCreditSummaryBuilder.cs:82-89] |
| total_loan_balance | loan_accounts.current_balance | Sum of all loan balances for customer, default 0 | [CustomerCreditSummaryBuilder.cs:96] |
| total_account_balance | accounts.current_balance | Sum of all account balances for customer, default 0 | [CustomerCreditSummaryBuilder.cs:106] |
| loan_count | loan_accounts | Count of loan records for customer, default 0 | [CustomerCreditSummaryBuilder.cs:97] |
| account_count | accounts | Count of account records for customer, default 0 | [CustomerCreditSummaryBuilder.cs:107] |
| as_of | customers.as_of | Pass-through from customer row | [CustomerCreditSummaryBuilder.cs:121] |

## Non-Deterministic Fields
None identified.

## Write Mode Implications
- **Overwrite**: Each execution replaces the entire output file. For multi-day auto-advance runs, only the last effective date's output survives on disk.

## Edge Cases
- **Any source empty**: If ANY of customers, accounts, credit_scores, or loan_accounts is null/empty, the entire output is empty. This is more restrictive than other jobs which only guard on customers.
- **Customer with no credit scores**: avg_credit_score is DBNull.Value (written as empty in CSV).
- **Customer with no loans**: total_loan_balance=0, loan_count=0.
- **Customer with no accounts**: total_account_balance=0, account_count=0 (though this scenario is unlikely given the compound empty guard requires accounts to be non-empty).
- **Segments table unused**: Loaded but never referenced.
- **No rounding applied**: Balances are summed without explicit rounding. avg_credit_score from decimal Average is also not rounded.

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|------------------|
| Compound empty guard (all 4 sources) | [CustomerCreditSummaryBuilder.cs:22-29] |
| Average credit score or DBNull | [CustomerCreditSummaryBuilder.cs:82-89] |
| Loan aggregation | [CustomerCreditSummaryBuilder.cs:45-56] |
| Account aggregation | [CustomerCreditSummaryBuilder.cs:59-70] |
| Default zeros for missing data | [CustomerCreditSummaryBuilder.cs:93-99,102-109] |
| Customer-driven iteration | [CustomerCreditSummaryBuilder.cs:74] |
| Segments unused | [CustomerCreditSummaryBuilder.cs:17-20] |
| as_of from customer row | [CustomerCreditSummaryBuilder.cs:121] |
| LF line endings | [customer_credit_summary.json:52] |
| Overwrite mode | [customer_credit_summary.json:51] |

## Open Questions
- OQ-1: The segments table is sourced but unused. Whether this is intentional or a missing feature is unclear. Confidence: MEDIUM.
- OQ-2: No rounding is applied to total_loan_balance or total_account_balance. Whether this is intentional or an oversight compared to other jobs (e.g., Customer360Snapshot which rounds to 2 decimals) is unclear. Confidence: LOW.
