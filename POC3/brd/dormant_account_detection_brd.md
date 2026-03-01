# DormantAccountDetection — Business Requirements Document

## Overview
Identifies dormant (inactive) accounts by finding accounts that have zero transactions on the target effective date, enriching them with customer names and account details. Includes weekend fallback logic to use Friday's date when the effective date falls on a weekend.

## Output Type
ParquetFileWriter

## Writer Configuration
- **source**: `output`
- **outputDirectory**: `Output/curated/dormant_account_detection/`
- **numParts**: 1
- **writeMode**: Overwrite

## Source Tables

| Table | Columns Used | Filters | Evidence |
|-------|-------------|---------|----------|
| datalake.accounts | account_id, customer_id, account_type, current_balance | Effective date range (injected by executor) | [dormant_account_detection.json:8-10] |
| datalake.transactions | transaction_id, account_id, txn_type, amount | Effective date range (injected by executor) | [dormant_account_detection.json:14-16] |
| datalake.customers | id, first_name, last_name | Effective date range (injected by executor) | [dormant_account_detection.json:20-22] |

### Schema Details

**accounts**: account_id (integer), customer_id (integer), account_type (varchar), account_status (varchar), open_date (date), current_balance (numeric), interest_rate (numeric), credit_limit (numeric), apr (numeric), as_of (date)

**transactions**: transaction_id (integer), account_id (integer), txn_timestamp (timestamp), txn_type (varchar), amount (numeric), description (varchar), as_of (date)

**customers**: id (integer), prefix (varchar), first_name (varchar), last_name (varchar), sort_name (varchar), suffix (varchar), birthdate (date), as_of (date)

## Business Rules

BR-1: The module reads `__maxEffectiveDate` from shared state to determine the target date for dormancy detection.
- Confidence: HIGH
- Evidence: [ExternalModules/DormantAccountDetector.cs:27] `var maxDate = (DateOnly)sharedState["__maxEffectiveDate"]`

BR-2: Weekend fallback: If the effective date is Saturday, the target date is moved to Friday (maxDate - 1). If Sunday, moved to Friday (maxDate - 2).
- Confidence: HIGH
- Evidence: [ExternalModules/DormantAccountDetector.cs:28-30] explicit DayOfWeek checks with AddDays

BR-3: An account is considered dormant if it has NO transactions on the target date. The module builds a set of account_ids that have at least one transaction matching the target date, then flags all accounts NOT in that set.
- Confidence: HIGH
- Evidence: [ExternalModules/DormantAccountDetector.cs:33-46] builds activeAccounts HashSet; [DormantAccountDetector.cs:70] `!activeAccounts.Contains(accountId)`

BR-4: The transaction filter compares the transaction's `as_of` date to the target date (after weekend adjustment), NOT the transaction timestamp.
- Confidence: HIGH
- Evidence: [ExternalModules/DormantAccountDetector.cs:39-40] `var asOf = (DateOnly)txnRow["as_of"]; if (asOf == targetDate)`

BR-5: ALL accounts in the accounts DataFrame are evaluated for dormancy — no filter on account_type, account_status, or balance.
- Confidence: HIGH
- Evidence: [ExternalModules/DormantAccountDetector.cs:64] iterates all accounts.Rows without filtering

BR-6: The accounts DataFrame may contain rows across multiple as_of dates (full effective range). ALL account rows are checked, not just those matching the target date.
- Confidence: HIGH
- Evidence: [ExternalModules/DormantAccountDetector.cs:64] no as_of filter on accounts iteration — an account appearing on any date in the range is evaluated

BR-7: If accounts DataFrame is null or empty, output is empty.
- Confidence: HIGH
- Evidence: [ExternalModules/DormantAccountDetector.cs:20-24]

BR-8: Customer name lookup uses last-write-wins for multi-date ranges.
- Confidence: HIGH
- Evidence: [ExternalModules/DormantAccountDetector.cs:49-59] dictionary keyed by custId

BR-9: Missing customer names default to empty strings.
- Confidence: HIGH
- Evidence: [ExternalModules/DormantAccountDetector.cs:72] `GetValueOrDefault(customerId, ("", ""))`

BR-10: The output as_of is set to the target date (after weekend adjustment) formatted as "yyyy-MM-dd" string, NOT the original account's as_of.
- Confidence: HIGH
- Evidence: [ExternalModules/DormantAccountDetector.cs:82] `["as_of"] = targetDate.ToString("yyyy-MM-dd")`

BR-11: The `transaction_id`, `txn_type`, and `amount` columns are sourced from transactions but only `account_id` and `as_of` are used by the module.
- Confidence: HIGH
- Evidence: [dormant_account_detection.json:16] sourced; [DormantAccountDetector.cs:38-44] only account_id and as_of accessed

BR-12: An account that appears on multiple as_of dates but has no transactions on the target date will produce MULTIPLE output rows (one per as_of snapshot of that account).
- Confidence: HIGH
- Evidence: [DormantAccountDetector.cs:64-85] iterates all account rows without dedup by account_id

## Output Schema

| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| account_id | accounts.account_id | Direct | [DormantAccountDetector.cs:76] |
| customer_id | accounts.customer_id | Direct | [DormantAccountDetector.cs:77] |
| first_name | customers.first_name | Lookup by customer_id | [DormantAccountDetector.cs:78] |
| last_name | customers.last_name | Lookup by customer_id | [DormantAccountDetector.cs:79] |
| account_type | accounts.account_type | Direct | [DormantAccountDetector.cs:80] |
| current_balance | accounts.current_balance | Direct | [DormantAccountDetector.cs:81] |
| as_of | Computed | targetDate.ToString("yyyy-MM-dd") — weekend-adjusted date | [DormantAccountDetector.cs:82] |

## Non-Deterministic Fields
None identified.

## Write Mode Implications
**Overwrite mode**: Each execution replaces the entire `Output/curated/dormant_account_detection/` directory. Single part file (numParts=1).

## Edge Cases

- **Weekend fallback**: Saturday maps to Friday, Sunday maps to Friday. This ensures dormancy is evaluated against weekday data.
- **Empty accounts**: Returns empty output with correct column schema.
- **No transactions at all**: If transactions DataFrame is null, activeAccounts is empty, so ALL accounts are flagged as dormant.
- **Multi-date account duplication**: When effective range spans multiple days, the same account_id appears once per as_of date. Each snapshot row is independently checked against the active set, potentially producing duplicate dormant entries for the same account.
- **Weekend transaction data**: If data exists for weekends in the transactions table but the target date is shifted to Friday, weekend transactions are ignored.
- **as_of as string**: The output as_of is a string ("yyyy-MM-dd"), not a DateOnly — different from other jobs that pass through the original typed value.
- **Account with transactions on other dates but not target**: Flagged as dormant. The check is single-day only.

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|------------------|
| BR-1: Read __maxEffectiveDate | [DormantAccountDetector.cs:27] |
| BR-2: Weekend fallback | [DormantAccountDetector.cs:28-30] |
| BR-3: Dormancy = no transactions | [DormantAccountDetector.cs:33-46, 70] |
| BR-4: as_of date comparison | [DormantAccountDetector.cs:39-40] |
| BR-5: All accounts evaluated | [DormantAccountDetector.cs:64] |
| BR-6: Multi-date accounts | [DormantAccountDetector.cs:64] |
| BR-7: Empty accounts guard | [DormantAccountDetector.cs:20-24] |
| BR-8: Customer lookup last-write-wins | [DormantAccountDetector.cs:49-59] |
| BR-9: Missing customer defaults | [DormantAccountDetector.cs:72] |
| BR-10: Output as_of = adjusted target date | [DormantAccountDetector.cs:82] |
| BR-11: Unused sourced columns | [dormant_account_detection.json:16] |
| BR-12: Duplicate account rows | [DormantAccountDetector.cs:64-85] |

## Open Questions

OQ-1: Is the duplicate output per account (one row per as_of snapshot) intentional? When effective range spans 5 days, a dormant account with 5 snapshots produces 5 output rows with the same as_of value (the adjusted target date). This seems like a bug.
- Confidence: MEDIUM — duplication seems unintentional given all rows get the same adjusted as_of

OQ-2: Should dormancy detection consider account_status (e.g., only flag Active accounts)?
- Confidence: LOW — current code has no status filter, but business intent is unclear
