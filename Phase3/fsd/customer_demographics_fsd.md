# FSD: CustomerDemographicsV2

## Overview
Replaces the original CustomerDemographics job with a V2 implementation that writes to the `double_secret_curated` schema instead of `curated`. The V2 External module replicates the exact same business logic as the original CustomerDemographicsBuilder and then writes directly to `double_secret_curated.customer_demographics` via DscWriterUtil.

## Design Decisions
- **Pattern A (External module replacement)**: The original job uses DataSourcing x4 -> External (CustomerDemographicsBuilder) -> DataFrameWriter. The V2 replaces both the External and DataFrameWriter steps with a single V2 External module that combines the processing logic and writing.
- The V2 module replicates the original's age computation (year difference with birthday adjustment), age bracket classification, and primary phone/email lookup (first-found per customer).
- Write mode is Overwrite (matching original), so DscWriterUtil.Write is called with overwrite=true.
- The segments DataSourcing step is retained in the config (matching the original) even though it is unused.

## Module Pipeline
| Step | Module Type | Config/Details |
|------|------------|----------------|
| 1 | DataSourcing | schema=datalake, table=customers, columns=[id, prefix, first_name, last_name, sort_name, suffix, birthdate], resultName=customers |
| 2 | DataSourcing | schema=datalake, table=phone_numbers, columns=[phone_id, customer_id, phone_type, phone_number], resultName=phone_numbers |
| 3 | DataSourcing | schema=datalake, table=email_addresses, columns=[email_id, customer_id, email_address, email_type], resultName=email_addresses |
| 4 | DataSourcing | schema=datalake, table=segments, columns=[segment_id, segment_name], resultName=segments (unused but retained) |
| 5 | External | CustomerDemographicsV2Processor -- replicates original logic + writes to dsc |

## V2 External Module: CustomerDemographicsV2Processor
- File: ExternalModules/CustomerDemographicsV2Processor.cs
- Processing logic: Reads customers, phone_numbers, email_addresses from shared state. Builds first-phone and first-email lookups by customer_id. Iterates customers, computes age from birthdate vs as_of date with birthday adjustment, classifies age bracket, looks up primary phone/email. Writes to double_secret_curated via DscWriterUtil with overwrite=true.
- Output columns: customer_id, first_name, last_name, birthdate, age, age_bracket, primary_phone, primary_email, as_of
- Target table: double_secret_curated.customer_demographics
- Write mode: Overwrite (overwrite=true)

## Traceability
| BRD Requirement | FSD Design Element |
|----------------|-------------------|
| BR-1 | V2 processor iterates all customer rows, producing one output row per customer |
| BR-2 | V2 processor computes age = asOfDate.Year - birthdate.Year with birthday adjustment |
| BR-3 | V2 processor uses switch expression with same bracket ranges: <26, <=35, <=45, <=55, <=65, >65 |
| BR-4 | V2 processor builds phoneByCustomer dictionary, taking first phone found per customer |
| BR-5 | V2 processor builds emailByCustomer dictionary, taking first email found per customer |
| BR-6 | V2 processor uses GetValueOrDefault(customerId, "") for phone |
| BR-7 | V2 processor uses GetValueOrDefault(customerId, "") for email |
| BR-8 | V2 processor passes birthdate through unchanged |
| BR-9 | V2 processor has guard clause returning empty DataFrame when customers is null/empty |
| BR-10 | segments DataSourcing retained in config; prefix, sort_name, suffix sourced but unused |
| BR-11 | DscWriterUtil.Write called with overwrite=true (Overwrite) |
| BR-12 | V2 processor null-coalesces first_name and last_name to empty string |
