# PreferenceBySegment -- Business Requirements Document

## Overview
Calculates opt-in rates for each preference type within each customer segment. The External module bypasses the framework's CsvFileWriter, writing directly to disk via StreamWriter with a custom trailer that uses the input row count (inflated) instead of the output row count.

## Output Type
Direct file I/O via External module (StreamWriter). The job config has NO framework writer module -- the External module handles all file output internally.

## Writer Configuration
The External module writes directly, not through the framework's CsvFileWriter:
- **Output path:** `Output/curated/preference_by_segment.csv`
- **Header:** Yes (comma-separated column names)
- **Line ending:** LF (`\n` via StreamWriter default on Linux)
- **Trailer:** `TRAILER|{inputCount}|{dateStr}` -- NOTE: uses input (preference) row count, NOT output row count
- **Encoding:** System default (UTF-8 on Linux)
- **Append mode:** false (overwrites: `new StreamWriter(outputPath, append: false)`)

## Source Tables

| Table | Columns Used | Filters | Evidence |
|-------|-------------|---------|----------|
| datalake.customer_preferences | preference_id, customer_id, preference_type, opted_in | All rows processed; grouped by (segment, preference_type) | [preference_by_segment.json:9-11], [PreferenceBySegmentWriter.cs:53-68] |
| datalake.customers_segments | customer_id, segment_id | JOIN key between customers and segments | [preference_by_segment.json:15-17], [PreferenceBySegmentWriter.cs:43-49] |
| datalake.segments | segment_id, segment_name | Resolves segment_id to segment_name | [preference_by_segment.json:21-23], [PreferenceBySegmentWriter.cs:35-40] |

## Business Rules

BR-1: Opt-in rate is calculated per (segment_name, preference_type) group as: opted_in_count / total_count, rounded to 2 decimal places using Banker's rounding (MidpointRounding.ToEven).
- Confidence: HIGH
- Evidence: [PreferenceBySegmentWriter.cs:85-87] -- `Math.Round((decimal)optedIn / total, 2, MidpointRounding.ToEven)`

BR-2: Customer-to-segment mapping uses the customers_segments junction table. Customers without a segment mapping get segment "Unknown".
- Confidence: HIGH
- Evidence: [PreferenceBySegmentWriter.cs:48] -- `custSegLookup[custId] = segmentLookup.GetValueOrDefault(segId, "Unknown")`

BR-3: The trailer uses the INPUT row count (number of preference rows before grouping), NOT the output row count. This produces an inflated trailer count.
- Confidence: HIGH
- Evidence: [PreferenceBySegmentWriter.cs:29] -- `var inputCount = prefs.Count`; [PreferenceBySegmentWriter.cs:93] -- `TRAILER|{inputCount}|{dateStr}`; Comment at line 28: "W7: Count INPUT rows before any grouping (inflated count for trailer)"

BR-4: Output rows are ordered by segment_name ascending, then preference_type ascending.
- Confidence: HIGH
- Evidence: [PreferenceBySegmentWriter.cs:80] -- `.OrderBy(k => k.Key.segment).ThenBy(k => k.Key.prefType)`

BR-5: The date in the trailer and as_of column is the maxEffectiveDate formatted as "yyyy-MM-dd".
- Confidence: HIGH
- Evidence: [PreferenceBySegmentWriter.cs:32] -- `var dateStr = maxDate.ToString("yyyy-MM-dd")`

BR-6: The External module sets shared state "output" to an empty DataFrame so the framework doesn't error on a missing output key.
- Confidence: HIGH
- Evidence: [PreferenceBySegmentWriter.cs:97] -- `sharedState["output"] = new DataFrame(new List<Row>(), outputColumns)`

BR-7: There is no framework writer module in the job config. The last module is the External module, which handles all file I/O.
- Confidence: HIGH
- Evidence: [preference_by_segment.json:26-30] -- the External module is the last in the modules array; no CsvFileWriter or ParquetFileWriter follows

BR-8: If a customer has a segment_id in customers_segments that doesn't exist in segments, their segment defaults to "Unknown".
- Confidence: HIGH
- Evidence: [PreferenceBySegmentWriter.cs:48] -- `segmentLookup.GetValueOrDefault(segId, "Unknown")`

BR-9: When a customer has multiple entries in customers_segments, only the last one wins (dictionary overwrite for custSegLookup).
- Confidence: HIGH
- Evidence: [PreferenceBySegmentWriter.cs:46] -- `custSegLookup[custId] = ...` overwrites

BR-10: If prefs, custSegments, or segments are null/empty, output is empty.
- Confidence: HIGH
- Evidence: [PreferenceBySegmentWriter.cs:22-26]

## Output Schema

| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| segment_name | segments.segment_name (via customers_segments) | Lookup; "Unknown" if segment_id not found | [PreferenceBySegmentWriter.cs:48, 58] |
| preference_type | customer_preferences.preference_type | Direct from source | [PreferenceBySegmentWriter.cs:56] |
| opt_in_rate | Derived | Decimal: opted_in / total, rounded to 2 dp (Banker's rounding) | [PreferenceBySegmentWriter.cs:85-87] |
| as_of | __maxEffectiveDate | Formatted as yyyy-MM-dd string | [PreferenceBySegmentWriter.cs:32, 89] |

## Non-Deterministic Fields
- **segment assignment**: When a customer appears multiple times in customers_segments (different segment_ids), only the last-encountered assignment is used. Row order from database is non-deterministic.
- **{timestamp}**: Not used in this job.

## Write Mode Implications
The External module writes with `append: false` (overwrite). Each execution replaces the entire CSV file. For multi-day auto-advance runs, only the last effective date's output persists on disk.

## Edge Cases

1. **Inflated trailer count**: The trailer row_count reflects the total number of input preference rows (e.g., 11150 per date), not the number of output rows (which is the number of unique segment+preference_type combinations, likely ~40). This is a known quirk documented as "W7".
   - Evidence: [PreferenceBySegmentWriter.cs:28-29, 92-93]

2. **Banker's rounding**: Unlike standard rounding, Banker's rounding rounds 0.5 to the nearest even number. For example, 0.045 rounds to 0.04, not 0.05. This produces subtly different results from standard rounding.
   - Evidence: [PreferenceBySegmentWriter.cs:86] -- `MidpointRounding.ToEven`

3. **Bypasses framework writer**: The External module writes the file directly, bypassing CsvFileWriter's RFC 4180 quoting rules, configurable line endings, and trailer token substitution. The output format is the External module's own implementation.
   - Evidence: [PreferenceBySegmentWriter.cs:76-94]

4. **Customer in preferences but not in customers_segments**: Gets segment "Unknown" (no mapping found in custSegLookup).
   - Evidence: [PreferenceBySegmentWriter.cs:58] -- `custSegLookup.GetValueOrDefault(custId, "Unknown")`

5. **No date partitioning**: All preference rows across the entire effective date range are aggregated together. The as_of in the output is always maxEffectiveDate regardless.
   - Evidence: [PreferenceBySegmentWriter.cs:53-69] -- no date filtering

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|------------------|
| Banker's rounding for opt_in_rate | [PreferenceBySegmentWriter.cs:85-87] |
| Inflated trailer count (input rows) | [PreferenceBySegmentWriter.cs:28-29, 92-93] |
| Direct file I/O (no framework writer) | [preference_by_segment.json:26-30], [PreferenceBySegmentWriter.cs:72-94] |
| Ordered by segment, preference_type | [PreferenceBySegmentWriter.cs:80] |
| Unknown segment fallback | [PreferenceBySegmentWriter.cs:48, 58] |
| Overwrite (append: false) | [PreferenceBySegmentWriter.cs:76] |

## Open Questions

1. **Why bypass CsvFileWriter?** The External module writes CSV directly instead of using the framework's CsvFileWriter. This means RFC 4180 quoting is not applied, and the trailer format differs from the standard framework behavior. Is this intentional?
   - Confidence: MEDIUM -- the External module appears deliberately crafted with its own file I/O

2. **Inflated trailer count**: The trailer intentionally uses input row count instead of output row count. This is documented as "W7" in the code comments, suggesting it is a known production behavior being replicated. What downstream system consumes this trailer?
   - Confidence: HIGH -- clearly intentional per code comments
