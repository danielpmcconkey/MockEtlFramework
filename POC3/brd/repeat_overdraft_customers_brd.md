# RepeatOverdraftCustomers — Business Requirements Document

## Overview
Identifies customers with 2 or more overdraft events across the effective date range, producing a per-customer summary with event count and total overdraft amount, enriched with customer name. Output is a single-part Parquet file.

## Output Type
ParquetFileWriter

## Writer Configuration
- **source**: `output`
- **outputDirectory**: `Output/curated/repeat_overdraft_customers/`
- **numParts**: 1
- **writeMode**: Overwrite

## Source Tables

| Table | Columns Used | Filters | Evidence |
|-------|-------------|---------|----------|
| datalake.overdraft_events | overdraft_id, account_id, customer_id, overdraft_amount, fee_amount, fee_waived, event_timestamp | Effective date range injected via shared state | [repeat_overdraft_customers.json:4-11] |
| datalake.customers | id, first_name, last_name | Effective date range injected via shared state | [repeat_overdraft_customers.json:13-17] |

### Table Schemas (from database)

**overdraft_events**: overdraft_id (integer), account_id (integer), customer_id (integer), overdraft_amount (numeric), fee_amount (numeric), fee_waived (boolean), event_timestamp (timestamp), as_of (date)

**customers**: id (integer), prefix (varchar), first_name (varchar), last_name (varchar), sort_name (varchar), suffix (varchar), birthdate (date), as_of (date)

## Business Rules

BR-1: Customer lookup is built from ALL customer rows (across all dates in the effective range). If a customer's name changes across `as_of` dates, the LAST loaded values are used (dictionary overwrite).
- Confidence: MEDIUM
- Evidence: [RepeatOverdraftCustomerProcessor.cs:31-38] Dictionary overwrite on each row

BR-2: Overdraft events are grouped by `customer_id`, accumulating count and total_overdraft_amount per customer across ALL dates in the effective range.
- Confidence: HIGH
- Evidence: [RepeatOverdraftCustomerProcessor.cs:41-52] Row-by-row iteration; Comment: `AP6: Row-by-row iteration`

BR-3: **Repeat threshold: 2+ overdrafts** — Only customers with 2 or more overdraft events are included in the output.
- Confidence: HIGH
- Evidence: [RepeatOverdraftCustomerProcessor.cs:55-58] `if (kvp.Value.count < 2) continue;` — Comment: `AP7: Magic threshold`

BR-4: The `as_of` value for all output rows is taken from the first row of overdraft_events (object pass-through, not string-formatted).
- Confidence: HIGH
- Evidence: [RepeatOverdraftCustomerProcessor.cs:28] `var asOf = overdraftEvents.Rows[0]["as_of"];`

BR-5: If a customer_id is not found in the customer lookup, the name defaults to empty strings.
- Confidence: HIGH
- Evidence: [RepeatOverdraftCustomerProcessor.cs:62-63] `customerLookup.ContainsKey(custId) ? customerLookup[custId] : ("", "")`

BR-6: Total overdraft amount uses decimal arithmetic for precision.
- Confidence: HIGH
- Evidence: [RepeatOverdraftCustomerProcessor.cs:44] `var amount = Convert.ToDecimal(evt["overdraft_amount"]);`

BR-7: Effective dates are injected by the executor at runtime.
- Confidence: HIGH
- Evidence: [repeat_overdraft_customers.json:4-17] No hardcoded dates in DataSourcing configs

## Output Schema

| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| customer_id | overdraft_events.customer_id | Direct pass-through (group key) | [RepeatOverdraftCustomerProcessor.cs:67] |
| first_name | customers.first_name | Lookup by customer_id | [RepeatOverdraftCustomerProcessor.cs:68] |
| last_name | customers.last_name | Lookup by customer_id | [RepeatOverdraftCustomerProcessor.cs:69] |
| overdraft_count | Derived | Count of overdraft events per customer (>= 2) | [RepeatOverdraftCustomerProcessor.cs:70] |
| total_overdraft_amount | overdraft_events.overdraft_amount | Sum per customer (decimal) | [RepeatOverdraftCustomerProcessor.cs:71] |
| as_of | overdraft_events (first row) | Object pass-through from first source row | [RepeatOverdraftCustomerProcessor.cs:72] |

## Non-Deterministic Fields
None identified. Output is deterministic given the same source data and effective date range.

## Write Mode Implications
- **Overwrite** mode: Each execution replaces the Parquet directory. On multi-day auto-advance, only the final effective date's output survives.
- Since the count threshold (2+) is applied across ALL dates in the range, wider effective date ranges will identify more repeat offenders.
- Confidence: HIGH
- Evidence: [repeat_overdraft_customers.json:24] `"writeMode": "Overwrite"`

## Edge Cases

EC-1: **Magic threshold** — The repeat threshold of 2 is hardcoded with no configuration option. Changing it requires modifying the processor code.
- Confidence: HIGH
- Evidence: [RepeatOverdraftCustomerProcessor.cs:55-58] Comment: `AP7: Magic threshold`

EC-2: **Cross-date counting** — Overdraft counts span the entire effective date range, not per-date. A customer with 1 overdraft on day 1 and 1 on day 2 qualifies as a repeat (count=2).
- Confidence: HIGH
- Evidence: [RepeatOverdraftCustomerProcessor.cs:41-52] No date filtering; iterates all rows

EC-3: **as_of from first row** — The as_of value is taken from the first row of overdraft_events, which may not correspond to the current effective date.
- Confidence: HIGH
- Evidence: [RepeatOverdraftCustomerProcessor.cs:28]

EC-4: **Empty source data** — If either overdraft_events or customers is empty, an empty DataFrame is returned.
- Confidence: HIGH
- Evidence: [RepeatOverdraftCustomerProcessor.cs:22-26]

EC-5: **Single-overdraft customers excluded** — Customers with exactly 1 overdraft event are filtered out. This is the core business logic, not an edge case per se, but it means the output is a strict subset of all overdraft customers.
- Confidence: HIGH
- Evidence: [RepeatOverdraftCustomerProcessor.cs:55-58]

EC-6: **Overwrite on multi-day runs** — Only the last effective date's output survives.
- Confidence: HIGH
- Evidence: [repeat_overdraft_customers.json:24] `"writeMode": "Overwrite"`

EC-7: **Unused sourced columns** — `overdraft_id`, `account_id`, `fee_amount`, `fee_waived`, `event_timestamp` from overdraft_events are sourced but only `customer_id` and `overdraft_amount` are used.
- Confidence: HIGH
- Evidence: Comparison of DataSourcing columns vs. processor logic

EC-8: **Output row ordering** — Output rows follow Dictionary iteration order (insertion order), which is the order customers are first encountered in the overdraft events. This is not explicitly sorted.
- Confidence: HIGH
- Evidence: [RepeatOverdraftCustomerProcessor.cs:56-75] Iterates `customerOverdrafts` dictionary

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|------------------|
| BR-1: Customer lookup | [RepeatOverdraftCustomerProcessor.cs:31-38] |
| BR-2: Group by customer_id | [RepeatOverdraftCustomerProcessor.cs:41-52] |
| BR-3: Repeat threshold >= 2 | [RepeatOverdraftCustomerProcessor.cs:55-58] |
| BR-4: as_of from first row | [RepeatOverdraftCustomerProcessor.cs:28] |
| BR-5: Missing customer fallback | [RepeatOverdraftCustomerProcessor.cs:62-63] |
| BR-6: Decimal arithmetic | [RepeatOverdraftCustomerProcessor.cs:44] |
| EC-1: Magic threshold | [RepeatOverdraftCustomerProcessor.cs:55-58] |
| EC-2: Cross-date counting | [RepeatOverdraftCustomerProcessor.cs:41-52] |
| EC-8: Unordered output | [RepeatOverdraftCustomerProcessor.cs:56-75] |

## Open Questions
1. **Should the threshold be configurable?** The 2+ threshold is hardcoded. In a production system, this might need to be a config parameter. Confidence: LOW — unclear if this is a conscious design choice.
2. **Cross-date vs. per-date counting** — Is it intentional that repeat status is determined across all dates in the range, rather than per individual date? Confidence: MEDIUM — the cross-date approach seems reasonable for identifying habitual overdrafters.
