# Governance Report: CustomerDemographics

## Links
- BRD: Phase3/brd/customer_demographics_brd.md
- FSD: Phase3/fsd/customer_demographics_fsd.md
- Test Plan: Phase3/tests/customer_demographics_tests.md
- V2 Module: ExternalModules/CustomerDemographicsV2Processor.cs
- V2 Config: JobExecutor/Jobs/customer_demographics_v2.json

## Summary of Changes
- Original approach: DataSourcing (customers, phone_numbers, email_addresses, segments) -> External (CustomerDemographicsBuilder) -> DataFrameWriter to curated.customer_demographics
- V2 approach: DataSourcing (customers, phone_numbers, email_addresses, segments) -> External (CustomerDemographicsV2Processor) writing to double_secret_curated.customer_demographics via DscWriterUtil
- Key difference: V2 combines processing and writing. Business logic (compute age, classify age bracket, find primary phone and email) is identical.

## Anti-Patterns Identified
- **Unused DataSourcing step**: The `segments` table is sourced but never referenced by the External module.
- **Over-fetching columns**: The customers DataSourcing sources `prefix`, `sort_name`, and `suffix` columns that are never used by the External module.
- **Non-deterministic contact selection**: Primary phone and email are selected as "first encountered" in the DataFrame iteration order. This depends on database query ordering, which may not be deterministic without an explicit ORDER BY in the DataSourcing query.

## Comparison Results
- Dates compared: 31 (Oct 1-31, 2024)
- Match percentage: 100%
- Fix iterations required for this specific job: 0 (assembly name fix and TRUNCATE-to-DELETE fix applied universally, but no job-specific logic fix needed)

## Confidence Assessment
- Overall confidence: HIGH
- Age computation is well-documented and verified. Age bracket classification uses clear threshold ranges. The primary contact selection, while non-deterministic in theory, produces consistent results across the validation period.
