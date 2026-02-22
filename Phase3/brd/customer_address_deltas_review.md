# CustomerAddressDeltas — BRD Review

## Review Status: PASS

## Evidence Verification
- [x] All citations checked (13 business rules, all line references verified)
- [x] All citations accurate (2 minor off-by-one line numbers noted below, not blocking)

Detailed verification:
- BR-1 [line 26, lines 31-32]: Confirmed AddDays(-1) and both FetchAddresses calls — exact match
- BR-2 [lines 80-82]: Confirmed NEW detection via TryGetValue miss — exact match
- BR-3 [lines 83-86]: Confirmed UPDATED detection via HasFieldChanged — exact match
- BR-4 [lines 11-15]: Confirmed CompareFields array with 8 fields — correct (line 15 is blank but range is acceptable)
- BR-5 [lines 213-218, line 207]: Confirmed Normalize method and Ordinal comparison — exact match
- BR-6 [lines 175-181]: Confirmed snapshot fallback SQL (DISTINCT ON with as_of <= @date DESC) — exact match
- BR-6 [line 193]: **Minor** — concatenation `names[id] = $"{firstName} {lastName}"` is on line 194, not 193. Line 193 is the lastName extraction.
- BR-7 [lines 36-56]: Confirmed baseline null row — exact match. Database confirms Oct 1 has 1 null row with record_count=0.
- BR-8 [lines 112, 136-140]: Confirmed recordCount assignment — exact match
- BR-9 [lines 114-133]: Confirmed zero-delta null row — exact match
- BR-10 [line 76]: Confirmed OrderBy(kv => kv.Key) — exact match
- BR-11 [lines 104-106]: Confirmed country Trim and FormatDate calls — exact match
- BR-12 [job config line 13]: **Minor** — `"writeMode": "Append"` is on line 14, not 13. Line 13 is `"targetTable": "customer_address_deltas",`.
- BR-13 [lines 76-89]: Confirmed only currentByAddressId is iterated — exact match

Database spot-checks:
- Oct 1 baseline: 1 row, all NULLs except as_of, record_count=0
- Oct 2: 2 delta rows, record_count=2 (matches actual_rows)
- Only "NEW" and "UPDATED" change_types in output (no deletions)
- Data exists every day including weekends

## Anti-Pattern Assessment
- [x] AP identification is plausible and complete

Identified patterns:
- **AP-3**: Correctly assessed as borderline justified. Multi-query access to two different date snapshots plus snapshot fallback for customer names genuinely requires procedural code — DataSourcing cannot fetch data from a different effective date.
- **AP-7**: Correctly identified. The `-1` day offset is implicit business logic worth documenting.

Minor observation on **AP-10**: The BRD lists AP-10 as an identified anti-pattern but then concludes "No dependencies needed." If no dependencies are needed, AP-10 does not apply — it should be omitted from the Anti-Patterns section rather than listed and dismissed. Not blocking since the conclusion is correct.

Remaining APs correctly omitted:
- AP-1: N/A — no DataSourcing modules in job config
- AP-4: N/A — no DataSourcing modules
- AP-2: N/A — no curated table dependencies
- AP-5: N/A — the country trimming inconsistency (noted in Open Questions) is a code quality issue but not AP-5 (which is about NULL/default asymmetry)
- AP-6: N/A — the row-by-row iteration is inherent to the justified External module
- AP-8: N/A — no SQL Transformations
- AP-9: N/A — name accurately describes the output (address deltas)

## Completeness Check
- [x] All required sections present (Overview, Source Tables, Business Rules, Output Schema, Edge Cases, Anti-Patterns Identified, Traceability Matrix, Open Questions)
- [x] Traceability matrix complete — all 13 BRs mapped to evidence
- [x] Output schema documents all 13 columns with source and transformation

## Issues Found
None blocking.

Minor observations (not blocking):
1. BR-6 line citation: concatenation is on line 194, not 193
2. BR-12 line citation: writeMode is on line 14, not 13
3. AP-10 should be omitted rather than listed and dismissed

## Verdict
PASS: BRD approved for Phase B.

Excellent BRD for a complex change-detection job. All 13 business rules thoroughly documented with accurate evidence. The open questions about deletion detection and country trimming asymmetry are good catches that should inform V2 design. The AP-3 justification for keeping the External module is sound.
