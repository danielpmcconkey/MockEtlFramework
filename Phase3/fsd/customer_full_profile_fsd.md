# FSD: CustomerFullProfileV2

## Overview
Replaces the original CustomerFullProfile job with a V2 implementation that writes to the `double_secret_curated` schema instead of `curated`. The V2 External module replicates the exact same business logic as the original FullProfileAssembler and then writes directly to `double_secret_curated.customer_full_profile` via DscWriterUtil.

## Design Decisions
- **Pattern A (External module replacement)**: The original job uses DataSourcing x5 -> External (FullProfileAssembler) -> DataFrameWriter. The V2 replaces both the External and DataFrameWriter steps with a single V2 External module that combines the processing logic and writing.
- The V2 module replicates the original's age computation, age bracket classification, primary phone/email lookup, and segment name resolution (comma-separated list from customers_segments + segments join).
- Write mode is Overwrite (matching original), so DscWriterUtil.Write is called with overwrite=true.
- The segment_code column is sourced from segments but not used in output (matching original behavior).

## Module Pipeline
| Step | Module Type | Config/Details |
|------|------------|----------------|
| 1 | DataSourcing | schema=datalake, table=customers, columns=[id, first_name, last_name, birthdate], resultName=customers |
| 2 | DataSourcing | schema=datalake, table=phone_numbers, columns=[phone_id, customer_id, phone_type, phone_number], resultName=phone_numbers |
| 3 | DataSourcing | schema=datalake, table=email_addresses, columns=[email_id, customer_id, email_address, email_type], resultName=email_addresses |
| 4 | DataSourcing | schema=datalake, table=customers_segments, columns=[customer_id, segment_id], resultName=customers_segments |
| 5 | DataSourcing | schema=datalake, table=segments, columns=[segment_id, segment_name, segment_code], resultName=segments |
| 6 | External | CustomerFullProfileV2Processor -- replicates original logic + writes to dsc |

## V2 External Module: CustomerFullProfileV2Processor
- File: ExternalModules/CustomerFullProfileV2Processor.cs
- Processing logic: Reads customers, phone_numbers, email_addresses, customers_segments, segments from shared state. Builds phone/email/segment lookups. Iterates customers, computes age/age_bracket, looks up phone/email/segments, produces comma-separated segment names. Writes to double_secret_curated via DscWriterUtil with overwrite=true.
- Output columns: customer_id, first_name, last_name, age, age_bracket, primary_phone, primary_email, segments, as_of
- Target table: double_secret_curated.customer_full_profile
- Write mode: Overwrite (overwrite=true)

## Traceability
| BRD Requirement | FSD Design Element |
|----------------|-------------------|
| BR-1 | V2 processor iterates all customer rows, producing one output row per customer |
| BR-2 | V2 processor computes age = asOfDate.Year - birthdate.Year with birthday adjustment |
| BR-3 | V2 processor uses same age bracket switch expression |
| BR-4 | V2 processor builds phoneByCustomer dictionary, taking first phone per customer |
| BR-5 | V2 processor builds emailByCustomer dictionary, taking first email per customer |
| BR-6 | V2 processor joins customers_segments to segments, producing comma-separated segment names |
| BR-7 | V2 processor defaults to empty list for customers with no segments, resulting in empty string |
| BR-8 | V2 processor filters unmatched segment_ids with Where clause |
| BR-9 | V2 processor uses string.Join(",", ...) with no space after comma |
| BR-10 | V2 processor has guard clause returning empty DataFrame when customers is null/empty |
| BR-11 | DscWriterUtil.Write called with overwrite=true (Overwrite) |
| BR-12 | segment_code sourced but not used in output (matching original) |
| BR-13 | V2 processor uses dictionary assignment for segmentNames, last value wins on duplicate segment_id |
| BR-14 | V2 processor null-coalesces names to empty string, defaults phone/email to empty string |
