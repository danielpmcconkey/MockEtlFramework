# FSD: BranchVisitLogV2

## Overview
Replaces the original BranchVisitLog job with a V2 implementation that writes to `double_secret_curated` instead of `curated`. The V2 External module replicates the exact BranchVisitEnricher logic -- joining branch visits with customer names and branch names -- then writes to `double_secret_curated.branch_visit_log` via DscWriterUtil.

## Design Decisions
- **Pattern A (External module replacement)**: The original uses DataSourcing -> External (BranchVisitEnricher) -> DataFrameWriter. The V2 replaces both with a single V2 External module.
- Write mode is Append (matching original), so DscWriterUtil.Write is called with overwrite=false.
- The addresses DataSourcing step is retained in the config (matching the original) even though it is unused.
- The weekend guard on customers (empty customers -> empty output) is preserved identically.
- Missing branch -> empty string for branch_name; missing customer -> (null, null) for first_name/last_name -- preserved identically.
- Customer name lookup uses `?.ToString() ?? ""` for building the dictionary, but unmatched customer returns (null!, null!) -- same asymmetric behavior as original.

## Module Pipeline
| Step | Module Type | Config/Details |
|------|------------|----------------|
| 1 | DataSourcing | schema=datalake, table=branch_visits, columns=[visit_id, customer_id, branch_id, visit_timestamp, visit_purpose], resultName=branch_visits |
| 2 | DataSourcing | schema=datalake, table=branches, columns=[branch_id, branch_name, address_line1, city, state_province, postal_code, country], resultName=branches |
| 3 | DataSourcing | schema=datalake, table=customers, columns=[id, first_name, last_name], resultName=customers |
| 4 | DataSourcing | schema=datalake, table=addresses, columns=[address_id, customer_id, address_line1, city], resultName=addresses (unused but retained) |
| 5 | External | BranchVisitLogV2Processor -- enriches visits with names, writes to dsc |

## V2 External Module: BranchVisitLogV2Processor
- File: ExternalModules/BranchVisitLogV2Processor.cs
- Processing logic: Reads "branch_visits", "branches", "customers" from shared state; builds branch_id -> branch_name and customer_id -> (first_name, last_name) lookups; iterates visits enriching each row; writes to double_secret_curated
- Output columns: visit_id, customer_id, first_name, last_name, branch_id, branch_name, visit_timestamp, visit_purpose, as_of
- Target table: double_secret_curated.branch_visit_log
- Write mode: Append (overwrite=false)

## Traceability
| BRD Requirement | FSD Design Element |
|----------------|-------------------|
| BR-1 | V2 builds branch_id -> branch_name lookup from branches DataFrame |
| BR-2 | V2 builds customer_id -> (first_name, last_name) lookup from customers DataFrame |
| BR-3 | Missing branch returns "" for branch_name via GetValueOrDefault |
| BR-4 | Missing customer returns (null!, null!) for first_name/last_name via GetValueOrDefault |
| BR-5 | addresses DataSourcing retained in config but not read by V2 processor |
| BR-6 | DscWriterUtil.Write called with overwrite=false (Append) |
| BR-7 | Weekend guard: if customers null or empty, returns empty DataFrame |
| BR-8 | If branch_visits null or empty, returns empty DataFrame |
| BR-9 | visit_timestamp passed through as-is |
| BR-10 | Weekend guard ensures weekday-only output |
| BR-11 | All visit rows iterated without filtering |
| BR-12 | Lookup dictionaries built from all rows; last-write-wins for duplicates |
