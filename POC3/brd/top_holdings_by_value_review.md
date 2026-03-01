# top_holdings_by_value -- BRD Review

## Reviewer: reviewer-1
## Status: PASS

## Section Checks
| Section | Status | Notes |
|---------|--------|-------|
| Overview | PASS | Accurate summary of ranked top 20 securities with tier classification |
| Output Type | PASS | ParquetFileWriter confirmed |
| Writer Configuration | PASS | numParts 50, Overwrite, source top_holdings match JSON config |
| Source Tables | PASS | holdings and securities match config |
| Business Rules | PASS | All 11 rules verified against SQL |
| Output Schema | PASS | All 8 columns documented with correct sources |
| Non-Deterministic Fields | PASS | ROW_NUMBER tie-breaking correctly identified |
| Write Mode Implications | PASS | Overwrite behavior and empty parts correctly documented |
| Edge Cases | PASS | Unused CTE, ties, multi-date ranking, empty parts all covered |
| Traceability Matrix | PASS | All requirements traced to evidence |

## Evidence Spot-Checks

| Requirement | Cited Evidence | Verified? | Notes |
|-------------|---------------|-----------|-------|
| BR-5: Tier classification | [top_holdings_by_value.json:22] | YES | CASE WHEN rank<=5 'Top 5', <=10 'Top 10', <=20 'Top 20', ELSE 'Other' |
| BR-7: unused_cte | [top_holdings_by_value.json:22] | YES | `unused_cte AS (SELECT security_id, total_held_value FROM security_totals WHERE total_held_value > 0)` is defined but never referenced |
| BR-11: ROW_NUMBER non-deterministic ties | [top_holdings_by_value.json:22] | YES | `ROW_NUMBER() OVER (ORDER BY st.total_held_value DESC)` with no tiebreaker |
| BR-10: numParts 50 for max 20 rows | [top_holdings_by_value.json:28] | YES | Config: `"numParts": 50` |

## Issues Found
None.

## Verdict
PASS: Thorough SQL CTE analysis. Good catches on the unused CTE (dead code), the cross-date ranking issue (no PARTITION BY as_of), the dead "Other" tier branch, and the oversized numParts. The ROW_NUMBER non-determinism for ties is correctly identified.
