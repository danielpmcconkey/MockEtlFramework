# Governance Report: CustomerAddressDeltas

## Links
- BRD: Phase3/brd/customer_address_deltas_brd.md
- FSD: Phase3/fsd/customer_address_deltas_fsd.md
- Test Plan: Phase3/tests/customer_address_deltas_tests.md
- V2 Module: ExternalModules/CustomerAddressDeltasV2Processor.cs
- V2 Config: JobExecutor/Jobs/customer_address_deltas_v2.json

## Summary of Changes
- Original approach: External-only pipeline (CustomerAddressDeltaProcessor) with direct database queries for current and previous day address snapshots -> DataFrameWriter to curated.customer_address_deltas
- V2 approach: External (CustomerAddressDeltasV2Processor) with identical direct database queries -> writes to double_secret_curated.customer_address_deltas via DscWriterUtil
- Key difference: V2 replicates the exact same day-over-day comparison logic (NEW and UPDATED change detection) with identical SQL queries. The only addition is DscWriterUtil.Write().

## Anti-Patterns Identified
- **Framework bypass**: Like CoveredTransactions, this job performs its own direct database queries, bypassing DataSourcing. This is necessary because it needs to fetch data for two different dates (current and previous), which DataSourcing cannot express.
- **Complex change detection**: The job compares 7 address fields across two date snapshots to detect changes. Field comparison is done individually rather than using a hash or composite comparison, making the logic verbose but explicit.

## Comparison Results
- Dates compared: 31 (Oct 1-31, 2024)
- Match percentage: 100%
- Fix iterations required for this specific job: 0

## Confidence Assessment
- Overall confidence: HIGH
- The day-over-day comparison logic is complex but well-documented with 9 business rules. The 100% match across 31 dates (including the first date where previous-day data may not exist) confirms correctness.
