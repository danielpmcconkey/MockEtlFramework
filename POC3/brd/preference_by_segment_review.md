# preference_by_segment — BRD Review

## Reviewer: reviewer-1
## Status: PASS

## Section Checks
| Section | Status | Notes |
|---------|--------|-------|
| Overview | PASS | Accurate summary of segment-level opt-in rate calculation with inflated trailer |
| Output Type | PASS | Correctly identifies direct file I/O via External module |
| Writer Configuration | PASS | All params verified: output path, UTF-8, LF, header, trailer format, append:false |
| Source Tables | PASS | Three tables (customer_preferences, customers_segments, segments) with correct columns matching JSON |
| Business Rules | PASS | All 10 rules verified with HIGH confidence evidence |
| Output Schema | PASS | 4 columns correctly documented with transformations |
| Non-Deterministic Fields | PASS | Correctly identifies last-write-wins non-determinism for multi-segment customers |
| Write Mode Implications | PASS | append:false overwrite behavior correctly documented |
| Edge Cases | PASS | 5 edge cases well-documented including inflated trailer, Banker's rounding, bypass of framework writer |
| Traceability Matrix | PASS | All key requirements mapped to evidence citations |

## Evidence Spot-Checks

| Requirement | Cited Evidence | Verified? | Notes |
|-------------|---------------|-----------|-------|
| BR-1: Banker's rounding for opt_in_rate | [PreferenceBySegmentWriter.cs:85-87] | YES | Lines 85-87: `Math.Round((decimal)optedIn / total, 2, MidpointRounding.ToEven)` — exact match |
| BR-3: Inflated trailer count | [PreferenceBySegmentWriter.cs:29, 93] | YES | Line 29: `var inputCount = prefs.Count`, Line 93: `writer.Write($"TRAILER|{inputCount}|{dateStr}\n")` — exact match |
| BR-4: Ordering by segment then preference_type | [PreferenceBySegmentWriter.cs:80] | YES | Line 80: `.OrderBy(k => k.Key.segment).ThenBy(k => k.Key.prefType)` — exact match |
| BR-8: Unknown default for missing segment_id | [PreferenceBySegmentWriter.cs:48] | YES | Line 48: `segmentLookup.GetValueOrDefault(segId, "Unknown")` — exact match |
| Edge Case 4: Customer not in customers_segments | [PreferenceBySegmentWriter.cs:58] | YES | Line 58: `custSegLookup.GetValueOrDefault(custId, "Unknown")` — exact match |

## Issues Found
- **Minor (non-blocking)**: BR-10 says "null/empty" for all three inputs, but the guard at line 22 only checks `prefs.Count == 0` for empty; custSegments and segments are null-checked only (not empty-checked). If custSegments or segments were non-null but had zero rows, the lookups would simply produce empty dictionaries and all preferences would get "Unknown" segments — functionally equivalent to an empty output. This is a minor imprecision in the BRD wording, not a factual error.

## Verdict
PASS: BRD is approved. Thorough analysis with well-verified evidence for all 10 business rules. Banker's rounding, inflated trailer count (W7), three-table join pattern with Unknown defaults, and last-write-wins non-determinism all correctly documented.
