# Governance Report: CustomerFullProfile

## Links
- BRD: Phase3/brd/customer_full_profile_brd.md
- FSD: Phase3/fsd/customer_full_profile_fsd.md
- Test Plan: Phase3/tests/customer_full_profile_tests.md
- V2 Module: ExternalModules/CustomerFullProfileV2Processor.cs
- V2 Config: JobExecutor/Jobs/customer_full_profile_v2.json

## Summary of Changes
- Original approach: DataSourcing (customers, phone_numbers, email_addresses, customers_segments, segments) -> External (FullProfileAssembler) -> DataFrameWriter to curated.customer_full_profile
- V2 approach: DataSourcing (customers, phone_numbers, email_addresses, customers_segments, segments) -> External (CustomerFullProfileV2Processor) writing to double_secret_curated.customer_full_profile via DscWriterUtil
- Key difference: V2 combines processing and writing. Business logic (demographics + contacts + comma-separated segment names) is identical.

## Anti-Patterns Identified
- **Over-fetching columns**: The segments DataSourcing sources `segment_code` which is never used in the output (only `segment_name` is used to build the comma-separated segment list).
- **Duplicated logic with CustomerDemographics**: Age computation and age bracket classification are implemented identically in both CustomerDemographics and CustomerFullProfile External modules, violating DRY. A shared utility would be more maintainable.
- **Non-deterministic contact selection**: Same as CustomerDemographics -- primary phone and email selection depends on DataFrame iteration order.

## Comparison Results
- Dates compared: 31 (Oct 1-31, 2024)
- Match percentage: 100%
- Fix iterations required for this specific job: 0 (assembly name fix and TRUNCATE-to-DELETE fix applied universally, but no job-specific logic fix needed)

## Confidence Assessment
- Overall confidence: HIGH
- The most feature-rich customer profile job, combining demographics, contacts, and segments. All sub-features are well-documented and verified across 31 dates.
