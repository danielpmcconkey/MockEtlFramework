# BondMaturitySchedule — Business Requirements Document

## Overview
Produces a summary of bond-type securities held by customers, aggregating the total held value and number of holders for each bond. Despite its name suggesting maturity dates, the job actually computes bond holding aggregates using an External module that filters securities to bonds and joins with holdings data.

## Output Type
ParquetFileWriter

## Writer Configuration
- **source**: `output`
- **outputDirectory**: `Output/curated/bond_maturity_schedule/`
- **numParts**: 1
- **writeMode**: Overwrite

Evidence: [bond_maturity_schedule.json:24-30]

## Source Tables

| Table | Columns Used | Filters | Evidence |
|-------|-------------|---------|----------|
| datalake.securities | security_id, ticker, security_name, security_type, sector, exchange | Filtered to `security_type = 'Bond'` in External module | [bond_maturity_schedule.json:6-11], [BondMaturityScheduleBuilder.cs:29-31] |
| datalake.holdings | holding_id, investment_id, security_id, customer_id, quantity, cost_basis, current_value | Filtered to bond security_ids only via lookup in External module | [bond_maturity_schedule.json:13-18], [BondMaturityScheduleBuilder.cs:56-58] |

### Table Schemas (from database)

**securities**: security_id (integer), ticker (varchar), security_name (varchar), security_type (varchar), sector (varchar), exchange (varchar), as_of (date)

**holdings**: holding_id (integer), investment_id (integer), security_id (integer), customer_id (integer), quantity (numeric), cost_basis (numeric), current_value (numeric), as_of (date)

## Business Rules

BR-1: Only securities with `security_type = 'Bond'` are included in the output.
- Confidence: HIGH
- Evidence: [BondMaturityScheduleBuilder.cs:29] — `.Where(r => r["security_type"]?.ToString() == "Bond")`

BR-2: If no securities data exists (null or empty), an empty DataFrame with the output schema is returned.
- Confidence: HIGH
- Evidence: [BondMaturityScheduleBuilder.cs:19-23]

BR-3: If no bonds exist after filtering, an empty DataFrame with the output schema is returned.
- Confidence: HIGH
- Evidence: [BondMaturityScheduleBuilder.cs:33-37]

BR-4: Holdings are aggregated per bond (security_id): `total_held_value` is the SUM of `current_value`, and `holder_count` is the COUNT of holding rows per security_id.
- Confidence: HIGH
- Evidence: [BondMaturityScheduleBuilder.cs:52-67] — Row-by-row accumulation in `bondTotals` dictionary

BR-5: `total_held_value` is rounded to 2 decimal places using default rounding (MidpointRounding.ToEven is the C# default for `Math.Round`).
- Confidence: HIGH
- Evidence: [BondMaturityScheduleBuilder.cs:85] — `Math.Round(totals.totalValue, 2)`

BR-6: Bonds with no matching holdings get `total_held_value = 0` and `holder_count = 0`.
- Confidence: HIGH
- Evidence: [BondMaturityScheduleBuilder.cs:75-77] — default tuple `(totalValue: 0m, holderCount: 0)`

BR-7: The `as_of` column in the output is set to `__maxEffectiveDate` from shared state, not from the source data.
- Confidence: HIGH
- Evidence: [BondMaturityScheduleBuilder.cs:25,87] — `var maxDate = (DateOnly)sharedState["__maxEffectiveDate"];` and `["as_of"] = maxDate`

BR-8: Output rows are ordered by the iteration order of filtered bond securities rows (same order as input securities data).
- Confidence: MEDIUM
- Evidence: [BondMaturityScheduleBuilder.cs:72] — iterates `bonds` list which preserves original `securities` order

BR-9: Holdings are joined to bonds via `security_id` — holdings whose security_id does not appear in the bond lookup are skipped.
- Confidence: HIGH
- Evidence: [BondMaturityScheduleBuilder.cs:58] — `if (!bondLookup.ContainsKey(secId)) continue;`

BR-10: Null ticker, security_name, or sector values default to empty string.
- Confidence: HIGH
- Evidence: [BondMaturityScheduleBuilder.cs:44-48] — `?.ToString() ?? ""`

BR-11: Data is sourced for the effective date range injected by the executor (no explicit date filters in the job config).
- Confidence: HIGH
- Evidence: [bond_maturity_schedule.json] — no minEffectiveDate/maxEffectiveDate in DataSourcing modules; Architecture.md documents automatic injection.

## Output Schema

| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| security_id | securities.security_id | Cast to int via `Convert.ToInt32` | [BondMaturityScheduleBuilder.cs:43,79] |
| ticker | securities.ticker | Null coalesced to empty string | [BondMaturityScheduleBuilder.cs:45,82] |
| security_name | securities.security_name | Null coalesced to empty string | [BondMaturityScheduleBuilder.cs:46,83] |
| sector | securities.sector | Null coalesced to empty string | [BondMaturityScheduleBuilder.cs:47,84] |
| total_held_value | SUM(holdings.current_value) for matching security_id | Rounded to 2 decimal places | [BondMaturityScheduleBuilder.cs:63,85] |
| holder_count | COUNT of holdings rows per security_id | Integer count | [BondMaturityScheduleBuilder.cs:63,86] |
| as_of | __maxEffectiveDate from shared state | DateOnly value | [BondMaturityScheduleBuilder.cs:25,87] |

## Non-Deterministic Fields
None identified. All values are deterministic given the same input data and effective date.

## Write Mode Implications
- **Overwrite** mode: Each run replaces the entire Parquet output directory.
- For multi-day auto-advance runs, only the final effective date's output persists in the Parquet files. Prior dates are overwritten.
- The `as_of` column records `__maxEffectiveDate`, so each run's output reflects only the last processed date.

## Edge Cases

1. **No bonds in data**: If `security_type = 'Bond'` yields zero rows, an empty DataFrame is written to Parquet. (HIGH confidence — [BondMaturityScheduleBuilder.cs:33-37])

2. **Bond with no holdings**: The bond still appears in output with `total_held_value = 0` and `holder_count = 0`. (HIGH confidence — [BondMaturityScheduleBuilder.cs:75-77])

3. **NULL security_type**: Securities with null `security_type` are excluded because `null?.ToString()` returns null, which does not equal `"Bond"`. (HIGH confidence — [BondMaturityScheduleBuilder.cs:29])

4. **Weekend/holiday dates**: Securities table has data for all calendar days; holdings skips weekends. If the effective date falls on a weekend, holdings may have no data for that as_of while securities does. The External module does NOT apply weekend fallback logic — it processes whatever data DataSourcing returns. (MEDIUM confidence — observed from database date patterns)

5. **Multiple as_of dates in source data**: When effective date range spans multiple days, the DataSourcing module returns all matching rows. The External module aggregates across ALL rows without filtering by as_of, meaning holdings from multiple dates may be summed together. (HIGH confidence — [BondMaturityScheduleBuilder.cs:55-67] — no date filtering in the aggregation loop)

6. **NULL current_value in holdings**: `Convert.ToDecimal(null)` would throw an exception. No null guard exists. (MEDIUM confidence — [BondMaturityScheduleBuilder.cs:60])

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|------------------|
| Bond-only filter | [BondMaturityScheduleBuilder.cs:29] |
| Empty output on no securities | [BondMaturityScheduleBuilder.cs:19-23] |
| Empty output on no bonds | [BondMaturityScheduleBuilder.cs:33-37] |
| Aggregation logic (sum value, count holders) | [BondMaturityScheduleBuilder.cs:52-67] |
| Rounding to 2 decimal places | [BondMaturityScheduleBuilder.cs:85] |
| Bonds with no holdings included | [BondMaturityScheduleBuilder.cs:75-77] |
| as_of from __maxEffectiveDate | [BondMaturityScheduleBuilder.cs:25,87] |
| Parquet output with Overwrite | [bond_maturity_schedule.json:24-30] |
| Null coalescing for string fields | [BondMaturityScheduleBuilder.cs:44-48] |
| DataSourcing columns | [bond_maturity_schedule.json:6-18] |

## Open Questions

1. **Misleading job name**: The job is named "BondMaturitySchedule" but computes holding aggregates, not maturity dates. No maturity date column exists in the securities table or the output schema. The name may be a legacy artifact. (LOW confidence — no maturity data in schema)

2. **Cross-date aggregation**: When the effective date range spans multiple days, holdings from all dates are aggregated together without date-level grouping. This may produce inflated totals if the same holding appears on multiple dates. Unclear if this is intentional. (MEDIUM confidence — [BondMaturityScheduleBuilder.cs:55-67])
