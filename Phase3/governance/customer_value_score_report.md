# Governance Report: CustomerValueScore

## Links
- BRD: Phase3/brd/customer_value_score_brd.md
- FSD: Phase3/fsd/customer_value_score_fsd.md
- Test Plan: Phase3/tests/customer_value_score_tests.md
- V2 Module: ExternalModules/CustomerValueScoreV2Processor.cs
- V2 Config: JobExecutor/Jobs/customer_value_score_v2.json

## Summary of Changes
- Original approach: DataSourcing (customers, transactions, accounts, branch_visits) -> External (CustomerValueCalculator) -> DataFrameWriter to curated.customer_value_score
- V2 approach: DataSourcing (customers, transactions, accounts, branch_visits) -> External (CustomerValueScoreV2Processor) writing to double_secret_curated.customer_value_score via DscWriterUtil
- Key difference: V2 combines processing and writing. Business logic (compute transaction score, balance score, visit score with caps at 1000, then weighted composite) is identical.

## Anti-Patterns Identified
- **Magic numbers**: The scoring formula uses hardcoded constants (10.0 multiplier for transaction score, 0.1 multiplier for balance score, 50.0 multiplier for visit score, cap of 1000, weights of 0.4/0.35/0.25) without named constants or configuration. These business rules are embedded deep in the code.
- **Multi-source dependency**: The job depends on 4 source tables and performs intermediate lookups (account_id -> customer_id for transaction attribution). Any data quality issue in any source affects the output.

## Comparison Results
- Dates compared: 31 (Oct 1-31, 2024)
- Match percentage: 100%
- Fix iterations required for this specific job: 0 (assembly name fix and TRUNCATE-to-DELETE fix applied universally, but no job-specific logic fix needed)

## Confidence Assessment
- Overall confidence: HIGH
- The scoring formula is deterministic and well-documented in the BRD with 8 business rules covering each component. All sub-score computations and the final weighted sum produce identical results across 31 dates.
