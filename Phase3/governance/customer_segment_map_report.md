# Governance Report: CustomerSegmentMap

## Links
- BRD: Phase3/brd/customer_segment_map_brd.md
- FSD: Phase3/fsd/customer_segment_map_fsd.md
- Test Plan: Phase3/tests/customer_segment_map_tests.md
- V2 Module: ExternalModules/CustomerSegmentMapV2Writer.cs
- V2 Config: JobExecutor/Jobs/customer_segment_map_v2.json

## Summary of Changes
- Original approach: DataSourcing (customers_segments, segments, branches) -> Transformation (SQL INNER JOIN on segment_id and as_of) -> DataFrameWriter to curated.customer_segment_map
- V2 approach: DataSourcing (customers_segments, segments, branches) -> Transformation (same SQL) -> External (CustomerSegmentMapV2Writer) writing to double_secret_curated.customer_segment_map via DscWriterUtil
- Key difference: V2 retains the identical Transformation SQL and replaces only the DataFrameWriter with a thin V2 writer module. No business logic changes.

## Anti-Patterns Identified
- **Unused DataSourcing step**: The `branches` table is sourced but never referenced in the Transformation SQL.

## Comparison Results
- Dates compared: 31 (Oct 1-31, 2024)
- Match percentage: 100%
- Fix iterations required for this specific job: 0

## Confidence Assessment
- Overall confidence: HIGH
- Simple SQL INNER JOIN transformation. No ambiguity in the segment mapping logic.
