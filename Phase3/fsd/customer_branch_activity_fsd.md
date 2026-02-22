# FSD: CustomerBranchActivityV2

## Overview
CustomerBranchActivityV2 replicates the exact per-customer visit count logic of CustomerBranchActivity. It counts branch visits per customer, enriches with customer names, and writes to `double_secret_curated.customer_branch_activity`.

## Design Decisions
- **Pattern A (External module)**: Original uses DataSourcing + External + DataFrameWriter. V2 keeps DataSourcing steps identical and replaces External+DataFrameWriter with a single V2 External.
- **Write mode**: Append (overwrite=false) to match original.
- **Branches DataSourcing retained**: Original sources branches but never uses them. V2 retains this.
- **No explicit ordering**: Original iterates dictionary (insertion order). V2 replicates this behavior exactly.

## Module Pipeline
| Step | Module Type | Config/Details |
|------|------------|----------------|
| 1 | DataSourcing | branch_visits: visit_id, customer_id, branch_id, visit_purpose |
| 2 | DataSourcing | customers: id, first_name, last_name |
| 3 | DataSourcing | branches: branch_id, branch_name |
| 4 | External | CustomerBranchActivityV2Processor |

## V2 External Module: CustomerBranchActivityV2Processor
- File: ExternalModules/CustomerBranchActivityV2Processor.cs
- Processing logic: Counts visits per customer_id, joins with customer names, uses as_of from first visit row
- Output columns: customer_id, first_name, last_name, as_of, visit_count
- Target table: double_secret_curated.customer_branch_activity
- Write mode: Append (overwrite=false)

## Traceability
| BRD Requirement | FSD Design Element |
|----------------|-------------------|
| BR-1 (Visit count per customer) | visitCounts dictionary counting |
| BR-2 (Only customers with visits) | Loop iterates visitCounts only |
| BR-3 (Customer name lookup, null if missing) | customerNames dictionary, null defaults |
| BR-4 (as_of from first visit row) | branchVisits.Rows[0]["as_of"] |
| BR-5 (Append mode) | DscWriterUtil.Write with overwrite=false |
| BR-6 (Empty guard) | Early return if customers or visits empty |
| BR-7 (Dictionary order) | Iterates visitCounts in insertion order |
| BR-8 (Branches unused) | Branches DataSourcing retained |
| BR-9 (Weekend exclusion) | No output on weekends (customers empty) |
| BR-10 (Weekday-only output) | 23 dates, driven by customers availability |
