# OverdraftRecoveryRate — Business Requirements Document

## Overview
Calculates the overdraft fee recovery rate — the proportion of overdraft events where fees were actually charged (not waived). Produces a single-row summary output. Contains known integer division and banker's rounding bugs. Output is a CSV file with a trailer line.

## Output Type
CsvFileWriter

## Writer Configuration
- **source**: `output`
- **outputFile**: `Output/curated/overdraft_recovery_rate.csv`
- **includeHeader**: true
- **writeMode**: Overwrite
- **lineEnding**: LF
- **trailerFormat**: `TRAILER|{row_count}|{date}`

## Source Tables

| Table | Columns Used | Filters | Evidence |
|-------|-------------|---------|----------|
| datalake.overdraft_events | overdraft_id, account_id, customer_id, overdraft_amount, fee_amount, fee_waived, event_timestamp | Effective date range injected via shared state | [overdraft_recovery_rate.json:4-11] |

### Table Schema (from database)

**overdraft_events**: overdraft_id (integer), account_id (integer), customer_id (integer), overdraft_amount (numeric), fee_amount (numeric), fee_waived (boolean), event_timestamp (timestamp), as_of (date)

## Business Rules

BR-1: All overdraft events across the effective date range are counted. Each event increments `totalEvents` and either `chargedCount` (fee_waived=false) or `waivedCount` (fee_waived=true).
- Confidence: HIGH
- Evidence: [OverdraftRecoveryRateProcessor.cs:33-41] `if (feeWaived) waivedCount++; else chargedCount++;`

BR-2: **Integer division bug** — `recovery_rate` is computed as `(decimal)(chargedCount / totalEvents)` where both operands are `int`. Since `chargedCount` is always less than `totalEvents`, integer division truncates to 0.
- Confidence: HIGH
- Evidence: [OverdraftRecoveryRateProcessor.cs:44] `decimal recoveryRate = (decimal)(chargedCount / totalEvents);` — Comment: `W4: Integer division`

BR-3: **Banker's rounding** — After the integer division, the result is rounded to 2 decimal places using `MidpointRounding.ToEven` (banker's rounding). Since the input is always 0 (due to integer division), this has no practical effect, but it establishes a rounding convention.
- Confidence: HIGH
- Evidence: [OverdraftRecoveryRateProcessor.cs:47] `Math.Round(recoveryRate, 2, MidpointRounding.ToEven);` — Comment: `W5: Banker's rounding`

BR-4: The output is always a single row containing aggregate totals across all dates in the effective range.
- Confidence: HIGH
- Evidence: [OverdraftRecoveryRateProcessor.cs:49-57] Single Row added to outputRows

BR-5: The `as_of` column is set to `__maxEffectiveDate` formatted as `yyyy-MM-dd`.
- Confidence: HIGH
- Evidence: [OverdraftRecoveryRateProcessor.cs:55] `["as_of"] = maxDate.ToString("yyyy-MM-dd")`

BR-6: The trailer uses standard framework tokens: `{row_count}` (always 1 for non-empty data) and `{date}` (`__maxEffectiveDate`).
- Confidence: HIGH
- Evidence: [overdraft_recovery_rate.json:22] `"trailerFormat": "TRAILER|{row_count}|{date}"`

BR-7: Effective dates are injected by the executor at runtime.
- Confidence: HIGH
- Evidence: [overdraft_recovery_rate.json:4-11] No hardcoded dates in DataSourcing config

## Output Schema

| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| total_events | Derived | Count of all overdraft events in effective range | [OverdraftRecoveryRateProcessor.cs:35] |
| charged_count | Derived | Count where fee_waived=false | [OverdraftRecoveryRateProcessor.cs:39-40] |
| waived_count | Derived | Count where fee_waived=true | [OverdraftRecoveryRateProcessor.cs:37-38] |
| recovery_rate | Derived | charged_count / total_events (integer division, always 0), rounded to 2 decimal places | [OverdraftRecoveryRateProcessor.cs:44] |
| as_of | `__maxEffectiveDate` | Formatted as yyyy-MM-dd | [OverdraftRecoveryRateProcessor.cs:55] |

**Trailer row**: `TRAILER|{row_count}|{date}` — row_count is always 1 (one summary row)

## Non-Deterministic Fields
None identified. Output is deterministic.

## Write Mode Implications
- **Overwrite** mode: Each execution replaces the CSV file. On multi-day auto-advance, only the final effective date's output survives.
- Since this produces a single aggregated row across all dates, the final run's output includes data from the full effective range.
- Confidence: HIGH
- Evidence: [overdraft_recovery_rate.json:23] `"writeMode": "Overwrite"`

## Edge Cases

EC-1: **Recovery rate always 0** — Due to integer division (`chargedCount / totalEvents` where both are int), the recovery rate is always 0 unless `chargedCount >= totalEvents`, which can never happen since `chargedCount + waivedCount = totalEvents`.
- Confidence: HIGH
- Evidence: [OverdraftRecoveryRateProcessor.cs:44] Comment: `W4: Integer division`

EC-2: **Banker's rounding on zero** — The `MidpointRounding.ToEven` rounding is applied to 0, producing 0. If the integer division were fixed, banker's rounding would affect values at the 0.5 midpoint differently than standard rounding (e.g., 0.825 rounds to 0.82, not 0.83).
- Confidence: HIGH
- Evidence: [OverdraftRecoveryRateProcessor.cs:47] Comment: `W5: Banker's rounding`

EC-3: **Empty source data** — If no overdraft events exist, an empty DataFrame is returned and the CSV will contain only a header and trailer (row_count=0).
- Confidence: HIGH
- Evidence: [OverdraftRecoveryRateProcessor.cs:24-28]

EC-4: **Overwrite on multi-day runs** — Only the last effective date's output survives. Since this is an aggregate across all dates, the final run's data is the most complete.
- Confidence: HIGH
- Evidence: [overdraft_recovery_rate.json:23] `"writeMode": "Overwrite"`

EC-5: **Unused sourced columns** — `overdraft_id`, `account_id`, `customer_id`, `overdraft_amount`, `event_timestamp` are sourced but only `fee_waived` is used in the logic.
- Confidence: HIGH
- Evidence: Comparison of DataSourcing columns vs. processor logic

EC-6: **Division by zero** — If `totalEvents` is 0, the code would throw a `DivideByZeroException`. However, this path is guarded by the empty check at line 24-28.
- Confidence: HIGH
- Evidence: [OverdraftRecoveryRateProcessor.cs:24-28,44]

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|------------------|
| BR-1: Event counting | [OverdraftRecoveryRateProcessor.cs:33-41] |
| BR-2: Integer division bug | [OverdraftRecoveryRateProcessor.cs:44] |
| BR-3: Banker's rounding | [OverdraftRecoveryRateProcessor.cs:47] |
| BR-4: Single-row output | [OverdraftRecoveryRateProcessor.cs:49-57] |
| BR-5: as_of from maxDate | [OverdraftRecoveryRateProcessor.cs:55] |
| BR-6: Trailer format | [overdraft_recovery_rate.json:22] |
| EC-1: Rate always 0 | [OverdraftRecoveryRateProcessor.cs:44] |
| EC-2: Banker's rounding | [OverdraftRecoveryRateProcessor.cs:47] |

## Open Questions
1. **Is the integer division intentional?** The `W4` comment flags this explicitly. The recovery rate will always be 0. The correct formula would be `(decimal)chargedCount / totalEvents`. Confidence: HIGH that this is a known bug.
2. **Banker's rounding significance** — If the division were fixed, `MidpointRounding.ToEven` would behave differently from standard rounding at midpoints. This may be intentional for financial accuracy, or it may be an oversight. Confidence: MEDIUM.
