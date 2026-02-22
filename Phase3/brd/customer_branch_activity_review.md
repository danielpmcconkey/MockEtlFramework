# CustomerBranchActivity BRD — Review Report

**Reviewer:** reviewer
**Date:** 2026-02-22
**BRD:** Phase3/brd/customer_branch_activity_brd.md
**Result:** PASS

## Evidence Citation Verification

| Requirement | Citation | Verified? | Notes |
|-------------|----------|-----------|-------|
| BR-1 | CustomerBranchActivityBuilder.cs:42-49 | YES | Lines 42-49 contain the foreach loop grouping by customer_id |
| BR-2 | CustomerBranchActivityBuilder.cs:46-49 | YES | `visitCounts[custId]++` at lines 46-48 |
| BR-3 | CustomerBranchActivityBuilder.cs:61-68, 32-39 | YES | Lines 61-68: null fallback when customer not in dict; lines 32-39: customer lookup build |
| BR-4 | CustomerBranchActivityBuilder.cs:52 | YES | `var asOf = branchVisits.Rows[0]["as_of"];` exactly at line 52 |
| BR-5 | CustomerBranchActivityBuilder.cs:19-29 | YES | Null/empty guards for both customers (19-23) and branchVisits (25-29) |
| BR-6 | customer_branch_activity.json:34 | MINOR | `"writeMode": "Append"` is at line 35 in the JSON, not 34. Substantively correct. |
| BR-7 | datalake data patterns + .cs:19-22 | YES | DB confirms: branch_visits has Oct 5-6 data (20 and 17 rows), customers has NO Oct 5-6, curated output has NO Oct 5-6 rows |

All citations verified. One minor off-by-one on BR-6 line number (34 vs 35). No substantive impact.

## Anti-Pattern Assessment

| AP Code | BRD Finding | Reviewer Assessment |
|---------|-------------|---------------------|
| AP-1 | YES — branches table sourced but never used | CONFIRMED. Grep of .cs file shows zero references to "branches". Job config lines 20-25 source branches table unnecessarily. |
| AP-3 | YES — unnecessary External for GROUP BY + LEFT JOIN | CONFIRMED. Logic is COUNT(*) GROUP BY customer_id + LEFT JOIN customers — trivially expressible in SQL. |
| AP-4 | YES — visit_id, branch_id, visit_purpose sourced but unused | CONFIRMED. Only customer_id is referenced in the .cs file (lines 43-48). |
| AP-5 | Not listed | NOTED — Latent asymmetry exists: lines 36-37 use `?? ""` to coalesce null names to empty string when building lookup, but lines 61-62 use null for missing customers. However, DB verification shows all branch_visit customer_ids exist in customers and no NULLs/empty strings appear in actual output. The asymmetry would only manifest with data not present in the current dataset. BRD does document the missing-customer edge case at BR-3. Acceptable to not list as AP-5 since the pattern is documented and does not affect output. |
| AP-6 | YES — row-by-row iteration | CONFIRMED. Two foreach loops at lines 43 and 56. |

Anti-pattern section is thorough and accurate for the patterns that affect real output.

## Database Spot-Checks

- Output schema matches BRD: customer_id (integer), first_name (varchar), last_name (varchar), as_of (date), visit_count (integer)
- 23 dates in output (weekdays only Oct 1-31) — consistent with BR-7 weekend behavior
- Visit counts verified: direct COUNT from datalake.branch_visits matches curated output exactly for Oct 1
- No NULL first_name/last_name in output — all branch_visit customers exist in customers table
- No empty string first_name/last_name in output — no null coalescing triggered

## Impossible Knowledge Check

No evidence of information from forbidden sources. All claims derive from code, config, and data inspection.

## Unsupported/Speculative Claims Check

- Open question about output ordering is appropriately flagged with MEDIUM confidence. The BRD correctly notes Dictionary iteration order behavior.

## Completeness Check

- [x] Overview present
- [x] Source tables documented
- [x] Business rules (7) with confidence and evidence
- [x] Output schema with transformations
- [x] Edge cases documented
- [x] Anti-patterns identified (4)
- [x] Traceability matrix
- [x] Open questions

## Verdict: PASS

Well-structured BRD with accurate citations. The four anti-patterns are correctly identified with clear evidence. Weekend behavior (BR-7) is thoroughly documented with multi-source evidence. The latent AP-5 asymmetry is a minor observation that doesn't affect actual output and the relevant behavior is documented in BR-3.
