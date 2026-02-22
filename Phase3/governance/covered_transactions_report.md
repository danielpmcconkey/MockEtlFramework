# Governance Report: CoveredTransactions

## Links
- BRD: Phase3/brd/covered_transactions_brd.md
- FSD: Phase3/fsd/covered_transactions_fsd.md
- Test Plan: Phase3/tests/covered_transactions_tests.md
- V2 Module: ExternalModules/CoveredTransactionsV2Processor.cs
- V2 Config: JobExecutor/Jobs/covered_transactions_v2.json

## Summary of Changes
- Original approach: External-only pipeline (CoveredTransactionProcessor) with direct database queries, no DataSourcing or DataFrameWriter -> writes to curated.covered_transactions
- V2 approach: External-only pipeline (CoveredTransactionsV2Processor) with identical direct database queries -> writes to double_secret_curated.covered_transactions via DscWriterUtil
- Key difference: V2 replicates the exact same direct-query approach (bypassing DataSourcing entirely) with identical SQL queries for transactions, accounts, customers, addresses, and segments. The only addition is DscWriterUtil.Write() at the end.

## Anti-Patterns Identified
- **Framework bypass**: The original completely bypasses DataSourcing and DataFrameWriter, performing its own database connections and queries. While this provides flexibility (e.g., snapshot fallback queries, complex multi-table joins with different date filtering strategies), it also bypasses the framework's data lineage tracking and standardized error handling.
- **Complex multi-table join logic**: The job performs 5 separate database queries with different filtering strategies (exact date, snapshot fallback, active-address filtering), then joins results in memory. This is the most complex job in the portfolio and the hardest to reverse-engineer.

## Comparison Results
- Dates compared: 31 (Oct 1-31, 2024)
- Match percentage: 100%
- Fix iterations required for this specific job: 0 (no job-specific fix needed; the universal assembly name fix applied)

## Confidence Assessment
- Overall confidence: HIGH
- Despite being the most complex job (14 business rules documented in BRD), the V2 replicates the exact same SQL queries and in-memory join logic. The 100% match across 31 dates confirms behavioral equivalence.
- The zero-row sentinel behavior (outputs a null row with record_count=0 when no transactions qualify) was correctly replicated.
