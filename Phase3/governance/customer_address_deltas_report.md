# CustomerAddressDeltas -- Governance Report

## Links
- BRD: Phase3/brd/customer_address_deltas_brd.md
- FSD: Phase3/fsd/customer_address_deltas_fsd.md
- Test Plan: Phase3/tests/customer_address_deltas_tests.md
- V2 Config: JobExecutor/Jobs/customer_address_deltas_v2.json

## Summary of Changes
The original job used an External module (CustomerAddressDeltaProcessor.cs) performing two-date comparison with snapshot fallback for customer names. The V2 retains the External module (genuinely justified for comparing two date snapshots with snapshot fallback logic that cannot be expressed in the framework's single SQL Transformation model) but adds comments documenting the -1 day offset for day-over-day comparison.

## Anti-Pattern Scorecard

| AP Code | Present in Original? | Eliminated in V2? | How? |
|---------|---------------------|--------------------|------|
| AP-1    | N                   | N/A                | No DataSourcing modules (External does its own queries) |
| AP-2    | N                   | N/A                | No duplicated logic |
| AP-3    | N                   | N/A                | External module genuinely justified for two-date comparison + snapshot fallback |
| AP-4    | N                   | N/A                | No DataSourcing modules to trim |
| AP-5    | N                   | N/A                | NULL handling is consistent (customer_name defaults to empty string on miss) |
| AP-6    | N                   | N/A                | Row iteration is necessary for field-by-field comparison |
| AP-7    | Y                   | Y                  | Added comment explaining the -1 day offset for day-over-day comparison |
| AP-8    | N                   | N/A                | No overly complex SQL |
| AP-9    | N                   | N/A                | Name accurately describes delta detection |
| AP-10   | N                   | N/A                | All sources are datalake tables; no inter-job dependencies |

## Comparison Results
- Dates verified: 31 (Oct 1-31, 2024)
- Match percentage: 100%
- Write mode: Append
- Row count per date: 1 (baseline null-row on Oct 1, then varies)

## Confidence Assessment
**HIGH** -- Complex but well-understood delta detection logic. All 13 business rules documented with HIGH confidence. One of only 2 External modules retained as genuinely justified.
