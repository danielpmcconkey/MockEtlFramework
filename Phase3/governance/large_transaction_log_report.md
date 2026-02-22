# Governance Report: LargeTransactionLog

## Links
- BRD: Phase3/brd/large_transaction_log_brd.md
- FSD: Phase3/fsd/large_transaction_log_fsd.md
- Test Plan: Phase3/tests/large_transaction_log_tests.md
- V2 Module: ExternalModules/LargeTransactionV2Processor.cs
- V2 Config: JobExecutor/Jobs/large_transaction_log_v2.json

## Summary of Changes
- Original approach: DataSourcing (transactions, accounts, customers, addresses) -> External (LargeTransactionProcessor) -> DataFrameWriter to curated.large_transaction_log
- V2 approach: DataSourcing (transactions, accounts, customers, addresses) -> External (LargeTransactionV2Processor) writing to double_secret_curated.large_transaction_log via DscWriterUtil
- Key difference: V2 combines processing and writing. Business logic (filter transactions > $500, two-step enrichment via account-to-customer lookup, then customer-to-name lookup) is identical.

## Anti-Patterns Identified
- **Unused DataSourcing step**: The `addresses` table is sourced but never referenced by the External module.
- **Over-fetching columns**: The accounts DataSourcing sources 9 columns (account_id, customer_id, account_type, account_status, open_date, current_balance, interest_rate, credit_limit, apr) but only uses account_id and customer_id for the lookup.
- **Hardcoded threshold**: The $500 transaction amount threshold is hardcoded in the External module.
- **Default to 0 for missing accounts**: Transactions with an unmatched account_id get customer_id=0 rather than being filtered out, and this 0 then fails the customer name lookup (resulting in empty string defaults for name).

## Comparison Results
- Dates compared: 31 (Oct 1-31, 2024)
- Match percentage: 100%
- Fix iterations required for this specific job: 0

## Confidence Assessment
- Overall confidence: HIGH
- Standard filter-and-enrich pattern. The $500 threshold and two-step lookup chain are clear and unambiguous.
