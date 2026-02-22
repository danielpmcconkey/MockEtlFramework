# FSD: CustomerAddressDeltasV2

## Overview
CustomerAddressDeltasV2 replicates the exact day-over-day address change detection logic of CustomerAddressDeltas. It compares current and previous day address snapshots to identify NEW and UPDATED addresses, enriched with customer names. The V2 uses DscWriterUtil to write to `double_secret_curated.customer_address_deltas`.

## Design Decisions
- **Pattern A (External-only pipeline)**: The original is an External-only pipeline with direct database queries. The V2 replicates this exactly with DscWriterUtil.Write() added.
- **Write mode**: Append (overwrite=false) to match original.
- **No DataSourcing steps**: Like the original, the V2 manages its own database queries.

## Module Pipeline
| Step | Module Type | Config/Details |
|------|------------|----------------|
| 1 | External | CustomerAddressDeltasV2Processor - replicates all original logic + writes to dsc |

## V2 External Module: CustomerAddressDeltasV2Processor
- File: ExternalModules/CustomerAddressDeltasV2Processor.cs
- Processing logic: Fetches addresses for current and previous date, compares by address_id, detects NEW/UPDATED changes using normalized field comparison, enriches with customer names via snapshot fallback
- Output columns: change_type, address_id, customer_id, customer_name, address_line1, city, state_province, postal_code, country, start_date, end_date, as_of, record_count
- Target table: double_secret_curated.customer_address_deltas
- Write mode: Append (overwrite=false)

## Traceability
| BRD Requirement | FSD Design Element |
|----------------|-------------------|
| BR-1 (Compare current vs previous day) | FetchAddresses for currentDate and previousDate (currentDate-1) |
| BR-2 (NEW classification) | Address_id in current but not previous |
| BR-3 (UPDATED classification) | HasFieldChanged comparison |
| BR-4 (8 compare fields) | CompareFields array |
| BR-5 (Normalized comparison) | Normalize() method: null->"", dates->"yyyy-MM-dd", strings trimmed |
| BR-6 (No DELETED detection) | Loop iterates only currentByAddressId |
| BR-7 (Baseline sentinel) | When previousAddresses.Count == 0, emit null sentinel row |
| BR-8 (record_count) | Set on every row after delta detection |
| BR-9 (No-delta sentinel) | When deltaRows.Count == 0, emit null sentinel |
| BR-10 (Customer names snapshot fallback) | DISTINCT ON (id) WHERE as_of <= @date |
| BR-11 (Ordered by address_id) | OrderBy(kv => kv.Key) |
| BR-12 (Append mode) | DscWriterUtil.Write with overwrite=false |
| BR-13 (Country trimmed, dates formatted) | country?.ToString()?.Trim(), FormatDate() |
| BR-14 (External-only) | No DataSourcing in job config |
| BR-15 (Missing customer name defaults to "") | GetValueOrDefault(customerId, "") |
