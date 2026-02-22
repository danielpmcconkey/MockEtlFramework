# FSD: CustomerAddressHistoryV2

## Overview
CustomerAddressHistoryV2 replicates the exact Transformation-based logic of CustomerAddressHistory, filtering addresses to exclude null customer_id and ordering by customer_id. The V2 keeps the same DataSourcing and Transformation steps, replacing DataFrameWriter with a thin External writer that uses DscWriterUtil.

## Design Decisions
- **Pattern B (Transformation + V2 Writer)**: Original uses DataSourcing + Transformation + DataFrameWriter. V2 keeps DataSourcing and Transformation identical, replaces DataFrameWriter with External writer.
- **Write mode**: Append (overwrite=false) to match original.
- **Same SQL**: The Transformation SQL is kept identical to ensure behavioral equivalence.
- **Branches DataSourcing retained**: Original sources branches but SQL never references them. V2 retains this.

## Module Pipeline
| Step | Module Type | Config/Details |
|------|------------|----------------|
| 1 | DataSourcing | addresses: address_id, customer_id, address_line1, city, state_province, postal_code, country |
| 2 | DataSourcing | branches: branch_id, branch_name |
| 3 | Transformation | SQL filters null customer_id, orders by customer_id. Result: "addr_history" |
| 4 | External | CustomerAddressHistoryV2Writer - reads addr_history, writes to dsc |

## V2 External Module: CustomerAddressHistoryV2Writer
- File: ExternalModules/CustomerAddressHistoryV2Writer.cs
- Processing logic: Reads "addr_history" DataFrame from shared state, writes to dsc
- Output columns: customer_id, address_line1, city, state_province, postal_code, country, as_of
- Target table: double_secret_curated.customer_address_history
- Write mode: Append (overwrite=false)

## Traceability
| BRD Requirement | FSD Design Element |
|----------------|-------------------|
| BR-1 (Non-null customer_id) | SQL WHERE a.customer_id IS NOT NULL |
| BR-2 (Output columns) | SQL SELECT list |
| BR-3 (Ordered by customer_id) | SQL ORDER BY sub.customer_id |
| BR-4 (Append mode) | DscWriterUtil.Write with overwrite=false |
| BR-5 (Branches unused) | Branches DataSourcing retained, SQL doesn't reference |
| BR-6 (Subquery pattern) | SQL uses subquery wrapping (preserved for equivalence) |
| BR-7 (No address_id in output) | SQL doesn't select address_id |
| BR-8 (Transformation pipeline) | Same Transformation SQL as original |
