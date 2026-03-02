# CustomerValueScore -- Business Requirements Document

## Overview
Computes a composite customer value score based on three weighted components: transaction activity, account balance, and branch visit frequency. Each component is individually scored and capped at 1000, then combined using fixed weights to produce a final composite score. Output is a CSV with LF line endings.

## Output Type
CsvFileWriter

## Writer Configuration
- **source**: `output`
- **outputFile**: `Output/curated/customer_value_score.csv`
- **includeHeader**: true
- **writeMode**: Overwrite
- **lineEnding**: LF
- **trailerFormat**: (not configured -- no trailer)

## Source Tables

| Table | Columns Used | Filters | Evidence |
|-------|-------------|---------|----------|
| datalake.customers | id, first_name, last_name | Effective date range (injected by executor) | [customer_value_score.json:8-10] |
| datalake.transactions | transaction_id, account_id, txn_type, amount | Effective date range (injected by executor) | [customer_value_score.json:14-17] |
| datalake.accounts | account_id, customer_id, current_balance | Effective date range (injected by executor) | [customer_value_score.json:20-23] |
| datalake.branch_visits | visit_id, customer_id, branch_id | Effective date range (injected by executor) | [customer_value_score.json:27-29] |

## Business Rules

BR-1: Both customers and accounts must be non-null and non-empty for output to be produced. If either is empty, the output is empty.
- Confidence: HIGH
- Evidence: [CustomerValueCalculator.cs:22-24] -- `if (customers == null || customers.Count == 0 || accounts == null || accounts.Count == 0)`.

BR-2: Transaction count is linked to customer via the accounts table (transaction.account_id -> account.customer_id lookup).
- Confidence: HIGH
- Evidence: [CustomerValueCalculator.cs:34-39] -- `accountToCustomer` dictionary built from accounts. [CustomerValueCalculator.cs:46-49] -- transaction's account_id looked up in `accountToCustomer`.

BR-3: Transaction score = count of transactions * 10.0, capped at 1000.
- Confidence: HIGH
- Evidence: [CustomerValueCalculator.cs:94] -- `Math.Min(txnCount * 10.0m, 1000m)`.

BR-4: Balance score = total account balance / 1000.0, capped at 1000.
- Confidence: HIGH
- Evidence: [CustomerValueCalculator.cs:98] -- `Math.Min(totalBalance / 1000.0m, 1000m)`.

BR-5: Visit score = count of branch visits * 50.0, capped at 1000.
- Confidence: HIGH
- Evidence: [CustomerValueCalculator.cs:102] -- `Math.Min(visitCount * 50.0m, 1000m)`.

BR-6: Composite score = (transaction_score * 0.40) + (balance_score * 0.35) + (visit_score * 0.25).
- Confidence: HIGH
- Evidence: [CustomerValueCalculator.cs:29-31,105-107] -- weights defined as `transactionWeight = 0.4m`, `balanceWeight = 0.35m`, `visitWeight = 0.25m`.

BR-7: All individual scores and the composite score are rounded to 2 decimal places using `Math.Round`.
- Confidence: HIGH
- Evidence: [CustomerValueCalculator.cs:114-117] -- `Math.Round(..., 2)` applied to all four score fields.

BR-8: Customers with no transactions get transaction_score = 0 (via `GetValueOrDefault(customerId, 0)`).
- Confidence: HIGH
- Evidence: [CustomerValueCalculator.cs:93] -- `txnCountByCustomer.GetValueOrDefault(customerId, 0)`.

BR-9: Customers with no branch visits get visit_score = 0.
- Confidence: HIGH
- Evidence: [CustomerValueCalculator.cs:101] -- `visitCountByCustomer.GetValueOrDefault(customerId, 0)`.

BR-10: Transactions where the account_id is not found in the accounts table (customer_id resolves to 0) are silently skipped.
- Confidence: HIGH
- Evidence: [CustomerValueCalculator.cs:49] -- `if (customerId == 0) continue;`.

BR-11: The `as_of` column is taken from the customer row.
- Confidence: HIGH
- Evidence: [CustomerValueCalculator.cs:118] -- `custRow["as_of"]`.

BR-12: Iteration is customer-driven. Every customer in the customers DataFrame produces an output row.
- Confidence: HIGH
- Evidence: [CustomerValueCalculator.cs:86] -- `foreach (var custRow in customers.Rows)`.

## Output Schema

| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| customer_id | customers.id | Cast to int via Convert.ToInt32 | [CustomerValueCalculator.cs:88] |
| first_name | customers.first_name | ToString with null coalesce to "" | [CustomerValueCalculator.cs:89] |
| last_name | customers.last_name | ToString with null coalesce to "" | [CustomerValueCalculator.cs:90] |
| transaction_score | Computed | txn_count * 10, capped at 1000, rounded to 2 decimal places | [CustomerValueCalculator.cs:94,114] |
| balance_score | Computed | total_balance / 1000, capped at 1000, rounded to 2 decimal places | [CustomerValueCalculator.cs:98,115] |
| visit_score | Computed | visit_count * 50, capped at 1000, rounded to 2 decimal places | [CustomerValueCalculator.cs:102,116] |
| composite_score | Computed | Weighted sum: 0.4 * txn + 0.35 * bal + 0.25 * visit, rounded to 2 decimal places | [CustomerValueCalculator.cs:105-107,117] |
| as_of | customers.as_of | Pass-through | [CustomerValueCalculator.cs:118] |

## Non-Deterministic Fields
None identified.

## Write Mode Implications
- **Overwrite**: Each execution replaces the entire output file. For multi-day auto-advance runs, only the last effective date's output survives on disk.

## Edge Cases
- **Accounts required**: Unlike many other jobs, both customers AND accounts must be non-empty. If accounts is empty, no output is produced even if customers exist.
- **Orphan transactions**: Transactions referencing account_ids not in the accounts DataFrame are silently skipped (customerId resolves to 0).
- **All scores capped at 1000**: Very active or wealthy customers hit the ceiling and cannot be differentiated at the top end.
- **Negative balances**: Could produce negative balance_score (no floor applied, only a ceiling of 1000). The cap is `Math.Min`, not `Math.Clamp`.
- **Transactions counted regardless of type**: Both Debit and Credit transactions increment the count equally.

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|------------------|
| Empty guard on customers + accounts | [CustomerValueCalculator.cs:22-24] |
| Transaction-to-customer via accounts | [CustomerValueCalculator.cs:34-39,46-49] |
| Transaction score formula | [CustomerValueCalculator.cs:94] |
| Balance score formula | [CustomerValueCalculator.cs:98] |
| Visit score formula | [CustomerValueCalculator.cs:102] |
| Composite score weights | [CustomerValueCalculator.cs:29-31,105-107] |
| Rounding to 2 decimal places | [CustomerValueCalculator.cs:114-117] |
| Orphan transaction skip | [CustomerValueCalculator.cs:49] |
| LF line endings | [customer_value_score.json:44] |
| Overwrite mode | [customer_value_score.json:43] |

## Open Questions
- OQ-1: Negative account balances could produce negative balance scores. Whether a floor of 0 should be applied is unclear. Confidence: LOW -- no explicit handling in code.
