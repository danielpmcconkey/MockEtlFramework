# BranchVisitLog — Business Requirements Document

## Overview

This job produces an enriched log of branch visits by joining branch visit records with branch names and customer names. Each visit is augmented with the branch's name and the visiting customer's first and last name. The result is written to `curated.branch_visit_log` using Append mode, accumulating a historical record.

## Source Tables

### datalake.branch_visits
- **Columns sourced:** visit_id, customer_id, branch_id, visit_timestamp, visit_purpose
- **Columns actually used:** All 5 sourced columns plus framework-injected as_of
- **Join/filter logic:** No filtering. All visit rows for the effective date are included.
- **Evidence:** [ExternalModules/BranchVisitEnricher.cs:57-76] All visit columns are mapped to output.

### datalake.branches
- **Columns sourced:** branch_id, branch_name, address_line1, city, state_province, postal_code, country
- **Columns actually used:** branch_id (join key), branch_name (output)
- **Evidence:** [ExternalModules/BranchVisitEnricher.cs:38-43] Only branch_id and branch_name are read from the branches DataFrame.

### datalake.customers
- **Columns sourced:** id, first_name, last_name
- **Columns actually used:** All 3 (id as join key, first_name and last_name in output)
- **Evidence:** [ExternalModules/BranchVisitEnricher.cs:47-53] Customer lookup built from id -> (first_name, last_name).

### datalake.addresses
- **Columns sourced:** address_id, customer_id, address_line1, city
- **Usage:** NONE — loaded into shared state as "addresses" but never accessed by the External module.
- **Evidence:** [ExternalModules/BranchVisitEnricher.cs] No reference to `sharedState["addresses"]` anywhere.

## Business Rules

BR-1: Each branch visit is enriched with the branch name by joining on branch_id.
- Confidence: HIGH
- Evidence: [ExternalModules/BranchVisitEnricher.cs:38-43] Branch name lookup built by branch_id.
- Evidence: [ExternalModules/BranchVisitEnricher.cs:62] `var branchName = branchNames.GetValueOrDefault(branchId, "");`
- Evidence: [curated.branch_visit_log] branch_name column populated correctly (e.g., "Austin TX Branch" for branch_id 6).

BR-2: Each branch visit is enriched with the customer's first and last name by joining on customer_id.
- Confidence: HIGH
- Evidence: [ExternalModules/BranchVisitEnricher.cs:47-53] Customer name lookup built from customers.id.
- Evidence: [ExternalModules/BranchVisitEnricher.cs:63] `var (firstName, lastName) = customerNames.GetValueOrDefault(customerId, (null!, null!));`
- Evidence: [curated.branch_visit_log] first_name/last_name populated (e.g., "Ava" / "Garcia" for customer_id 1006).

BR-3: If a branch_id has no matching branch record, branch_name defaults to empty string.
- Confidence: HIGH
- Evidence: [ExternalModules/BranchVisitEnricher.cs:62] `GetValueOrDefault(branchId, "")` returns "" for missing branches.

BR-4: If a customer_id has no matching customer record, first_name and last_name default to null (null! in C#).
- Confidence: HIGH
- Evidence: [ExternalModules/BranchVisitEnricher.cs:63] `GetValueOrDefault(customerId, (null!, null!))` returns nulls for missing customers.
- Note: This is different from AccountCustomerJoin which defaults to empty strings. This is an asymmetric NULL handling pattern.

BR-5: The output contains 9 columns: visit_id, customer_id, first_name, last_name, branch_id, branch_name, visit_timestamp, visit_purpose, as_of.
- Confidence: HIGH
- Evidence: [ExternalModules/BranchVisitEnricher.cs:10-14] `outputColumns` lists these 9 columns.
- Evidence: [curated.branch_visit_log] Schema confirms these 9 columns (first_name and last_name are nullable).

BR-6: Data is written in Append mode, accumulating visits across effective dates.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/branch_visit_log.json:42] `"writeMode": "Append"`.
- Evidence: [curated.branch_visit_log] Contains multiple as_of dates with varying row counts.

BR-7: When the customers DataFrame is null or empty, an empty output is returned (even if branch_visits has data).
- Confidence: HIGH
- Evidence: [ExternalModules/BranchVisitEnricher.cs:21-25] The customers null/empty check comes FIRST, returning empty before checking branch_visits.

BR-8: When branch_visits is null or empty (but customers is not), an empty output is returned.
- Confidence: HIGH
- Evidence: [ExternalModules/BranchVisitEnricher.cs:27-30] Second guard after customers check.

BR-9: All branch visits for the effective date are included (no filtering by visit_purpose, branch_id, or any other attribute).
- Confidence: HIGH
- Evidence: [ExternalModules/BranchVisitEnricher.cs:56-76] foreach iterates all visit rows without conditions.

## Output Schema

| Column | Source | Transformation |
|--------|--------|----------------|
| visit_id | datalake.branch_visits.visit_id | Direct pass-through |
| customer_id | datalake.branch_visits.customer_id | Direct pass-through |
| first_name | datalake.customers.first_name | Joined via customer_id; NULL if no match |
| last_name | datalake.customers.last_name | Joined via customer_id; NULL if no match |
| branch_id | datalake.branch_visits.branch_id | Direct pass-through |
| branch_name | datalake.branches.branch_name | Joined via branch_id; empty string if no match |
| visit_timestamp | datalake.branch_visits.visit_timestamp | Direct pass-through |
| visit_purpose | datalake.branch_visits.visit_purpose | Direct pass-through |
| as_of | datalake.branch_visits.as_of (injected by framework) | Direct pass-through |

## Edge Cases

- **Missing customer:** first_name and last_name become NULL (not empty string — contrast with AccountCustomerJoin).
- **Missing branch:** branch_name becomes empty string.
- **Weekend dates:** branch_visits has data for weekends (unlike accounts/customers). However, customers does NOT have weekend data, and the External module checks customers first — if customers is empty, it returns empty output even if visits exist. This means weekend visits with no customer data will produce zero output rows.
- **Customers empty on weekends:** Since the customers null/empty guard comes first (BR-7), weekends where datalake.customers has no snapshot will produce empty output, even though branch_visits has data.
- **Branches available on weekends:** datalake.branches does have weekend data, but this doesn't matter if customers is empty.

## Anti-Patterns Identified

- **AP-1: Redundant Data Sourcing** — The `addresses` DataSourcing module fetches address_id, customer_id, address_line1, city from `datalake.addresses`, but the External module never accesses `sharedState["addresses"]`.
  - Evidence: [JobExecutor/Jobs/branch_visit_log.json:28-33] addresses DataSourcing defined; [ExternalModules/BranchVisitEnricher.cs] no reference to "addresses".
  - V2 approach: Remove the addresses DataSourcing module entirely.

- **AP-3: Unnecessary External Module** — The `BranchVisitEnricher` performs two LEFT JOINs (visits -> branches by branch_id, visits -> customers by customer_id). This is straightforward SQL.
  - Evidence: [ExternalModules/BranchVisitEnricher.cs] Logic is: build branch lookup, build customer lookup, iterate visits and join.
  - V2 approach: Replace with SQL Transformation using LEFT JOINs. Note: must match the NULL vs empty string behavior exactly.

- **AP-4: Unused Columns Sourced** — The branches DataSourcing fetches address_line1, city, state_province, postal_code, and country, but only branch_id and branch_name are used.
  - Evidence: [JobExecutor/Jobs/branch_visit_log.json:16] columns include address_line1, city, state_province, postal_code, country; [ExternalModules/BranchVisitEnricher.cs:38-43] only branch_id and branch_name are read.
  - V2 approach: Only source branch_id and branch_name from branches.

- **AP-5: Asymmetric NULL/Default Handling** — Missing branch names default to empty string, while missing customer names default to NULL. These are both string columns representing names of missing entities, but are handled inconsistently.
  - Evidence: [ExternalModules/BranchVisitEnricher.cs:62] branch_name defaults to ""; [ExternalModules/BranchVisitEnricher.cs:63] customer names default to (null!, null!).
  - V2 approach: The V2 must reproduce this asymmetric behavior for output equivalence. Document as a known inconsistency. In the SQL, use `COALESCE(b.branch_name, '')` for branch_name and leave customer names as NULL on LEFT JOIN miss.

- **AP-6: Row-by-Row Iteration in External Module** — The External module iterates visits row-by-row and does dictionary lookups, when SQL LEFT JOIN handles this directly.
  - Evidence: [ExternalModules/BranchVisitEnricher.cs:56] `foreach (var visitRow in branchVisits.Rows)`
  - V2 approach: Replace with SQL LEFT JOINs.

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|-------------------|
| BR-1 | [ExternalModules/BranchVisitEnricher.cs:38-43, 62], [curated.branch_visit_log] branch_name populated |
| BR-2 | [ExternalModules/BranchVisitEnricher.cs:47-53, 63], [curated.branch_visit_log] names populated |
| BR-3 | [ExternalModules/BranchVisitEnricher.cs:62] GetValueOrDefault default "" |
| BR-4 | [ExternalModules/BranchVisitEnricher.cs:63] GetValueOrDefault default (null!, null!) |
| BR-5 | [ExternalModules/BranchVisitEnricher.cs:10-14], [curated.branch_visit_log] schema |
| BR-6 | [JobExecutor/Jobs/branch_visit_log.json:42] |
| BR-7 | [ExternalModules/BranchVisitEnricher.cs:21-25] |
| BR-8 | [ExternalModules/BranchVisitEnricher.cs:27-30] |
| BR-9 | [ExternalModules/BranchVisitEnricher.cs:56-76] no conditions in loop |

## Open Questions

- **Q1:** The weekend behavior is important: branch_visits has weekend data, but customers does not. The customers-first empty guard means weekend visits produce no output. This is consistent with the curated output (no weekend as_of dates in branch_visit_log). However, it's unclear if this is intentional or an artifact of the guard ordering. Confidence: MEDIUM that this is unintentional — the guard should logically check branch_visits first, but the result is the same (no output for weekends either way, since the join would produce NULL customer names).
