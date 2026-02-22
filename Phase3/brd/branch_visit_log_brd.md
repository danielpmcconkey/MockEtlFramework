# BRD: BranchVisitLog

## Overview
Produces an enriched log of branch visits by joining visit records with customer names and branch names. Each visit row is augmented with the customer's first_name/last_name and the branch_name. Uses Append mode to build a historical log across all effective dates.

## Source Tables
| Table | Schema | Columns Used | Join/Filter Logic | Evidence |
|-------|--------|-------------|-------------------|----------|
| branch_visits | datalake | visit_id, customer_id, branch_id, visit_timestamp, visit_purpose | Filtered by effective date range. All visits included. | [JobExecutor/Jobs/branch_visit_log.json:6-11] |
| branches | datalake | branch_id, branch_name, address_line1, city, state_province, postal_code, country | Filtered by effective date range. Joined to visits via branch_id. Only branch_name is used. | [JobExecutor/Jobs/branch_visit_log.json:13-18] |
| customers | datalake | id, first_name, last_name | Filtered by effective date range. Joined to visits via customer_id = id. | [JobExecutor/Jobs/branch_visit_log.json:20-24] |
| addresses | datalake | address_id, customer_id, address_line1, city | Filtered by effective date range. Sourced but NOT used in output. | [JobExecutor/Jobs/branch_visit_log.json:26-31] |

## Business Rules
BR-1: Each branch visit row is enriched with the branch_name by looking up branch_id in the branches DataFrame.
- Confidence: HIGH
- Evidence: [ExternalModules/BranchVisitEnricher.cs:34-43] Builds `branchNames` dictionary from branches.Rows keyed by branch_id
- Evidence: [ExternalModules/BranchVisitEnricher.cs:62] `var branchName = branchNames.GetValueOrDefault(branchId, "");`

BR-2: Each branch visit row is enriched with the customer's first_name and last_name by looking up customer_id in the customers DataFrame.
- Confidence: HIGH
- Evidence: [ExternalModules/BranchVisitEnricher.cs:46-53] Builds `customerNames` dictionary from customers.Rows keyed by id
- Evidence: [ExternalModules/BranchVisitEnricher.cs:63] Looks up customer names via `customerNames.GetValueOrDefault(customerId, (null!, null!))`

BR-3: If a branch_id has no match in the branches lookup, an empty string is used for branch_name.
- Confidence: HIGH
- Evidence: [ExternalModules/BranchVisitEnricher.cs:62] `branchNames.GetValueOrDefault(branchId, "")`

BR-4: If a customer_id has no match in the customers lookup, null values are used for first_name and last_name.
- Confidence: HIGH
- Evidence: [ExternalModules/BranchVisitEnricher.cs:63] `customerNames.GetValueOrDefault(customerId, (null!, null!))` returns (null, null) for unmatched customers

BR-5: The addresses table is sourced but never used in the output. The BranchVisitEnricher only reads "branch_visits", "branches", and "customers" from shared state.
- Confidence: HIGH
- Evidence: [ExternalModules/BranchVisitEnricher.cs:16-18] Only `branch_visits`, `branches`, and `customers` are read -- no reference to "addresses"

BR-6: Write mode is Append -- each effective date's enriched visits are added to the existing table, building a historical log.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/branch_visit_log.json:42] `"writeMode": "Append"`
- Evidence: [curated.branch_visit_log] Multiple as_of dates present (23 dates, weekday-only)

BR-7: The job returns an empty output if customers is null or empty (weekend guard).
- Confidence: HIGH
- Evidence: [ExternalModules/BranchVisitEnricher.cs:21-25] Comment says "Weekend guard on customers empty"; returns empty DataFrame if customers is null or has 0 rows
- Note: Customers table has weekday-only data. On weekends, the customers DataSourcing returns empty, triggering this guard.

BR-8: The job also returns empty output if branch_visits is null or empty.
- Confidence: HIGH
- Evidence: [ExternalModules/BranchVisitEnricher.cs:27-30] Returns empty DataFrame if branch_visits is null or has 0 rows

BR-9: The output preserves the visit_timestamp as-is from the source.
- Confidence: HIGH
- Evidence: [ExternalModules/BranchVisitEnricher.cs:72] `["visit_timestamp"] = visitRow["visit_timestamp"]`

BR-10: The job runs only on business days (weekdays) because the customers table is weekday-only, and the weekend guard (BR-7) produces empty output when customers is empty.
- Confidence: HIGH
- Evidence: [ExternalModules/BranchVisitEnricher.cs:21-25] Weekend guard on customers
- Evidence: [curated.branch_visit_log] 23 dates = weekdays only in October 2024

BR-11: No filtering is applied to branch visits -- all visits for the effective date are included.
- Confidence: HIGH
- Evidence: [ExternalModules/BranchVisitEnricher.cs:56-77] Iterates all branch visit rows without filtering

BR-12: The branch lookup builds from ALL branch rows (across as_of dates if multi-date). Last-write-wins for duplicate branch_ids. Similarly for customer lookup.
- Confidence: MEDIUM
- Evidence: [ExternalModules/BranchVisitEnricher.cs:37-42] Dictionary built from all branch rows; [BranchVisitEnricher.cs:48-53] Same for customer rows
- Note: In single-date execution mode, this is a non-issue.

## Output Schema
| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| visit_id | datalake.branch_visits.visit_id | Pass-through | [BranchVisitEnricher.cs:66] |
| customer_id | datalake.branch_visits.customer_id | Pass-through | [BranchVisitEnricher.cs:67] |
| first_name | datalake.customers.first_name | Looked up via customer_id -> id; null if no match | [BranchVisitEnricher.cs:68] |
| last_name | datalake.customers.last_name | Looked up via customer_id -> id; null if no match | [BranchVisitEnricher.cs:69] |
| branch_id | datalake.branch_visits.branch_id | Pass-through | [BranchVisitEnricher.cs:70] |
| branch_name | datalake.branches.branch_name | Looked up via branch_id; empty string if no match | [BranchVisitEnricher.cs:71] |
| visit_timestamp | datalake.branch_visits.visit_timestamp | Pass-through | [BranchVisitEnricher.cs:72] |
| visit_purpose | datalake.branch_visits.visit_purpose | Pass-through | [BranchVisitEnricher.cs:73] |
| as_of | datalake.branch_visits.as_of | Pass-through | [BranchVisitEnricher.cs:74] |

## Edge Cases
- **NULL handling for customer names**: Customer first_name and last_name use `?.ToString() ?? ""` when building the lookup (converting NULL to empty string). [BranchVisitEnricher.cs:50-51]
- **NULL handling for branch names**: Branch names use `?.ToString() ?? ""` when building the lookup. [BranchVisitEnricher.cs:41]
- **Missing customer match**: Returns (null, null) from `GetValueOrDefault` with default `(null!, null!)`. This means unmatched customers get null first_name and last_name in the output. [BranchVisitEnricher.cs:63]
- **Missing branch match**: Returns empty string for branch_name. [BranchVisitEnricher.cs:62]
- **Weekend guard**: Customers table is weekday-only. On weekends, customers DataFrame is empty, triggering the guard to return empty output. Branch_visits has weekend data but it is not processed due to the guard. [BranchVisitEnricher.cs:21-25]
- **Empty branch_visits**: If no visits for the date, returns empty output. [BranchVisitEnricher.cs:27-30]
- **Branches null**: If branches is null, the branchNames dictionary stays empty, and all visit rows get empty string for branch_name. [BranchVisitEnricher.cs:36]

## Traceability Matrix
| Requirement | Evidence Citations |
|-------------|-------------------|
| BR-1 | [BranchVisitEnricher.cs:34-43], [BranchVisitEnricher.cs:62] |
| BR-2 | [BranchVisitEnricher.cs:46-53], [BranchVisitEnricher.cs:63] |
| BR-3 | [BranchVisitEnricher.cs:62] |
| BR-4 | [BranchVisitEnricher.cs:63] |
| BR-5 | [BranchVisitEnricher.cs:16-18], [branch_visit_log.json:26-31] |
| BR-6 | [branch_visit_log.json:42], [curated.branch_visit_log dates] |
| BR-7 | [BranchVisitEnricher.cs:21-25] |
| BR-8 | [BranchVisitEnricher.cs:27-30] |
| BR-9 | [BranchVisitEnricher.cs:72] |
| BR-10 | [BranchVisitEnricher.cs:21-25], [curated.branch_visit_log dates] |
| BR-11 | [BranchVisitEnricher.cs:56-77] |
| BR-12 | [BranchVisitEnricher.cs:37-42], [BranchVisitEnricher.cs:48-53] |

## Open Questions
- **Why is addresses sourced but unused?** The addresses DataSourcing module is configured in the job but never referenced by BranchVisitEnricher. Confidence: MEDIUM that it is intentionally unused.
- **Asymmetric null defaults**: Missing branches get empty string ("") for branch_name while missing customers get null for first_name/last_name. This inconsistency may or may not be intentional. Confidence: MEDIUM.
- **Weekend branch visit data is lost**: Branch_visits has weekend data but the weekend guard prevents processing on weekends because customers is empty on weekends. This means weekend visit data is never output. Confidence: HIGH that this is by design (the code explicitly comments "Weekend guard on customers empty").
