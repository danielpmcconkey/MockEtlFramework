# AccountTypeDistribution — Business Requirements Document

## Overview

This job computes the distribution of accounts by account type, producing a count and percentage breakdown for each type (Checking, Savings, Credit). The result is written to `curated.account_type_distribution` using Overwrite mode.

## Source Tables

### datalake.accounts
- **Columns sourced:** account_id, customer_id, account_type, account_status, current_balance
- **Columns actually used:** account_type (for grouping), as_of (from first row), and the total row count
- **Join/filter logic:** No filtering. All account rows for the effective date are included.
- **Evidence:** [ExternalModules/AccountDistributionCalculator.cs:29-35] Only account_type is read from each row.

### datalake.branches
- **Columns sourced:** branch_id, branch_name, city
- **Usage:** NONE — this DataSourcing module is loaded into shared state as "branches" but the External module never accesses it.
- **Evidence:** [ExternalModules/AccountDistributionCalculator.cs] No reference to `sharedState["branches"]` anywhere.

## Business Rules

BR-1: Accounts are grouped by account_type and counted.
- Confidence: HIGH
- Evidence: [ExternalModules/AccountDistributionCalculator.cs:29-35] Dictionary keyed by account_type, counting occurrences.
- Evidence: [curated.account_type_distribution] 3 rows for 2024-10-31: Checking=96, Savings=94, Credit=87.

BR-2: The total number of accounts is computed as the count of all account rows.
- Confidence: HIGH
- Evidence: [ExternalModules/AccountDistributionCalculator.cs:25] `var totalAccounts = accounts.Count;`

BR-3: The percentage is calculated as (type_count / total_accounts) * 100.
- Confidence: HIGH
- Evidence: [ExternalModules/AccountDistributionCalculator.cs:41] `var percentage = (double)typeCount / totalAccounts * 100.0;`
- Evidence: [curated.account_type_distribution] Checking: 96/277 * 100 = 34.657 -> stored as 34.66 (rounded to 2 decimal places by database NUMERIC(5,2) type).

BR-4: The as_of value is taken from the first account row and applied uniformly to all output rows.
- Confidence: HIGH
- Evidence: [ExternalModules/AccountDistributionCalculator.cs:24] `var asOf = accounts.Rows[0]["as_of"];`

BR-5: The output contains 5 columns: account_type, account_count, total_accounts, percentage, as_of.
- Confidence: HIGH
- Evidence: [ExternalModules/AccountDistributionCalculator.cs:10-13] `outputColumns` lists these 5 columns.
- Evidence: [curated.account_type_distribution] Schema confirms these 5 columns.

BR-6: Data is written in Overwrite mode — only the most recent date is retained.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/account_type_distribution.json:28] `"writeMode": "Overwrite"`.

BR-7: When the accounts DataFrame is null or empty, an empty DataFrame with the correct schema is returned.
- Confidence: HIGH
- Evidence: [ExternalModules/AccountDistributionCalculator.cs:17-21] Explicit null/empty guard.

BR-8: NULL account_type values are coalesced to empty string for grouping.
- Confidence: HIGH
- Evidence: [ExternalModules/AccountDistributionCalculator.cs:31] `?.ToString() ?? ""`
- Note: Schema constraints prevent NULL account_type, so this is defensive only.

BR-9: The percentage is computed as a C# double (floating-point), then stored into a NUMERIC(5,2) column in PostgreSQL, which rounds to 2 decimal places.
- Confidence: HIGH
- Evidence: [ExternalModules/AccountDistributionCalculator.cs:41] `(double)typeCount / totalAccounts * 100.0` produces a double.
- Evidence: [curated.account_type_distribution] percentage column is NUMERIC(5,2), so 34.6570... becomes 34.66.

## Output Schema

| Column | Source | Transformation |
|--------|--------|----------------|
| account_type | datalake.accounts.account_type | Group key; COALESCE to "" if null |
| account_count | Computed | COUNT of accounts per type |
| total_accounts | Computed | Total COUNT of all accounts |
| percentage | Computed | (account_count / total_accounts) * 100 as float, stored as NUMERIC(5,2) |
| as_of | datalake.accounts.as_of (first row) | Taken from first account row |

## Edge Cases

- **Empty accounts:** Returns empty output (BR-7).
- **Weekend dates:** No account data for weekends; DataSourcing returns empty, triggering the empty guard.
- **Percentage rounding:** The C# code computes percentage as a double, which may have floating-point imprecision. The database NUMERIC(5,2) column rounds to 2 decimal places on storage. V2 must reproduce equivalent rounding behavior.
- **Division by zero:** If total_accounts is zero, the code would divide by zero. However, the empty guard (BR-7) prevents this — if accounts is empty, it returns before reaching the division.

## Anti-Patterns Identified

- **AP-1: Redundant Data Sourcing** — The `branches` DataSourcing module fetches branch_id, branch_name, and city from `datalake.branches`, but the External module never accesses `sharedState["branches"]`.
  - Evidence: [JobExecutor/Jobs/account_type_distribution.json:13-18] branches DataSourcing defined; [ExternalModules/AccountDistributionCalculator.cs] no reference to "branches".
  - V2 approach: Remove the branches DataSourcing module entirely.

- **AP-3: Unnecessary External Module** — The `AccountDistributionCalculator` performs a GROUP BY with percentage calculation. This is expressible in SQL using COUNT with a window function or subquery.
  - Evidence: [ExternalModules/AccountDistributionCalculator.cs] Logic is: count by type, compute total, divide for percentage.
  - V2 approach: Replace with SQL Transformation: `SELECT account_type, COUNT(*) AS account_count, (SELECT COUNT(*) FROM accounts) AS total_accounts, CAST(COUNT(*) AS REAL) / (SELECT COUNT(*) FROM accounts) * 100.0 AS percentage, as_of FROM accounts GROUP BY account_type, as_of`.

- **AP-4: Unused Columns Sourced** — The accounts DataSourcing fetches account_id, customer_id, account_status, and current_balance, but only account_type and as_of are used.
  - Evidence: [JobExecutor/Jobs/account_type_distribution.json:10] columns include account_id, customer_id, account_status, current_balance; [ExternalModules/AccountDistributionCalculator.cs:29-35] only account_type is read per row.
  - V2 approach: Only source account_type from accounts.

- **AP-6: Row-by-Row Iteration in External Module** — The External module iterates accounts row-by-row to count types, when SQL GROUP BY handles this directly.
  - Evidence: [ExternalModules/AccountDistributionCalculator.cs:29] `foreach (var acctRow in accounts.Rows)`
  - V2 approach: Replace with SQL GROUP BY aggregation.

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|-------------------|
| BR-1 | [ExternalModules/AccountDistributionCalculator.cs:29-35], [curated.account_type_distribution] |
| BR-2 | [ExternalModules/AccountDistributionCalculator.cs:25] |
| BR-3 | [ExternalModules/AccountDistributionCalculator.cs:41] |
| BR-4 | [ExternalModules/AccountDistributionCalculator.cs:24] |
| BR-5 | [ExternalModules/AccountDistributionCalculator.cs:10-13], [curated.account_type_distribution] schema |
| BR-6 | [JobExecutor/Jobs/account_type_distribution.json:28] |
| BR-7 | [ExternalModules/AccountDistributionCalculator.cs:17-21] |
| BR-8 | [ExternalModules/AccountDistributionCalculator.cs:31] |
| BR-9 | [ExternalModules/AccountDistributionCalculator.cs:41], [curated.account_type_distribution] NUMERIC(5,2) |

## Open Questions

- **Q1:** The percentage calculation uses C# double arithmetic which may introduce floating-point imprecision (e.g., 34.657039711...). The PostgreSQL NUMERIC(5,2) column rounds this. The V2 SQL must produce the same rounding result. SQLite REAL division followed by PostgreSQL NUMERIC storage should produce equivalent results, but this should be verified during comparison testing. Confidence: MEDIUM.
