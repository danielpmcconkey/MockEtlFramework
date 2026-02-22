# Governance Report: MonthlyTransactionTrend

## Links
- BRD: Phase3/brd/monthly_transaction_trend_brd.md
- FSD: Phase3/fsd/monthly_transaction_trend_fsd.md
- Test Plan: Phase3/tests/monthly_transaction_trend_tests.md
- V2 Module: ExternalModules/MonthlyTransactionTrendV2Writer.cs
- V2 Config: JobExecutor/Jobs/monthly_transaction_trend_v2.json

## Summary of Changes
- Original approach: DataSourcing (transactions, branches) -> Transformation (SQL with CTE computing COUNT, SUM, AVG per as_of date) -> DataFrameWriter to curated.monthly_transaction_trend
- V2 approach: DataSourcing (transactions, branches) -> Transformation (same SQL) -> External (MonthlyTransactionTrendV2Writer) writing to double_secret_curated.monthly_transaction_trend via DscWriterUtil
- Key difference: V2 retains the identical Transformation SQL and replaces only the DataFrameWriter with a thin V2 writer module. No business logic changes.

## Anti-Patterns Identified
- **Unused DataSourcing step**: The `branches` table is sourced but never referenced in the Transformation SQL.
- **Misleading job name**: The job is named "MonthlyTransactionTrend" but actually computes per-day statistics (one row per as_of date). The "monthly" aspect only emerges when viewing the accumulated daily rows across a month.
- **Redundant SQL filter**: The SQL contains `WHERE as_of >= '2024-10-01'` but since DataSourcing only loads the single effective date, this filter is always satisfied for dates on or after Oct 1.
- **Unnecessary CTE**: The SQL uses a CTE (`base`) whose outer query simply selects all columns from it without any additional transformation. The CTE adds no value.
- **Dependency**: This job depends on DailyTransactionVolume (SameDay dependency), but it reads directly from datalake.transactions, not from the DailyTransactionVolume output.

## Comparison Results
- Dates compared: 31 (Oct 1-31, 2024)
- Match percentage: 100%
- Fix iterations required for this specific job: 0

## Confidence Assessment
- Overall confidence: HIGH
- Despite the misleading name and unnecessary SQL constructs, the actual logic (GROUP BY as_of with COUNT, SUM, AVG) is straightforward.
