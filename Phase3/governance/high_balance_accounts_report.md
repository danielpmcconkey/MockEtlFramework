# Governance Report: HighBalanceAccounts

## Links
- BRD: Phase3/brd/high_balance_accounts_brd.md
- FSD: Phase3/fsd/high_balance_accounts_fsd.md
- Test Plan: Phase3/tests/high_balance_accounts_tests.md
- V2 Module: ExternalModules/HighBalanceV2Processor.cs
- V2 Config: JobExecutor/Jobs/high_balance_accounts_v2.json

## Summary of Changes
- Original approach: DataSourcing (accounts, customers) -> External (HighBalanceFilter) -> DataFrameWriter to curated.high_balance_accounts
- V2 approach: DataSourcing (accounts, customers) -> External (HighBalanceV2Processor) writing to double_secret_curated.high_balance_accounts via DscWriterUtil
- Key difference: V2 combines processing and writing. Business logic (filter accounts with balance > $10,000, enrich with customer names) is identical.

## Anti-Patterns Identified
- **Hardcoded threshold**: The $10,000 balance threshold is hardcoded in the External module rather than being configurable via the job JSON. Any change to the threshold requires a code change and recompilation.

## Comparison Results
- Dates compared: 31 (Oct 1-31, 2024)
- Match percentage: 100%
- Fix iterations required for this specific job: 0 (assembly name fix and TRUNCATE-to-DELETE fix applied universally, but no job-specific logic fix needed)

## Confidence Assessment
- Overall confidence: HIGH
- Simple filter-and-enrich pattern. The threshold condition (`balance > 10000`) is unambiguous.
