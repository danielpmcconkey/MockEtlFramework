# Governance Report: CustomerAccountSummaryV2

## Links
- BRD: Phase3/brd/customer_account_summary_v2_brd.md
- FSD: Phase3/fsd/customer_account_summary_v2_fsd.md
- Test Plan: Phase3/tests/customer_account_summary_v2_tests.md
- V2 Module: ExternalModules/CustomerAccountSummaryV2V2Processor.cs
- V2 Config: JobExecutor/Jobs/customer_account_summary_v2_v2.json

## Summary of Changes
- Original approach: DataSourcing (customers, accounts, branches) -> External (CustomerAccountSummaryBuilder) -> DataFrameWriter to curated.customer_account_summary_v2
- V2 approach: DataSourcing (customers, accounts, branches) -> External (CustomerAccountSummaryV2V2Processor) writing to double_secret_curated.customer_account_summary_v2 via DscWriterUtil
- Key difference: V2 combines processing and writing. Business logic (per-customer account count, total balance, active balance with LEFT JOIN semantics) is identical. Note the "V2V2" naming: the original job is already called CustomerAccountSummaryV2, so the rewrite is CustomerAccountSummaryV2V2.

## Anti-Patterns Identified
- **Unused DataSourcing step**: The `branches` table is sourced but never referenced by the External module.
- **Confusing naming**: The original job is already named "V2" (CustomerAccountSummaryV2), suggesting it replaced an earlier version. The rewrite adds another "V2" suffix, creating the awkward "V2V2" naming.

## Comparison Results
- Dates compared: 31 (Oct 1-31, 2024)
- Match percentage: 100%
- Fix iterations required for this specific job: 0 (assembly name fix and TRUNCATE-to-DELETE fix applied universally, but no job-specific logic fix needed)

## Confidence Assessment
- Overall confidence: HIGH
- Standard aggregation pattern with clear per-customer grouping. LEFT JOIN semantics (zero defaults for customers with no accounts) are well-documented and verified.
