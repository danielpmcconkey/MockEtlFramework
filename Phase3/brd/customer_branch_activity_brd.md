# CustomerBranchActivity — Business Requirements Document

## Overview

Produces a per-customer count of branch visits for each effective date, enriched with customer name. Shows how many times each customer visited any branch on a given day. Output is appended daily.

## Source Tables

| Table | Schema | Columns Used | Purpose |
|-------|--------|-------------|---------|
| `datalake.branch_visits` | datalake | visit_id, customer_id, branch_id, visit_purpose | Branch visit records; only customer_id is used for counting |
| `datalake.customers` | datalake | id, first_name, last_name | Customer name lookup |
| `datalake.branches` | datalake | branch_id, branch_name | **SOURCED BUT NEVER USED** — not referenced by the External module |

## Business Rules

BR-1: One output row per customer who has at least one branch visit on the effective date.
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerBranchActivityBuilder.cs:42-49] Groups visits by customer_id, counts per customer
- Evidence: [curated.customer_branch_activity] Each customer_id appears once per as_of

BR-2: visit_count is the total number of branch visit records for each customer on the effective date.
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerBranchActivityBuilder.cs:46-49] `visitCounts[custId]++`
- Evidence: [curated.customer_branch_activity] visit_count values match counts from datalake.branch_visits

BR-3: Customer name (first_name, last_name) is looked up from the customers DataFrame. If a customer is not found in the customers table, both name fields are NULL.
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerBranchActivityBuilder.cs:61-68] `if (customerNames.ContainsKey(customerId))` else names stay null
- Evidence: [ExternalModules/CustomerBranchActivityBuilder.cs:32-39] Customer lookup built from customers DataFrame

BR-4: The as_of value comes from the first row of the branch_visits DataFrame.
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerBranchActivityBuilder.cs:52] `var asOf = branchVisits.Rows[0]["as_of"];`

BR-5: When either branch_visits or customers DataFrames are empty, an empty output is produced.
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerBranchActivityBuilder.cs:19-29] Null/empty checks for both DataFrames return empty output

BR-6: Output uses Append mode — each daily run appends rows.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/customer_branch_activity.json:34] `"writeMode": "Append"`

BR-7: Weekend behavior — branch_visits has data every day including weekends, but customers does NOT have weekend data. Since the External module returns empty when customers is empty, no output is produced on weekends.
- Confidence: HIGH
- Evidence: [datalake.branch_visits] Has data for Oct 5-6 (20 and 17 rows)
- Evidence: [datalake.customers] No data for Oct 5-6
- Evidence: [curated.customer_branch_activity] No rows for Oct 5-6 (weekends)
- Evidence: [ExternalModules/CustomerBranchActivityBuilder.cs:19-22] Returns empty when customers is empty

## Output Schema

| Column | Source | Transformation |
|--------|--------|----------------|
| customer_id | branch_visits.customer_id | Direct (integer) |
| first_name | customers.first_name | Direct; NULL if customer not found |
| last_name | customers.last_name | Direct; NULL if customer not found |
| as_of | branch_visits (first row) | Direct from DataFrame |
| visit_count | Computed | COUNT of branch_visits per customer_id |

## Edge Cases

- **No branch visits for effective date**: Empty output (BR-5).
- **No customers for effective date (weekends)**: Empty output (BR-5). Branch visits exist on weekends but customers don't, so no output on Sat/Sun.
- **Customer in branch_visits but not in customers table**: Row is still produced with NULL first_name and last_name (BR-3). This could happen if a customer record is missing.
- **Output ordering**: The External module does not sort output rows. Order depends on dictionary iteration order of `visitCounts`. In practice this means customer_id ordering is not deterministic.

## Anti-Patterns Identified

- **AP-1: Redundant Data Sourcing** — The `branches` table is sourced via DataSourcing (branch_id, branch_name) but never referenced by the External module. The module only uses `branch_visits` and `customers`. Evidence: [JobExecutor/Jobs/customer_branch_activity.json:22-26] branches sourced; [ExternalModules/CustomerBranchActivityBuilder.cs] no reference to "branches". V2 approach: Remove the branches DataSourcing module.

- **AP-3: Unnecessary External Module** — The logic is: count branch visits per customer, join with customer names. This is a simple GROUP BY + LEFT JOIN, expressible entirely in SQL. Evidence: [ExternalModules/CustomerBranchActivityBuilder.cs:42-78] Row-by-row grouping and joining. V2 approach: Replace with a SQL Transformation using `GROUP BY customer_id` and `LEFT JOIN customers`.

- **AP-4: Unused Columns Sourced** — From branch_visits: `visit_id`, `branch_id`, and `visit_purpose` are sourced but only `customer_id` is used for counting (and `as_of` which is auto-added). Evidence: [JobExecutor/Jobs/customer_branch_activity.json:11] includes visit_id, branch_id, visit_purpose; [ExternalModules/CustomerBranchActivityBuilder.cs:43-49] only customer_id is accessed. V2 approach: Only source customer_id from branch_visits.

- **AP-6: Row-by-Row Iteration in External Module** — The module iterates over visits one by one to count them, then iterates over the count dictionary to build output. SQL GROUP BY does this natively. Evidence: [ExternalModules/CustomerBranchActivityBuilder.cs:42-78] foreach loops. V2 approach: Replace with SQL.

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|-------------------|
| BR-1 | [ExternalModules/CustomerBranchActivityBuilder.cs:42-49] |
| BR-2 | [ExternalModules/CustomerBranchActivityBuilder.cs:46-49] |
| BR-3 | [ExternalModules/CustomerBranchActivityBuilder.cs:61-68,32-39] |
| BR-4 | [ExternalModules/CustomerBranchActivityBuilder.cs:52] |
| BR-5 | [ExternalModules/CustomerBranchActivityBuilder.cs:19-29] |
| BR-6 | [JobExecutor/Jobs/customer_branch_activity.json:34] |
| BR-7 | [datalake data patterns], [ExternalModules/CustomerBranchActivityBuilder.cs:19-22] |

## Open Questions

- **Output ordering**: The External module does not define a sort order for output rows. Dictionary iteration order in C# is insertion order for `Dictionary<int, int>`, which follows the order visits are encountered. V2 should either match this order or establish a deterministic ORDER BY. Confidence: MEDIUM — order may not matter for Append mode output.
