# Governance Report: ExecutiveDashboard

## Links
- BRD: Phase3/brd/executive_dashboard_brd.md
- FSD: Phase3/fsd/executive_dashboard_fsd.md
- Test Plan: Phase3/tests/executive_dashboard_tests.md
- V2 Module: ExternalModules/ExecutiveDashboardV2Processor.cs
- V2 Config: JobExecutor/Jobs/executive_dashboard_v2.json

## Summary of Changes
- Original approach: DataSourcing (transactions, accounts, customers, loan_accounts, branch_visits, branches, segments) -> External (ExecutiveDashboardBuilder) -> DataFrameWriter to curated.executive_dashboard
- V2 approach: DataSourcing (transactions, accounts, customers, loan_accounts, branch_visits, branches, segments) -> External (ExecutiveDashboardV2Processor) writing to double_secret_curated.executive_dashboard via DscWriterUtil
- Key difference: V2 combines processing and writing. Business logic (compute 9 KPI metrics from 5 source tables, output as metric name/value pairs) is identical.

## Anti-Patterns Identified
- **Two unused DataSourcing steps**: Both `branches` and `segments` tables are sourced but never referenced by the External module. These waste two database queries.
- **Metric value as decimal**: All 9 metrics (including counts like total_customers) are cast to decimal for uniform output. This means integer counts like 223 are stored as 223.00, which may be surprising to consumers.
- **Highest fan-in job**: This job reads from 7 DataSourcing tables (5 used, 2 unused), making it the job with the most source dependencies. Any schema change to any of these tables could affect this job.

## Comparison Results
- Dates compared: 31 (Oct 1-31, 2024)
- Match percentage: 100%
- Fix iterations required for this specific job: 0 (assembly name fix and TRUNCATE-to-DELETE fix applied universally, but no job-specific logic fix needed)

## Confidence Assessment
- Overall confidence: HIGH
- The 9 metrics are all simple COUNT, SUM, or AVG operations. Output is small (9 rows per date) and deterministic. All metrics verified across 31 dates.
