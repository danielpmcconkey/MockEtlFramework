# Governance Report: AccountBalanceSnapshot

## Links
- BRD: Phase3/brd/account_balance_snapshot_brd.md
- FSD: Phase3/fsd/account_balance_snapshot_fsd.md
- Test Plan: Phase3/tests/account_balance_snapshot_tests.md
- V2 Module: ExternalModules/AccountBalanceSnapshotV2Processor.cs
- V2 Config: JobExecutor/Jobs/account_balance_snapshot_v2.json

## Summary of Changes
- Original approach: DataSourcing (accounts, branches) -> External (AccountSnapshotBuilder) -> DataFrameWriter to curated.account_balance_snapshot
- V2 approach: DataSourcing (accounts, branches) -> External (AccountBalanceSnapshotV2Processor) writing to double_secret_curated.account_balance_snapshot via DscWriterUtil
- Key difference: V2 External module combines processing and writing into a single step, bypassing DataFrameWriter. Business logic (select 6 columns from accounts, pass through as-is) is identical.

## Anti-Patterns Identified
- **Unused DataSourcing step**: The `branches` table is sourced via DataSourcing but never referenced by the External module. This wastes a database query and memory.
- **Over-fetching columns**: Sources 8 account columns (including open_date, interest_rate, credit_limit) but only uses 5 plus as_of in output.

## Comparison Results
- Dates compared: 31 (Oct 1-31, 2024)
- Match percentage: 100%
- Fix iterations required for this specific job: 0 (no job-specific fix needed; only the universal assembly name fix in Iteration 1 applied)

## Confidence Assessment
- Overall confidence: HIGH
- Simple pass-through logic with no transformations, filtering, or joins. Straightforward to verify.
