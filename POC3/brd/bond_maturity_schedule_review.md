# bond_maturity_schedule -- BRD Review

## Reviewer: reviewer-1
## Status: PASS

## Section Checks
| Section | Status | Notes |
|---------|--------|-------|
| Overview | PASS | Accurate description; correctly notes the misleading job name vs actual behavior |
| Output Type | PASS | Correctly identifies ParquetFileWriter |
| Writer Configuration | PASS | All params verified: outputDirectory, numParts=1, writeMode=Overwrite match JSON lines 24-30 |
| Source Tables | PASS | Both tables listed with correct columns; bond filter and holdings join correctly described |
| Business Rules | PASS | 11 rules with appropriate confidence levels and verified evidence |
| Output Schema | PASS | 7 columns documented, matches code outputColumns definition at lines 10-14 |
| Non-Deterministic Fields | PASS | States none; acceptable given the output iterates bonds list in input order (MEDIUM confidence noted in BR-8) |
| Write Mode Implications | PASS | Correctly describes Overwrite behavior and as_of reflecting __maxEffectiveDate |
| Edge Cases | PASS | 6 edge cases including cross-date aggregation, NULL current_value exception risk, NULL security_type exclusion |
| Traceability Matrix | PASS | All key requirements mapped to evidence |
| Open Questions | PASS | Two well-reasoned questions: misleading name (LOW) and cross-date aggregation (MEDIUM) |

## Evidence Spot-Checks

| Requirement | Cited Evidence | Verified? | Notes |
|-------------|---------------|-----------|-------|
| BR-1: Bond-only filter | [BondMaturityScheduleBuilder.cs:29] | YES | Line 29: `.Where(r => r["security_type"]?.ToString() == "Bond")` |
| BR-5: Rounding to 2 dp | [BondMaturityScheduleBuilder.cs:85] | YES | Line 85: `Math.Round(totals.totalValue, 2)` — correctly notes default ToEven rounding |
| BR-6: Bonds with no holdings | [BondMaturityScheduleBuilder.cs:75-77] | YES | Lines 75-77: ternary with default `(totalValue: 0m, holderCount: 0)` |
| BR-7: as_of from __maxEffectiveDate | [BondMaturityScheduleBuilder.cs:25,87] | YES | Line 25: reads shared state; line 87: assigns to output row |
| Edge Case 6: NULL current_value | [BondMaturityScheduleBuilder.cs:60] | YES | Line 60: `Convert.ToDecimal(row["current_value"])` — no null guard, would throw |

## Issues Found
None. All evidence citations verified against source code and job config. No hallucinations. No impossible knowledge.

Minor observation (not blocking): The bondLookup dictionary (lines 40-49) uses last-wins semantics for the same security_id across multiple as_of dates, meaning bond metadata (ticker, name, sector) could vary based on iteration order in multi-day runs. This is implicitly captured by Edge Case 5 and Open Question 2 regarding cross-date behavior.

## Verdict
PASS: BRD is approved. Thorough analysis of the External module with strong evidence chains. Good identification of the misleading job name, cross-date aggregation concerns, and the NULL current_value exception risk.
