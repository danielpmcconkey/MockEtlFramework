# TopBranches BRD — Review Report

**Reviewer:** reviewer
**Date:** 2026-02-22
**BRD:** Phase3/brd/top_branches_brd.md
**Result:** PASS

## Evidence Citation Verification

| Requirement | Citation | Verified? | Notes |
|-------------|----------|-----------|-------|
| BR-1 | top_branches.json:22 | YES | COUNT(*) GROUP BY bv.branch_id |
| BR-2 | top_branches.json:22 | YES | RANK() OVER (ORDER BY total_visits DESC) |
| BR-3 | top_branches.json:22 | YES | JOIN branches b ... b.branch_name |
| BR-4 | top_branches.json:22 | YES | b.as_of in SELECT |
| BR-5 | top_branches.json:22 | YES | CTE only includes branches with visits |
| BR-6 | top_branches.json:22 | YES | ORDER BY rank, vt.branch_id |
| BR-7 | top_branches.json:28 | YES | `"writeMode": "Overwrite"` |

Data verification confirms branch_id 27 has 4 visits on Oct 31.

## Anti-Pattern Assessment

| AP Code | BRD Finding | Reviewer Assessment |
|---------|-------------|---------------------|
| AP-1 | NOT PRESENT — correctly noted both tables are used | CORRECT |
| AP-2 | YES — duplicates BranchVisitSummary visit counts | CONFIRMED. Key finding. |
| AP-4 | YES — visit_id unused | CONFIRMED. SQL only uses branch_id. |
| AP-7 | YES — hardcoded date '2024-10-01' | CONFIRMED. Redundant. |
| AP-8 | YES — CTE could be simplified | CONFIRMED. |
| AP-10 | YES — dependency declared but not leveraged | CONFIRMED. |

Good observation that AP-1 does NOT apply here. Five APs correctly identified.

## Verdict: PASS

Well-structured BRD with good RANK vs DENSE_RANK edge case documentation and AP-2 finding.
