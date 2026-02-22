# Governance Report: AccountCustomerJoin

## Links
- BRD: Phase3/brd/account_customer_join_brd.md
- FSD: Phase3/fsd/account_customer_join_fsd.md
- Test Plan: Phase3/tests/account_customer_join_tests.md
- V2 Module: ExternalModules/AccountCustomerJoinV2Processor.cs
- V2 Config: JobExecutor/Jobs/account_customer_join_v2.json

## Summary of Changes
- Original approach: DataSourcing (accounts, customers, addresses) -> External (AccountCustomerDenormalizer) -> DataFrameWriter to curated.account_customer_join
- V2 approach: DataSourcing (accounts, customers, addresses) -> External (AccountCustomerJoinV2Processor) writing to double_secret_curated.account_customer_join via DscWriterUtil
- Key difference: V2 External module combines processing and writing. Business logic (join accounts to customers by customer_id, left-join semantics with empty string defaults) is identical.

## Anti-Patterns Identified
- **Unused DataSourcing step**: The `addresses` table is sourced but never referenced by the External module. Dead configuration.
- **Implicit left-join via GetValueOrDefault**: The customer lookup uses dictionary GetValueOrDefault with empty string fallback rather than an explicit SQL LEFT JOIN pattern, making the join semantics less obvious to reviewers.

## Comparison Results
- Dates compared: 31 (Oct 1-31, 2024)
- Match percentage: 100%
- Fix iterations required for this specific job: 0 (assembly name fix and TRUNCATE-to-DELETE fix applied universally, but no job-specific logic fix needed)

## Confidence Assessment
- Overall confidence: HIGH
- Standard denormalization pattern with clear join logic. No ambiguity in business rules.
