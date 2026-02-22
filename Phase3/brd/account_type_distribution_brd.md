# BRD: AccountTypeDistribution

## Overview
Produces a distribution analysis of accounts by account_type, showing the count per type, total accounts, and the percentage each type represents. Uses Overwrite mode so only the latest effective date's distribution is retained.

## Source Tables
| Table | Schema | Columns Used | Join/Filter Logic | Evidence |
|-------|--------|-------------|-------------------|----------|
| accounts | datalake | account_id, customer_id, account_type, account_status, current_balance | Filtered by effective date range. All accounts included. | [JobExecutor/Jobs/account_type_distribution.json:6-11] |
| branches | datalake | branch_id, branch_name, city | Filtered by effective date range. Sourced but NOT used in output. | [JobExecutor/Jobs/account_type_distribution.json:13-18] |

## Business Rules
BR-1: Accounts are grouped by account_type and the count of accounts in each type is computed.
- Confidence: HIGH
- Evidence: [ExternalModules/AccountDistributionCalculator.cs:28-35] Groups by `accountType` key and counts occurrences
- Evidence: [curated.account_type_distribution] Output shows 3 rows: Checking=96, Savings=94, Credit=87

BR-2: The total_accounts value is the total count of ALL accounts in the DataFrame (across all types).
- Confidence: HIGH
- Evidence: [ExternalModules/AccountDistributionCalculator.cs:25] `var totalAccounts = accounts.Count;`
- Evidence: [curated.account_type_distribution] total_accounts = 277 for all rows, matching datalake.accounts count

BR-3: The percentage is calculated as (typeCount / totalAccounts) * 100.0 using floating-point division.
- Confidence: HIGH
- Evidence: [ExternalModules/AccountDistributionCalculator.cs:41] `var percentage = (double)typeCount / totalAccounts * 100.0;`
- Evidence: [curated.account_type_distribution] Checking: 96/277*100 = 34.66, Savings: 94/277*100 = 33.94, Credit: 87/277*100 = 31.41

BR-4: The percentage is stored as a double (floating-point), which gets written to PostgreSQL as NUMERIC via the DataFrameWriter's type inference.
- Confidence: HIGH
- Evidence: [ExternalModules/AccountDistributionCalculator.cs:41] Result is `double` type
- Evidence: [curated.account_type_distribution] `percentage` column is NUMERIC, values show 2 decimal places (34.66, 33.94, 31.41)

BR-5: The as_of value is taken from the first row of the accounts DataFrame.
- Confidence: HIGH
- Evidence: [ExternalModules/AccountDistributionCalculator.cs:24] `var asOf = accounts.Rows[0]["as_of"];`

BR-6: The branches table is sourced but never used in the output.
- Confidence: HIGH
- Evidence: [ExternalModules/AccountDistributionCalculator.cs:15] Only `accounts` is read from shared state -- no reference to "branches"

BR-7: Write mode is Overwrite -- the curated table is truncated before each write.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/account_type_distribution.json:28] `"writeMode": "Overwrite"`
- Evidence: [curated.account_type_distribution] Only 1 as_of date (2024-10-31) with 3 rows

BR-8: NULL or empty account_type values are coalesced to empty string.
- Confidence: HIGH
- Evidence: [ExternalModules/AccountDistributionCalculator.cs:31] `acctRow["account_type"]?.ToString() ?? ""`

BR-9: The job runs only on business days (weekdays) since the accounts source table only has weekday as_of dates.
- Confidence: HIGH
- Evidence: [datalake.accounts] Weekday-only as_of dates

## Output Schema
| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| account_type | datalake.accounts.account_type | Grouping key (coalesced to "" if null) | [AccountDistributionCalculator.cs:31, 45] |
| account_count | Computed | COUNT of accounts per account_type | [AccountDistributionCalculator.cs:40, 46] |
| total_accounts | Computed | Total COUNT of all accounts (accounts.Count) | [AccountDistributionCalculator.cs:25, 47] |
| percentage | Computed | (account_count / total_accounts) * 100.0 as double | [AccountDistributionCalculator.cs:41, 48] |
| as_of | datalake.accounts.as_of (first row) | Taken from first account row | [AccountDistributionCalculator.cs:24, 49] |

## Edge Cases
- **NULL handling**: account_type is coalesced to empty string via `?.ToString() ?? ""`. [AccountDistributionCalculator.cs:31]
- **Empty accounts DataFrame**: If accounts is null or has 0 rows, an empty DataFrame with correct schema is returned. [AccountDistributionCalculator.cs:15-21]
- **Division by zero**: Not explicitly guarded. If totalAccounts is 0, the percentage calculation would produce NaN or Infinity. However, the empty DataFrame guard (line 18) prevents this case -- if count is 0, the empty output is returned before percentage calculation.
- **Floating-point precision**: Percentage is computed as `double`, which may have floating-point rounding issues. The values stored in PostgreSQL (34.66, 33.94, 31.41) suggest rounding occurs at the database level when stored as NUMERIC.
- **Weekend/date fallback**: Accounts table has no weekend data, so no output on weekends.

## Traceability Matrix
| Requirement | Evidence Citations |
|-------------|-------------------|
| BR-1 | [AccountDistributionCalculator.cs:28-35], [curated.account_type_distribution data] |
| BR-2 | [AccountDistributionCalculator.cs:25], [curated.account_type_distribution total_accounts] |
| BR-3 | [AccountDistributionCalculator.cs:41], [curated.account_type_distribution percentages] |
| BR-4 | [AccountDistributionCalculator.cs:41], [curated.account_type_distribution schema] |
| BR-5 | [AccountDistributionCalculator.cs:24] |
| BR-6 | [AccountDistributionCalculator.cs:15], [account_type_distribution.json:13-18] |
| BR-7 | [account_type_distribution.json:28], [curated.account_type_distribution row counts] |
| BR-8 | [AccountDistributionCalculator.cs:31] |
| BR-9 | [datalake.accounts as_of dates] |

## Open Questions
- **Why is branches sourced but unused?** The branches DataSourcing module (with branch_id, branch_name, city) is configured but never referenced by AccountDistributionCalculator. Confidence: MEDIUM that it is intentionally unused.
- **Percentage precision**: The double->NUMERIC conversion may introduce subtle rounding differences between the C# computation and what gets stored in PostgreSQL. The V2 implementation should match this behavior exactly.
