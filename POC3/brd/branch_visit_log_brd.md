# BranchVisitLog — Business Requirements Document

## Overview
Produces an enriched log of branch visits by joining visit records with customer names and branch names via an External module (BranchVisitEnricher). Each visit row is augmented with the customer's first/last name and the branch name.

## Output Type
ParquetFileWriter

## Writer Configuration
- **source**: `output`
- **outputDirectory**: `Output/curated/branch_visit_log/`
- **numParts**: 3
- **writeMode**: Append

## Source Tables

| Table | Columns Used | Filters | Evidence |
|-------|-------------|---------|----------|
| datalake.branch_visits | visit_id, customer_id, branch_id, visit_timestamp, visit_purpose | Effective date range (injected by executor) | [branch_visit_log.json:8-10] |
| datalake.branches | branch_id, branch_name, address_line1, city, state_province, postal_code, country | Effective date range (injected by executor) | [branch_visit_log.json:14-16] |
| datalake.customers | id, first_name, last_name | Effective date range (injected by executor) | [branch_visit_log.json:20-22] |
| datalake.addresses | address_id, customer_id, address_line1, city | Effective date range (injected by executor) | [branch_visit_log.json:26-28] |

### Schema Details

**branch_visits**: visit_id (integer), customer_id (integer), branch_id (integer), visit_timestamp (timestamp), visit_purpose (varchar), as_of (date)

**branches**: branch_id (integer), branch_name (varchar), address_line1 (varchar), city (varchar), state_province (varchar), postal_code (varchar), country (char), as_of (date)

**customers**: id (integer), prefix (varchar), first_name (varchar), last_name (varchar), sort_name (varchar), suffix (varchar), birthdate (date), as_of (date)

**addresses**: address_id (integer), customer_id (integer), address_line1 (varchar), city (varchar), state_province (varchar), postal_code (varchar), country (char), start_date (date), end_date (date), as_of (date)

## Business Rules

BR-1: The External module (BranchVisitEnricher) performs row-by-row enrichment of branch_visits with customer names and branch names using in-memory lookups.
- Confidence: HIGH
- Evidence: [ExternalModules/BranchVisitEnricher.cs:6-82]

BR-2: Branch name lookup is built from the `branches` DataFrame — keyed by branch_id. When multiple as_of dates are in range, the last-seen branch_id wins (dictionary overwrite behavior).
- Confidence: HIGH
- Evidence: [ExternalModules/BranchVisitEnricher.cs:34-43] dictionary keyed by branch_id, later rows overwrite earlier ones

BR-3: Customer name lookup is built from the `customers` DataFrame — keyed by customer id. Same last-write-wins behavior for multi-date ranges.
- Confidence: HIGH
- Evidence: [ExternalModules/BranchVisitEnricher.cs:46-53] dictionary keyed by custId

BR-4: If customers DataFrame is null or empty, an empty output DataFrame is returned (weekend guard).
- Confidence: HIGH
- Evidence: [ExternalModules/BranchVisitEnricher.cs:21-25] explicit null/empty check

BR-5: If branch_visits DataFrame is null or empty, an empty output DataFrame is returned.
- Confidence: HIGH
- Evidence: [ExternalModules/BranchVisitEnricher.cs:27-31] explicit null/empty check

BR-6: When a visit references a branch_id not in the lookup, branch_name defaults to empty string.
- Confidence: HIGH
- Evidence: [ExternalModules/BranchVisitEnricher.cs:62] `GetValueOrDefault(branchId, "")`

BR-7: When a visit references a customer_id not in the lookup, first_name and last_name default to null (from the null-forgiving tuple default).
- Confidence: HIGH
- Evidence: [ExternalModules/BranchVisitEnricher.cs:63] `GetValueOrDefault(customerId, (null!, null!))` — tuple with null values

BR-8: The `addresses` table is sourced by DataSourcing but never consumed by the BranchVisitEnricher. The External module only reads `branch_visits`, `branches`, and `customers` from shared state.
- Confidence: HIGH
- Evidence: [branch_visit_log.json:26-28] addresses sourced; [ExternalModules/BranchVisitEnricher.cs:16-18] only reads branch_visits, branches, customers

BR-9: Branch address columns (address_line1, city, state_province, postal_code, country) are sourced but not used by the External module.
- Confidence: HIGH
- Evidence: [branch_visit_log.json:16] columns sourced; [ExternalModules/BranchVisitEnricher.cs:34-43] only branch_id and branch_name used

BR-10: Output preserves the original visit row ordering from branch_visits.
- Confidence: HIGH
- Evidence: [ExternalModules/BranchVisitEnricher.cs:57-77] iterates branchVisits.Rows in order, appends to outputRows sequentially

## Output Schema

| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| visit_id | branch_visits.visit_id | Direct passthrough | [BranchVisitEnricher.cs:66] |
| customer_id | branch_visits.customer_id | Direct passthrough | [BranchVisitEnricher.cs:67] |
| first_name | customers.first_name | Lookup by customer_id | [BranchVisitEnricher.cs:68] |
| last_name | customers.last_name | Lookup by customer_id | [BranchVisitEnricher.cs:69] |
| branch_id | branch_visits.branch_id | Direct passthrough | [BranchVisitEnricher.cs:70] |
| branch_name | branches.branch_name | Lookup by branch_id | [BranchVisitEnricher.cs:71] |
| visit_timestamp | branch_visits.visit_timestamp | Direct passthrough | [BranchVisitEnricher.cs:72] |
| visit_purpose | branch_visits.visit_purpose | Direct passthrough | [BranchVisitEnricher.cs:73] |
| as_of | branch_visits.as_of | Direct passthrough | [BranchVisitEnricher.cs:74] |

## Non-Deterministic Fields
None identified. All fields are derived deterministically from source data (though branch_name/customer_name lookups have last-write-wins behavior for multi-date ranges, which is data-order-dependent rather than random).

## Write Mode Implications
**Append mode**: Each execution appends to the existing `Output/curated/branch_visit_log/` directory. For multi-day auto-advance runs, each effective date adds new part files. This means the output accumulates over time and duplicate data can result if a date is re-run.

## Edge Cases

- **Weekend guard**: If customers table returns empty (e.g., no snapshot for a weekend), output is empty DataFrame with correct schema — no data written.
- **Empty branch_visits**: If no visits exist for the effective date, output is empty.
- **Missing branch_name**: Visits referencing non-existent branch_ids get empty string for branch_name.
- **Missing customer**: Visits referencing non-existent customer_ids get null first_name and last_name.
- **Multi-date lookup collisions**: When effective range spans multiple dates, branch/customer lookups contain only the last-seen entry per ID. The visit rows themselves span all dates, so a visit from day 1 might get the name from day N's snapshot.
- **Append accumulation**: Re-running for the same date produces duplicate records since Append does not deduplicate.
- **3 part files**: Output split across 3 Parquet files.

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|------------------|
| BR-1: Row-by-row enrichment | [BranchVisitEnricher.cs:6-82] |
| BR-2: Branch lookup last-write-wins | [BranchVisitEnricher.cs:34-43] |
| BR-3: Customer lookup last-write-wins | [BranchVisitEnricher.cs:46-53] |
| BR-4: Weekend guard on customers | [BranchVisitEnricher.cs:21-25] |
| BR-5: Empty visits guard | [BranchVisitEnricher.cs:27-31] |
| BR-6: Missing branch default | [BranchVisitEnricher.cs:62] |
| BR-7: Missing customer default | [BranchVisitEnricher.cs:63] |
| BR-8: addresses unused | [branch_visit_log.json:26-28], [BranchVisitEnricher.cs:16-18] |
| BR-9: Branch address columns unused | [branch_visit_log.json:16], [BranchVisitEnricher.cs:34-43] |
| BR-10: Preserves visit order | [BranchVisitEnricher.cs:57-77] |

## Open Questions

OQ-1: Why is the `addresses` table sourced when the External module never reads it? Possible vestigial data source or planned enrichment that was never implemented.
- Confidence: MEDIUM — clearly sourced but unused

OQ-2: Is the last-write-wins behavior for branch/customer lookups intentional? When effective date spans multiple days, lookups are not date-scoped to the visit's as_of.
- Confidence: MEDIUM — could be an oversight or acceptable given stable reference data
