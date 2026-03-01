# AccountTypeDistribution — Business Requirements Document

## Overview
Produces a daily distribution analysis showing the count and percentage of accounts by account type. Output is written to CSV with an END trailer, overwriting on each run.

## Output Type
CsvFileWriter

## Writer Configuration
- **source**: `output`
- **outputFile**: `Output/curated/account_type_distribution.csv`
- **includeHeader**: true
- **trailerFormat**: `END|{row_count}`
- **writeMode**: Overwrite
- **lineEnding**: LF

## Source Tables

| Table | Columns Used | Filters | Evidence |
|-------|-------------|---------|----------|
| datalake.accounts | account_id, customer_id, account_type, account_status, current_balance | Effective date range (injected) | [account_type_distribution.json:8-10] |
| datalake.branches | branch_id, branch_name, city | Effective date range (injected) | [account_type_distribution.json:13-16] |

## Business Rules

BR-1: Accounts are grouped by account_type. For each type, the count and percentage of total accounts are computed.
- Confidence: HIGH
- Evidence: [AccountDistributionCalculator.cs:28-35] — typeCounts dictionary; [AccountDistributionCalculator.cs:41] — percentage calculation

BR-2: Percentage is calculated as `(typeCount / totalAccounts) * 100.0` using double-precision floating point arithmetic.
- Confidence: HIGH
- Evidence: [AccountDistributionCalculator.cs:41] — `(double)typeCount / totalAccounts * 100.0`

BR-3: The total_accounts field represents the total row count of the accounts DataFrame (all types combined).
- Confidence: HIGH
- Evidence: [AccountDistributionCalculator.cs:25] — `var totalAccounts = accounts.Count;`

BR-4: The branches DataFrame is sourced but NOT used by the External module.
- Confidence: HIGH
- Evidence: [AccountDistributionCalculator.cs:8-56] — no reference to "branches"

BR-5: The as_of value is taken from the first row of the accounts DataFrame.
- Confidence: HIGH
- Evidence: [AccountDistributionCalculator.cs:24] — `var asOf = accounts.Rows[0]["as_of"];`

BR-6: If accounts is null or empty, an empty DataFrame with the correct schema is produced.
- Confidence: HIGH
- Evidence: [AccountDistributionCalculator.cs:17-21]

BR-7: With current data (3 account types: Checking, Savings, Credit), expect 3 output rows per day.
- Confidence: HIGH
- Evidence: [DB query: DISTINCT account_type returns 3 values]

BR-8: The trailer format is `END|{row_count}` — note this uses a different prefix ("END") than most other jobs ("TRAILER").
- Confidence: HIGH
- Evidence: [account_type_distribution.json:29]

## Output Schema

| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| account_type | accounts.account_type | Group key | [AccountDistributionCalculator.cs:43] |
| account_count | Computed | COUNT of rows per account_type | [AccountDistributionCalculator.cs:44] |
| total_accounts | accounts.Count | Total count of all account rows | [AccountDistributionCalculator.cs:45] |
| percentage | Computed | (account_count / total_accounts) * 100.0 as double | [AccountDistributionCalculator.cs:46] |
| as_of | accounts.Rows[0].as_of | First row value applied to all | [AccountDistributionCalculator.cs:47] |

## Non-Deterministic Fields
None identified. Percentage values are deterministic given the same input data, though floating-point representation may vary slightly.

## Write Mode Implications
- **Overwrite mode**: Each run replaces the entire CSV. Only the latest effective date persists.
- Multi-day gap-fill: intermediate days are lost; only the final day survives.

## Edge Cases
- **Weekend dates**: Accounts is weekday-only. Weekend dates produce empty output.
- **Floating-point precision**: Percentage is computed as a double, which may produce values like 33.333333333333336 rather than 33.33. No rounding is applied.
- **Output row ordering**: Dictionary iteration order is not guaranteed.

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|------------------|
| Group by account_type with count/percentage | AccountDistributionCalculator.cs:28-41 |
| Branches sourced but unused | AccountDistributionCalculator.cs (no reference) |
| Percentage as double arithmetic | AccountDistributionCalculator.cs:41 |
| END trailer format | account_type_distribution.json:29 |
| Overwrite write mode | account_type_distribution.json:30 |
| First effective date 2024-10-01 | account_type_distribution.json:3 |

## Open Questions
1. Why are branches sourced if they are not used? (Confidence: LOW)
2. The percentage values are unrounded doubles — is this intentional or a precision oversight? (Confidence: MEDIUM)
