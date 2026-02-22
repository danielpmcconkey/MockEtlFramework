# CustomerFullProfile -- Governance Report

## Links
- BRD: Phase3/brd/customer_full_profile_brd.md
- FSD: Phase3/fsd/customer_full_profile_fsd.md
- Test Plan: Phase3/tests/customer_full_profile_tests.md
- V2 Config: JobExecutor/Jobs/customer_full_profile_v2.json

## Summary of Changes
The original job used an External module (FullProfileAssembler.cs) that re-derived age, age_bracket, primary_phone, and primary_email using logic identical to CustomerDemographics, plus added segment concatenation. The V2 eliminates this duplication by reading pre-computed demographics from `curated.customer_demographics` (fixing AP-2), then enriching with segment data from datalake using a SQL Transformation with JOINs and GROUP_CONCAT. A SameDay dependency on CustomerDemographics is declared.

## Anti-Pattern Scorecard

| AP Code | Present in Original? | Eliminated in V2? | How? |
|---------|---------------------|--------------------|------|
| AP-1    | N                   | N/A                | No unused data sources in original |
| AP-2    | Y                   | Y                  | Instead of re-deriving age, age_bracket, primary_phone, primary_email from raw datalake tables, V2 reads them from curated.customer_demographics |
| AP-3    | Y                   | Y                  | Replaced External module with SQL Transformation + DataFrameWriter |
| AP-4    | Y                   | Y                  | Removed unused columns: phone_id/phone_type from phone_numbers; email_id/email_type from email_addresses; segment_code from segments. V2 no longer sources phone_numbers or email_addresses at all. |
| AP-5    | N                   | N/A                | NULL handling consistent |
| AP-6    | Y                   | Y                  | Five foreach loops replaced with set-based SQL JOINs and GROUP_CONCAT |
| AP-7    | Y                   | Documented         | Age bracket magic values no longer computed here (delegated to CustomerDemographics). Documented in that job's FSD. |
| AP-8    | N                   | N/A                | No complex SQL in original |
| AP-9    | N                   | N/A                | Name reasonably reflects output |
| AP-10   | Y                   | Y                  | V2 declares SameDay dependency on CustomerDemographics (required for curated.customer_demographics to be populated) |

## Comparison Results
- Dates verified: 31 (Oct 1-31, 2024)
- Match percentage: 100%
- Write mode: Overwrite
- Row count: 223

## Confidence Assessment
**HIGH** -- All 10 business rules directly observable. The AP-2 fix (reading from upstream curated table) is the most significant architectural change, properly leveraging the dependency chain. No fix iterations required for this job.
