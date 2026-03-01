# customer_compliance_risk -- BRD Review

## Reviewer: reviewer-1
## Status: PASS

## Section Checks
| Section | Status | Notes |
|---------|--------|-------|
| Overview | PASS | Accurate description of composite risk scoring job |
| Output Type | PASS | Correctly identifies CsvFileWriter |
| Writer Configuration | PASS | All params verified: outputFile, includeHeader=true, no trailer, writeMode=Overwrite, lineEnding=LF match JSON lines 38-45 |
| Source Tables | PASS | All 4 tables listed with correct columns and filter descriptions |
| Business Rules | PASS | 12 rules with HIGH confidence; account_id/customer_id mismatch bug well-documented |
| Output Schema | PASS | 8 columns documented matching code outputColumns at lines 10-14 |
| Non-Deterministic Fields | PASS | States none; output follows customers.Rows iteration order |
| Write Mode Implications | PASS | Correctly describes Overwrite behavior |
| Edge Cases | PASS | 5 edge cases including the mismatch bug, threshold analysis, and double precision |
| Traceability Matrix | PASS | All 12 requirements mapped to evidence citations |
| Open Questions | PASS | Two well-reasoned questions about the proxy bug and threshold relevance |

## Evidence Spot-Checks

| Requirement | Cited Evidence | Verified? | Notes |
|-------------|---------------|-----------|-------|
| BR-4: account_id as customer_id proxy | [CustomerComplianceRiskCalculator.cs:57-73] | YES | Lines 65-70: dictionary keyed by accountId; lines 66-67 comment acknowledges the proxy |
| BR-5: Mismatch means zero matches | [CustomerComplianceRiskCalculator.cs:66-67,85] | YES | Line 85: `GetValueOrDefault(customerId, 0)` looks up by customerId in accountId-keyed dict |
| BR-6: Risk score formula | [CustomerComplianceRiskCalculator.cs:88] | YES | Line 88: `double riskScore = (complianceCount * 30.0) + (wireCount * 20.0) + (highTxnCount * 10.0)` |
| BR-7: Banker's rounding | [CustomerComplianceRiskCalculator.cs:91] | YES | Line 91: `Math.Round(riskScore, 2, MidpointRounding.ToEven)` |
| BR-11: as_of from customer row | [CustomerComplianceRiskCalculator.cs:102] | YES | Line 102: `["as_of"] = custRow["as_of"]` -- uses row's as_of, not __maxEffectiveDate |

## Issues Found
None. All evidence citations verified against source code and job config. No hallucinations. No impossible knowledge.

## Verdict
PASS: BRD is approved. Outstanding analytical work -- the discovery of the account_id/customer_id proxy bug (BR-4/BR-5) with the layered observation that max transaction amount (4200) is below the 5000 threshold anyway (BR-12) demonstrates deep analysis. The risk score formula, Banker's rounding, and as_of sourcing are all correctly documented.
