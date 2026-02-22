# BRD: CustomerBranchActivity

## Overview
This job produces a per-customer visit count summary for each effective date, showing how many branch visits each customer made on that day, enriched with the customer's name.

## Source Tables

| Table | Schema | Columns Used | Join/Filter Logic | Evidence |
|-------|--------|-------------|-------------------|----------|
| branch_visits | datalake | visit_id, customer_id, branch_id, visit_purpose | Sourced via DataSourcing for effective date range | [customer_branch_activity.json:7-11] |
| customers | datalake | id, first_name, last_name | Sourced via DataSourcing for effective date range | [customer_branch_activity.json:13-17] |
| branches | datalake | branch_id, branch_name | Sourced via DataSourcing but NOT USED in the External module | [customer_branch_activity.json:19-23] |

## Business Rules

BR-1: Branch visits are grouped by customer_id and counted to produce visit_count per customer.
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerBranchActivityBuilder.cs:42-49] Loop counts visits per customer_id using `visitCounts` dictionary

BR-2: Only customers who have branch visits are included in the output (INNER JOIN equivalent with visits).
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerBranchActivityBuilder.cs:56] Loop iterates `visitCounts` (customers with visits), not all customers

BR-3: Customer name is looked up from the customers DataFrame; if not found, first_name and last_name are null.
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerBranchActivityBuilder.cs:61-68] `firstName` and `lastName` start as null; only set if `customerNames.ContainsKey(customerId)`

BR-4: The as_of value is taken from the first branch_visit row in the DataFrame.
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerBranchActivityBuilder.cs:52] `var asOf = branchVisits.Rows[0]["as_of"];`

BR-5: Data is written in Append mode -- each effective date's activity accumulates.
- Confidence: HIGH
- Evidence: [customer_branch_activity.json:35] `"writeMode": "Append"`
- Evidence: [curated.customer_branch_activity] Multiple as_of dates with varying row counts

BR-6: If either customers or branch_visits DataFrame is empty, an empty DataFrame is returned.
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerBranchActivityBuilder.cs:19-29] Two separate guards: `customers.Count == 0` and `branchVisits.Count == 0` both return empty

BR-7: Output rows appear to be unordered -- the iteration order depends on the Dictionary enumeration order of visitCounts.
- Confidence: MEDIUM
- Evidence: [ExternalModules/CustomerBranchActivityBuilder.cs:56] `foreach (var kvp in visitCounts)` -- Dictionary enumeration order is not guaranteed but is typically insertion order in .NET

BR-8: The branches DataSourcing module is declared in the job config but NOT used by the External module.
- Confidence: HIGH
- Evidence: [customer_branch_activity.json:19-23] branches is sourced but [CustomerBranchActivityBuilder.cs] never references `sharedState["branches"]`

BR-9: Output contains only customers with at least one visit; no weekday data gaps exist for branch_visits since branch_visits has data for all 31 days including weekends.
- Confidence: HIGH
- Evidence: [datalake.branch_visits] All 31 dates present
- Evidence: [curated.customer_branch_activity] 23 dates present (only weekdays, because customers only has weekday data and empty customers triggers empty output)

BR-10: The output only has rows for weekdays (23 dates) even though branch_visits has weekend data, because the customers DataFrame is empty on weekends (customers table is weekday-only), triggering the empty guard.
- Confidence: HIGH
- Evidence: [curated.customer_branch_activity] 23 rows by date matching weekday-only pattern
- Evidence: [datalake.customers] 23 distinct as_of dates (weekdays only)
- Evidence: [CustomerBranchActivityBuilder.cs:19-20] `if (customers == null || customers.Count == 0)` returns empty

## Output Schema

| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| customer_id | branch_visits.customer_id | Cast to int | [CustomerBranchActivityBuilder.cs:45,72] |
| first_name | customers.first_name | ToString or null if customer not found | [CustomerBranchActivityBuilder.cs:37,65,73] |
| last_name | customers.last_name | ToString or null if customer not found | [CustomerBranchActivityBuilder.cs:38,66,74] |
| as_of | branch_visits.Rows[0].as_of | From first visit row | [CustomerBranchActivityBuilder.cs:52,75] |
| visit_count | Computed | Count of visits per customer_id | [CustomerBranchActivityBuilder.cs:48,76] |

## Edge Cases

- **NULL handling**: Customer names are null if the customer_id from visits is not found in the customers DataFrame. visit_count is always at least 1 (only customers with visits appear).
  - Evidence: [CustomerBranchActivityBuilder.cs:61-68] Null defaults for names
- **Weekend/date fallback**: Branch visits exist on weekends, but customers data does not. The customers-empty guard means no output is produced on weekends. With Append mode, this simply means no weekend rows exist.
  - Evidence: [curated.customer_branch_activity] Only 23 dates (weekdays); [datalake.branch_visits] 31 dates
- **Zero-row behavior**: If no visits exist for a date, empty DataFrame is returned. No rows written for that date in Append mode.
  - Evidence: [CustomerBranchActivityBuilder.cs:25-29]
- **Output ordering**: No explicit ordering -- rows appear in dictionary enumeration order. This may differ between runs.
  - Evidence: [CustomerBranchActivityBuilder.cs:56] Dictionary iteration order

## Traceability Matrix

| Requirement | Evidence Citations |
|-------------|-------------------|
| BR-1 | [CustomerBranchActivityBuilder.cs:42-49] |
| BR-2 | [CustomerBranchActivityBuilder.cs:56] |
| BR-3 | [CustomerBranchActivityBuilder.cs:61-68] |
| BR-4 | [CustomerBranchActivityBuilder.cs:52] |
| BR-5 | [customer_branch_activity.json:35], [curated.customer_branch_activity dates] |
| BR-6 | [CustomerBranchActivityBuilder.cs:19-29] |
| BR-7 | [CustomerBranchActivityBuilder.cs:56] |
| BR-8 | [customer_branch_activity.json:19-23], [CustomerBranchActivityBuilder.cs full source] |
| BR-9 | [datalake.branch_visits dates], [curated.customer_branch_activity dates] |
| BR-10 | [curated.customer_branch_activity dates], [datalake.customers dates], [CustomerBranchActivityBuilder.cs:19-20] |

## Open Questions

- **Branches sourced but unused**: Same pattern as other jobs. Confidence: HIGH that unused.
- **Output ordering**: No guaranteed ordering. In Append mode with comparison testing, row ordering differences may cause false mismatches. Confidence: MEDIUM (behavior depends on .NET dictionary implementation).
- **as_of from first visit row**: Using `Rows[0]["as_of"]` means all output rows get the same as_of. If the DataSourcing returns multiple dates' data (e.g., during a backfill), all rows would still get the as_of from the first visit row. In normal single-date operation this is correct. Confidence: HIGH for single-date runs.
