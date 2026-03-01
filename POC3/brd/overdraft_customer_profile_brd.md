# OverdraftCustomerProfile — Business Requirements Document

## Overview
Builds a per-customer overdraft profile showing overdraft count, total amount, and average amount. Enriches with customer name from the customers table. Implements weekend fallback logic (Saturday/Sunday uses Friday's data). Output is a single-part Parquet file.

## Output Type
ParquetFileWriter

## Writer Configuration
- **source**: `output`
- **outputDirectory**: `Output/curated/overdraft_customer_profile/`
- **numParts**: 1
- **writeMode**: Overwrite

## Source Tables

| Table | Columns Used | Filters | Evidence |
|-------|-------------|---------|----------|
| datalake.overdraft_events | overdraft_id, account_id, customer_id, overdraft_amount, fee_amount, fee_waived, event_timestamp | Effective date range injected via shared state; then filtered to target date in External | [overdraft_customer_profile.json:4-11] |
| datalake.customers | id, prefix, first_name, last_name, suffix, birthdate | Effective date range injected via shared state | [overdraft_customer_profile.json:13-18] |
| datalake.accounts | account_id, customer_id, account_type, account_status | Effective date range injected via shared state; **NEVER USED** | [overdraft_customer_profile.json:20-25] |

### Table Schemas (from database)

**overdraft_events**: overdraft_id (integer), account_id (integer), customer_id (integer), overdraft_amount (numeric), fee_amount (numeric), fee_waived (boolean), event_timestamp (timestamp), as_of (date)

**customers**: id (integer), prefix (varchar), first_name (varchar), last_name (varchar), sort_name (varchar), suffix (varchar), birthdate (date), as_of (date)

**accounts**: account_id (integer), customer_id (integer), account_type (varchar), account_status (varchar), as_of (date)

## Business Rules

BR-1: **Weekend fallback** — If `__maxEffectiveDate` is a Saturday, the processor uses Friday's date (maxDate - 1). If Sunday, uses Friday's date (maxDate - 2). Weekdays use the date as-is.
- Confidence: HIGH
- Evidence: [OverdraftCustomerProfileProcessor.cs:21-23] `if (maxDate.DayOfWeek == DayOfWeek.Saturday) targetDate = maxDate.AddDays(-1);` etc. Comment: `W2: Weekend fallback`

BR-2: Overdraft events are filtered to only the **target date** (after weekend fallback). Only events with `as_of` matching the target date contribute to the output.
- Confidence: HIGH
- Evidence: [OverdraftCustomerProfileProcessor.cs:42-44] `.Where(r => (r["as_of"] is DateOnly d && d == targetDate) || r["as_of"]?.ToString() == targetDate.ToString("yyyy-MM-dd"))`

BR-3: Customer lookup is built from ALL customer rows (across all dates), using the LAST seen values for each `id`. If a customer's name changes across `as_of` dates, only the last-loaded values are used.
- Confidence: MEDIUM
- Evidence: [OverdraftCustomerProfileProcessor.cs:54-61] Dictionary overwrites on each row

BR-4: Overdraft events are grouped by `customer_id`, with count and total amount accumulated per customer.
- Confidence: HIGH
- Evidence: [OverdraftCustomerProfileProcessor.cs:64-75]

BR-5: `avg_overdraft` is calculated as `total_overdraft_amount / overdraft_count`, rounded to 2 decimal places using `Math.Round`.
- Confidence: HIGH
- Evidence: [OverdraftCustomerProfileProcessor.cs:84-86] `Math.Round(kvp.Value.totalAmount / kvp.Value.count, 2)`

BR-6: **Accounts table is sourced but never used** — The DataSourcing module loads accounts data, but the External processor never accesses `sharedState["accounts"]`.
- Confidence: HIGH
- Evidence: [overdraft_customer_profile.json:20-25] accounts DataSourcing config; [OverdraftCustomerProfileProcessor.cs:32-33] Comment: `AP1: accounts sourced but never used (dead-end)`

BR-7: **Customer prefix, suffix, and birthdate are sourced but unused** — Only `first_name` and `last_name` are used from the customers table.
- Confidence: HIGH
- Evidence: [overdraft_customer_profile.json:17] columns include prefix, suffix, birthdate; [OverdraftCustomerProfileProcessor.cs:58-59] Only `first_name` and `last_name` extracted; Comment: `AP4: prefix, suffix, birthdate sourced from customers but unused`

BR-8: If a customer_id in overdraft events is not found in the customer lookup, the name defaults to empty strings.
- Confidence: HIGH
- Evidence: [OverdraftCustomerProfileProcessor.cs:81-82] `customerLookup.ContainsKey(custId) ? customerLookup[custId] : ("", "")`

BR-9: The output `as_of` is the target date (after weekend fallback), formatted as `yyyy-MM-dd` string.
- Confidence: HIGH
- Evidence: [OverdraftCustomerProfileProcessor.cs:96] `["as_of"] = targetDate.ToString("yyyy-MM-dd")`

BR-10: Effective dates are injected by the executor at runtime.
- Confidence: HIGH
- Evidence: [overdraft_customer_profile.json:4-25] No hardcoded dates in DataSourcing configs

## Output Schema

| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| customer_id | overdraft_events.customer_id | Direct pass-through (group key) | [OverdraftCustomerProfileProcessor.cs:90] |
| first_name | customers.first_name | Lookup by customer_id | [OverdraftCustomerProfileProcessor.cs:91] |
| last_name | customers.last_name | Lookup by customer_id | [OverdraftCustomerProfileProcessor.cs:92] |
| overdraft_count | Derived | Count of overdraft events per customer for target date | [OverdraftCustomerProfileProcessor.cs:73-74] |
| total_overdraft_amount | overdraft_events.overdraft_amount | Sum per customer for target date (decimal) | [OverdraftCustomerProfileProcessor.cs:74] |
| avg_overdraft | Derived | total_overdraft_amount / overdraft_count, rounded to 2 decimals | [OverdraftCustomerProfileProcessor.cs:84-86] |
| as_of | Derived from targetDate | Weekend-adjusted effective date as yyyy-MM-dd string | [OverdraftCustomerProfileProcessor.cs:96] |

## Non-Deterministic Fields
None identified. Output is deterministic given the same source data and effective date (including day-of-week for weekend fallback).

## Write Mode Implications
- **Overwrite** mode: Each execution replaces the Parquet directory. On multi-day auto-advance, only the final effective date's output survives.
- Confidence: HIGH
- Evidence: [overdraft_customer_profile.json:33] `"writeMode": "Overwrite"`

## Edge Cases

EC-1: **Weekend fallback** — Running on Saturday or Sunday uses Friday's data. If Friday has no overdraft events, the output is empty even though Saturday/Sunday might have data (if such data existed).
- Confidence: HIGH
- Evidence: [OverdraftCustomerProfileProcessor.cs:21-23]

EC-2: **No events on target date** — If no overdraft events match the target date, an empty DataFrame is returned.
- Confidence: HIGH
- Evidence: [OverdraftCustomerProfileProcessor.cs:46-50]

EC-3: **Dead-end data sources** — Accounts table and customer columns (prefix, suffix, birthdate) are loaded but never used, wasting resources.
- Confidence: HIGH
- Evidence: [OverdraftCustomerProfileProcessor.cs:32-33] Comments: `AP1`, `AP4`

EC-4: **Customer name staleness** — The customer lookup uses the last-loaded name for each id. If a customer changes name mid-period, only the final name is used.
- Confidence: MEDIUM
- Evidence: [OverdraftCustomerProfileProcessor.cs:54-61] Dictionary overwrite behavior

EC-5: **Decimal precision** — `overdraft_amount` uses decimal arithmetic and `Math.Round(..., 2)`, so precision is well-handled (unlike some other jobs that use double).
- Confidence: HIGH
- Evidence: [OverdraftCustomerProfileProcessor.cs:68,84-86]

EC-6: **Overwrite on multi-day runs** — Only the last effective date's output survives.
- Confidence: HIGH
- Evidence: [overdraft_customer_profile.json:33] `"writeMode": "Overwrite"`

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|------------------|
| BR-1: Weekend fallback | [OverdraftCustomerProfileProcessor.cs:21-23] |
| BR-2: Filter to target date | [OverdraftCustomerProfileProcessor.cs:42-44] |
| BR-3: Customer lookup | [OverdraftCustomerProfileProcessor.cs:54-61] |
| BR-4: Group by customer | [OverdraftCustomerProfileProcessor.cs:64-75] |
| BR-5: Average calculation | [OverdraftCustomerProfileProcessor.cs:84-86] |
| BR-6: Dead-end accounts | [OverdraftCustomerProfileProcessor.cs:32-33] |
| BR-7: Unused customer columns | [OverdraftCustomerProfileProcessor.cs:33] |
| BR-8: Missing customer fallback | [OverdraftCustomerProfileProcessor.cs:81-82] |
| BR-9: as_of from targetDate | [OverdraftCustomerProfileProcessor.cs:96] |
| EC-1: Weekend fallback | [OverdraftCustomerProfileProcessor.cs:21-23] |

## Open Questions
1. **Weekend fallback intent** — Is the Saturday/Sunday-to-Friday fallback a business requirement (no overdrafts on weekends) or a data availability constraint? Confidence: LOW — no additional context available.
2. **Why source accounts?** The accounts table is loaded but never accessed. This appears to be dead code. Confidence: HIGH.
