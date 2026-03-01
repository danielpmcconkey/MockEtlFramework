# CustomerBranchActivity — Business Requirements Document

## Overview
Produces a per-customer summary of branch visit activity, counting the total number of branch visits per customer and enriching with customer name information, using an External module (CustomerBranchActivityBuilder).

## Output Type
CsvFileWriter

## Writer Configuration
- **source**: `output`
- **outputFile**: `Output/curated/customer_branch_activity.csv`
- **includeHeader**: true
- **writeMode**: Append
- **lineEnding**: CRLF
- **trailerFormat**: not specified (no trailer)

## Source Tables

| Table | Columns Used | Filters | Evidence |
|-------|-------------|---------|----------|
| datalake.branch_visits | visit_id, customer_id, branch_id, visit_purpose | Effective date range (injected by executor) | [customer_branch_activity.json:8-10] |
| datalake.customers | id, first_name, last_name | Effective date range (injected by executor) | [customer_branch_activity.json:14-16] |
| datalake.branches | branch_id, branch_name | Effective date range (injected by executor) | [customer_branch_activity.json:20-22] |

### Schema Details

**branch_visits**: visit_id (integer), customer_id (integer), branch_id (integer), visit_timestamp (timestamp), visit_purpose (varchar), as_of (date)

**customers**: id (integer), prefix (varchar), first_name (varchar), last_name (varchar), sort_name (varchar), suffix (varchar), birthdate (date), as_of (date)

**branches**: branch_id (integer), branch_name (varchar), address_line1 (varchar), city (varchar), state_province (varchar), postal_code (varchar), country (char), as_of (date)

## Business Rules

BR-1: The External module (CustomerBranchActivityBuilder) counts total branch visits per customer across all as_of dates in the effective range, producing a single aggregate count per customer.
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerBranchActivityBuilder.cs:42-49] iterates all branch_visits rows, counts by customer_id

BR-2: Customer names are resolved via a lookup dictionary keyed by customer id. When multiple as_of dates are in the effective range, last-write-wins applies.
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerBranchActivityBuilder.cs:34-39] dictionary keyed by custId

BR-3: If customers DataFrame is null or empty, an empty output DataFrame is returned (weekend guard).
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerBranchActivityBuilder.cs:19-23] explicit null/empty check

BR-4: If branch_visits DataFrame is null or empty, an empty output DataFrame is returned.
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerBranchActivityBuilder.cs:25-29] explicit null/empty check

BR-5: The `as_of` value in the output is taken from the FIRST row of the branch_visits DataFrame, not from each individual visit.
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerBranchActivityBuilder.cs:52] `var asOf = branchVisits.Rows[0]["as_of"]` — single as_of for all output rows

BR-6: When a customer_id from branch_visits has no match in the customers lookup, first_name and last_name are null.
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerBranchActivityBuilder.cs:61-68] ContainsKey check, defaults remain null

BR-7: The `branches` table is sourced but never consumed by the External module. The module only reads `branch_visits` and `customers`.
- Confidence: HIGH
- Evidence: [customer_branch_activity.json:20-22] DataSourcing; [ExternalModules/CustomerBranchActivityBuilder.cs:15-16] only reads branch_visits and customers

BR-8: The `visit_id`, `branch_id`, and `visit_purpose` columns are sourced from branch_visits but not used by the External module.
- Confidence: HIGH
- Evidence: [customer_branch_activity.json:10] sourced; [ExternalModules/CustomerBranchActivityBuilder.cs:42-49] only customer_id used

BR-9: Output row order follows dictionary enumeration order of visitCounts, which is insertion order (order of first encounter of each customer_id in branch_visits).
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerBranchActivityBuilder.cs:56-78] iterates visitCounts dictionary

BR-10: Visit counts aggregate across ALL as_of dates in the effective range — not per-date. A customer who visited 3 times on day 1 and 2 times on day 2 gets visit_count = 5.
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerBranchActivityBuilder.cs:42-49] no date filtering; counts all rows

## Output Schema

| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| customer_id | branch_visits.customer_id | Grouped key | [CustomerBranchActivityBuilder.cs:71] |
| first_name | customers.first_name | Lookup by customer_id | [CustomerBranchActivityBuilder.cs:72] |
| last_name | customers.last_name | Lookup by customer_id | [CustomerBranchActivityBuilder.cs:73] |
| as_of | branch_visits.Rows[0]["as_of"] | First row's as_of for all output | [CustomerBranchActivityBuilder.cs:52] |
| visit_count | branch_visits | Count of all visits per customer | [CustomerBranchActivityBuilder.cs:74] |

## Non-Deterministic Fields
None identified. Output order depends on dictionary insertion order, which is deterministic given the same input data.

## Write Mode Implications
**Append mode**: Each execution appends data to `Output/curated/customer_branch_activity.csv`. Re-running the same date produces duplicate rows. No trailer means no delimiter between runs.

## Edge Cases

- **Weekend guard**: If customers is empty, output is empty with correct schema.
- **No visits**: If branch_visits is empty, output is empty.
- **Multi-day effective range**: Visit counts accumulate across all days. The as_of on all output rows is from the first branch_visit row, which may not represent the full range.
- **Customer not in customers table**: Visit is counted, but first_name and last_name are null.
- **CRLF line endings**: Windows-style.
- **No trailer**: Unlike some other branch jobs, no trailer line is appended.

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|------------------|
| BR-1: Aggregate visit count per customer | [CustomerBranchActivityBuilder.cs:42-49] |
| BR-2: Customer name lookup | [CustomerBranchActivityBuilder.cs:34-39] |
| BR-3: Weekend guard on customers | [CustomerBranchActivityBuilder.cs:19-23] |
| BR-4: Empty visits guard | [CustomerBranchActivityBuilder.cs:25-29] |
| BR-5: Single as_of from first row | [CustomerBranchActivityBuilder.cs:52] |
| BR-6: Null names for missing customers | [CustomerBranchActivityBuilder.cs:61-68] |
| BR-7: branches unused | [customer_branch_activity.json:20-22], [CustomerBranchActivityBuilder.cs:15-16] |
| BR-8: Unused sourced columns | [customer_branch_activity.json:10], [CustomerBranchActivityBuilder.cs:42-49] |
| BR-9: Dictionary insertion order | [CustomerBranchActivityBuilder.cs:56-78] |
| BR-10: Cross-date aggregation | [CustomerBranchActivityBuilder.cs:42-49] |

## Open Questions

OQ-1: Is the single as_of from the first branch_visit row intentional, or should each customer's as_of reflect their specific visit dates?
- Confidence: MEDIUM — simplification that may cause confusion in multi-day ranges

OQ-2: Why is the `branches` table sourced but never used by the External module?
- Confidence: MEDIUM — vestigial or planned enrichment
