# regulatory_exposure_summary -- BRD Review

## Reviewer: reviewer-1
## Status: PASS (Cycle 2)

## Section Checks
| Section | Status | Notes |
|---------|--------|-------|
| Overview | PASS | Accurate summary of exposure score calculation |
| Output Type | PASS | ParquetFileWriter confirmed |
| Writer Configuration | PASS | numParts 1, Overwrite, outputDirectory all match JSON config |
| Source Tables | PASS | All 4 sources (compliance_events, wire_transfers, accounts, customers) match config columns exactly |
| Business Rules | PASS | All 11 rules verified; BR-5 and BR-6 corrected in revision (MidpointRounding.ToEven default) |
| Output Schema | PASS | All 9 columns documented with correct sources and transformations |
| Non-Deterministic Fields | PASS | None identified is correct |
| Write Mode Implications | PASS | Overwrite behavior correctly documented |
| Edge Cases | PASS | Weekend fallback, cross-date inflation, empty input, fallback-to-all, decimal vs double all correct |
| Traceability Matrix | PASS | All 11 requirements traced to evidence |

## Evidence Spot-Checks

| Requirement | Cited Evidence | Verified? | Notes |
|-------------|---------------|-----------|-------|
| BR-1: Weekend fallback | [RegulatoryExposureCalculator.cs:29-32] | YES | Lines 29-32: Saturday AddDays(-1), Sunday AddDays(-2) |
| BR-4: Exposure formula | [RegulatoryExposureCalculator.cs:105-106] | YES | `(complianceCount * 30.0m) + (wireCount * 20.0m) + (totalBalance / 10000.0m)` with Math.Round(..., 2) |
| BR-5: Decimal arithmetic + banker's rounding | [RegulatoryExposureCalculator.cs:105-106] | YES | Revision correctly states both jobs use banker's rounding (ToEven); key difference is decimal vs double arithmetic |
| BR-6: Balance rounding | [RegulatoryExposureCalculator.cs:114] | YES | Revision correctly states implicit MidpointRounding.ToEven default |
| BR-9: Empty input guard | [RegulatoryExposureCalculator.cs:22-26] | YES | `if (customers == null || customers.Count == 0)` returns empty DataFrame |

## Cycle 1 Issues -- Resolution

### ISSUE 1 (was BLOCKING): BR-5 incorrectly claimed default MidpointRounding is AwayFromZero
**Status**: RESOLVED in revision

**Original claim**: "Math.Round(value, 2) uses default MidpointRounding.AwayFromZero"

**Revised claim**: "Both jobs use banker's rounding (MidpointRounding.ToEven) -- CustomerComplianceRisk specifies it explicitly, while this job uses it implicitly via the Math.Round(decimal, int) default. The key difference between the two jobs is decimal vs double arithmetic, not the rounding mode."

This is now factually correct. C# `Math.Round(decimal, int)` defaults to `MidpointRounding.ToEven`.

BR-6 and edge case #5 were also updated consistently with the corrected understanding.

## Verdict
PASS (Cycle 2): All issues from cycle 1 have been resolved. The BRD now correctly documents the implicit banker's rounding behavior and accurately contrasts the decimal vs double arithmetic difference with CustomerComplianceRisk. All 11 business rules verified against source code.
