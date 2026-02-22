# Governance Report: CustomerTransactionActivity

## Links
- BRD: Phase3/brd/customer_transaction_activity_brd.md
- FSD: Phase3/fsd/customer_transaction_activity_fsd.md
- Test Plan: Phase3/tests/customer_transaction_activity_tests.md
- V2 Module: ExternalModules/CustomerTransactionActivityV2Processor.cs
- V2 Config: JobExecutor/Jobs/customer_transaction_activity_v2.json

## Summary of Changes
- Original approach: DataSourcing (transactions, accounts) -> External (CustomerTxnActivityBuilder) -> DataFrameWriter to curated.customer_transaction_activity
- V2 approach: DataSourcing (transactions, accounts) -> External (CustomerTransactionActivityV2Processor) writing to double_secret_curated.customer_transaction_activity via DscWriterUtil
- Key difference: V2 combines processing and writing. Business logic (two-step lookup: account_id -> customer_id, then aggregate transaction counts and amounts per customer) is identical.

## Anti-Patterns Identified
- **Silent data loss on missing accounts**: Transactions with account_id not found in the accounts table are silently skipped (customer_id defaults to 0, then the `if (customerId == 0) continue` guard drops the row). No logging or error tracking for unmatched transactions.

## Comparison Results
- Dates compared: 31 (Oct 1-31, 2024)
- Match percentage: 100%
- Fix iterations required for this specific job: 0

## Confidence Assessment
- Overall confidence: HIGH
- Standard two-step lookup and aggregation pattern. The debit/credit classification is clear (based on txn_type string comparison). No numeric precision issues since amounts are summed as decimals without rounding.
