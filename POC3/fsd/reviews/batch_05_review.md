# FSD Review -- Batch 05

## Summary
- Jobs reviewed: 10
- PASS: 8
- FAIL: 2

---

## Per-Job Reviews

### customer_full_profile
**Verdict: PASS**
- Traceability: PASS -- All 9 BRD requirements (BR-1 through BR-9) are traced in the FSD's traceability matrix (Section 9). AP3, AP4, AP6 from the BRD are identified and addressed. No W-codes apply, and the FSD correctly documents why for each.
- Module Chain: PASS -- Tier 1 is justified. The FSD provides a detailed mapping of every V1 C# operation to its SQL equivalent (dictionary lookups to ROW_NUMBER window functions, foreach loops to JOINs, segment concatenation to GROUP_CONCAT). The justification is thorough and convincing. The lengthy deliberation about empty-table handling on weekends is well-reasoned, and the final decision to stay Tier 1 is defensible.
- SQL/Logic: PASS -- The SQL correctly implements: primary phone/email selection via ROW_NUMBER with PARTITION BY customer_id+as_of, segment concatenation via GROUP_CONCAT with INNER JOIN (filtering unknown segment_ids as V1 does), age calculation via strftime date arithmetic matching V1's year-difference-with-birthday-adjustment pattern, age bracket CASE expression with correct thresholds (<26, <=35, <=45, <=55, <=65, else 65+), and COALESCE for empty string defaults. The as_of join predicate discussion (Section 5 Note 1) is especially well-analyzed.
- Writer Config: PASS -- ParquetFileWriter, numParts=2, writeMode=Overwrite all match V1. Output path correctly uses V2 directory.
- Proofmark Config: PASS -- Starts strict with parquet reader, threshold 100.0. Correctly identifies segments, primary_phone, and primary_email as potentially needing EXCLUDED treatment due to non-deterministic ordering, but follows the "start strict, add overrides with evidence" best practice. Well-aligned with BRD's non-deterministic fields section.

Notes: Exceptionally thorough FSD. The extended analysis of the empty-table edge case and the as_of join semantics shows deep understanding of the framework internals.

---

### customer_investment_summary
**Verdict: PASS**
- Traceability: PASS -- All 9 BRD requirements (BR-1 through BR-9) are traced in the FSD's traceability matrix. AP1 (dead-end securities sourcing), AP3 (unnecessary External), AP4 (unused columns birthdate/advisor_id/investment_id), AP6 (row-by-row iteration) are all identified and eliminated. W5 (Banker's rounding) is correctly identified as applicable and addressed.
- Module Chain: PASS -- Tier 1 is well-justified. The FSD maps aggregation, JOIN, COALESCE, and ROUND to their SQL equivalents. Removing the securities DataSourcing (AP1) and the External module (AP3) is correct.
- SQL/Logic: PASS -- The SQL correctly implements: GROUP BY customer_id, COUNT(*) for investment_count, ROUND(SUM(current_value), 2) for total_value with Banker's rounding, LEFT JOIN with DISTINCT subquery for customer name lookup with COALESCE to empty string, MAX(i.as_of) for the effective date. The DISTINCT subquery for customer deduplication is a thoughtful touch. The row ordering discussion (ORDER BY customer_id vs dictionary insertion order) is honest about the potential mismatch and defers to Proofmark validation.
- Writer Config: PASS -- CsvFileWriter, includeHeader=true, writeMode=Overwrite, lineEnding=LF, no trailer -- all match V1.
- Proofmark Config: PASS -- csv reader, header_rows=1, trailer_rows=0, threshold=100.0. No exclusions or fuzzy columns. The risk assessment noting potential row ordering issues is appropriate.

Notes: Clean, well-structured FSD. The BRD correction is not needed here (unlike customer_value_score). The as_of handling via MAX(i.as_of) is a clever alternative to accessing shared state from SQL.

---

### customer_segment_map
**Verdict: PASS**
- Traceability: PASS -- All 7 BRD requirements (BR-1 through BR-7) are traced. AP1 (dead-end sourcing of branches table) is correctly identified and eliminated. Both open questions from the BRD (OQ-1 branches, OQ-2 as_of gaps) are addressed in the appendix.
- Module Chain: PASS -- Tier 1 is correct. V1 was already Tier 1 (no External module). The only change is removing the dead-end branches DataSourcing.
- SQL/Logic: PASS -- The SQL is identical to V1's SQL (which was already clean). INNER JOIN on segment_id+as_of, ORDER BY customer_id+segment_id, all five output columns present in correct order. The FSD correctly notes that the V1 SQL was already correct and only the config needed cleanup.
- Writer Config: PASS -- CsvFileWriter, includeHeader=true, writeMode=Append, lineEnding=LF, no trailer -- all match V1. The write mode implications section correctly describes Append behavior with header suppression on subsequent writes.
- Proofmark Config: PASS -- csv reader, header_rows=1, trailer_rows=0, threshold=100.0. No exclusions or fuzzy. All columns are deterministic integers/strings/dates.

Notes: Straightforward job, straightforward FSD. V1 was already clean; V2 only removes the dead-end branches source. No issues.

---

### customer_transaction_activity
**Verdict: PASS**
- Traceability: PASS -- All 11 BRD requirements (BR-1 through BR-11) are traced in the FSD's traceability matrix. AP3 (unnecessary External), AP4 (unused transaction_id), AP6 (row-by-row iteration) are identified and eliminated. W12 is correctly analyzed as not applicable (framework handles header suppression, not the External module).
- Module Chain: PASS -- Tier 1 is justified. The FSD demonstrates that the V1 External module's dictionary-lookup+aggregation pattern maps cleanly to INNER JOIN + GROUP BY with CASE WHEN conditional counting.
- SQL/Logic: PASS -- The SQL correctly implements: INNER JOIN between transactions and accounts (matching V1's dictionary lookup), WHERE a.customer_id != 0 (matching V1's skip-unmatched guard), SUM(CASE WHEN) for debit/credit counting, SUM(t.amount) without rounding (matching BR-4), correlated subquery (SELECT MIN(as_of) FROM transactions) for the single as_of value (matching BR-7). The analysis of W12 header behavior in Append mode (Section 3.1) is thorough. The row ordering discussion (Section 3.5) is honest about potential mismatches.
- Writer Config: PASS -- CsvFileWriter, includeHeader=true, writeMode=Append, lineEnding=LF, no trailer -- all match V1.
- Proofmark Config: PASS -- csv reader, header_rows=1, trailer_rows=0, threshold=100.0. Correctly starts strict. The risk assessment for row ordering and total_amount precision is appropriate.

Notes: Good analysis of the empty-data edge case and the decision to stay Tier 1 based on the datalake's full-load pattern. The BR-9 (last-write-wins account lookup) analysis is well-reasoned for single-day execution contexts.

---

### customer_value_score
**Verdict: FAIL**
- Traceability: FAIL -- The FSD identifies a BRD error in BR-7 (rounding precision). The BRD states "rounded to the nearest whole number (0 decimal places)" but the FSD claims V1 uses `Math.Round(..., 2)` (2 decimal places). **This is a material discrepancy.** The FSD provides evidence citations (CustomerValueCalculator.cs:114-117) for the 2-decimal-places claim. If the FSD is correct, the BRD is wrong and must be updated before implementation. If the BRD is correct, the FSD's SQL uses the wrong ROUND precision. Either way, the document chain is currently inconsistent -- the BRD says 0 decimal places, the FSD says 2 decimal places, and they cannot both be right. **This must be resolved before proceeding to implementation.** The FSD does call this out explicitly in Section 4 ("BRD Correction: Rounding Precision"), which is good practice, but the BRD itself has not been updated, leaving an inconsistency in the document chain. All other BRD requirements (BR-1 through BR-12) are properly traced.
- Module Chain: PASS -- Tier 1 is well-justified. The FSD maps every V1 operation (dictionary lookups, counting, summing, capping with Math.Min, weighted sums, ROUND) to SQL equivalents. The transaction-to-customer linkage via accounts is correctly handled as a two-level join.
- SQL/Logic: PASS (conditional on rounding resolution) -- The SQL is well-designed: LEFT JOINs for optional data with COALESCE to 0, MIN() for score capping, correct composite score formula with weights (0.4/0.35/0.25), GROUP BY for deduplication. The composite score is computed from un-rounded component scores, matching V1 behavior. The negative balance score discussion (OQ-1) is correctly handled -- no floor applied, matching V1.
- Writer Config: PASS -- CsvFileWriter, includeHeader=true, writeMode=Overwrite, lineEnding=LF, no trailer -- all match V1.
- Proofmark Config: PASS -- csv reader, header_rows=1, trailer_rows=0, threshold=100.0. No exclusions or fuzzy columns. Reasonable given that score computations involve integer counts multiplied by integer-valued constants.

**FAIL reason:** BRD-FSD inconsistency on rounding precision (BR-7). The FSD explicitly notes this discrepancy and provides V1 source code evidence, but the BRD has not been corrected. Per the BLUEPRINT's "Changes flow uphill" principle, the BRD must be updated to match the actual V1 behavior before the FSD can be considered internally consistent. The FSD itself is otherwise excellent -- this is a BRD fix needed, not an FSD rewrite.

---

### daily_balance_movement
**Verdict: PASS**
- Traceability: PASS -- All 8 BRD requirements (BR-1 through BR-8) are traced. W6 (double epsilon) and W9 (wrong writeMode) are correctly identified as applicable and handled. AP3 (unnecessary External), AP4 (unused transaction_id), AP6 (row-by-row iteration) are identified and eliminated.
- Module Chain: PASS -- Tier 1 is justified, with a documented risk for the empty-accounts-on-weekends edge case. The FSD provides a thorough analysis of the empty-table problem and pragmatically decides to stay Tier 1 with documented fallback to Tier 2 if needed during resolution. This is the right approach per the BLUEPRINT's "start at Tier 1, escalate only with justification" principle.
- SQL/Logic: PASS -- The SQL correctly implements: GROUP BY account_id, SUM(CASE WHEN) for debit/credit separation, CAST(t.amount AS REAL) for double-precision accumulation (matching W6), credit_total - debit_total for net_movement, LEFT JOIN with COALESCE for customer_id lookup (default 0), MIN(t.as_of) for first-encountered date. The explicit CAST to REAL is a thoughtful touch to ensure SQLite uses double precision. The W6 analysis is correct -- SQLite REAL is IEEE 754 double, matching V1's Convert.ToDouble behavior. No ORDER BY, matching V1's non-deterministic dictionary iteration.
- Writer Config: PASS -- CsvFileWriter, includeHeader=true, writeMode=Overwrite, lineEnding=LF, no trailer -- all match V1. W9 documented correctly.
- Proofmark Config: PASS -- csv reader, header_rows=1, trailer_rows=0, threshold=100.0. Starts strict. The potential FUZZY escalation for W6 double-precision columns is well-documented with specific column names and tolerance values, to be applied only if strict comparison fails.

Notes: The empty-accounts-on-weekends analysis is thorough and honest. The documented risk and mitigation strategy show good engineering judgment.

---

### daily_transaction_summary
**Verdict: PASS**
- Traceability: PASS -- All 7 BRD requirements (BR-1 through BR-7) are traced, plus the write mode, line ending, and header settings. AP1 (dead-end branches source), AP4 (unused transaction_id and description columns), and AP8 (subquery wrapper) are correctly identified and eliminated. The BRD's open question about the branches table is addressed.
- Module Chain: PASS -- Tier 1 is correct. V1 was already Tier 1; V2 maintains it.
- SQL/Logic: PASS -- The SQL correctly simplifies V1's subquery wrapper (AP8) while preserving all aggregation logic verbatim: GROUP BY account_id+as_of, ROUND(SUM debit + SUM credit, 2) for total_amount, COUNT(*) for transaction_count, individual ROUND(SUM(CASE WHEN)) for debit_total and credit_total, ORDER BY as_of+account_id. The justification for removing the subquery (it adds no transformation) is correct. The V1-to-V2 SQL diff is minimal and safe.
- Writer Config: PASS -- CsvFileWriter, includeHeader=true, trailerFormat="TRAILER|{row_count}|{date}", writeMode=Append, lineEnding=LF -- all match V1. The Append+header+trailer behavior is thoroughly documented with framework code references.
- Proofmark Config: PASS -- csv reader, header_rows=1, trailer_rows=0 (correct for Append mode with embedded trailers per CONFIG_GUIDE.md Example 4), threshold=100.0. No exclusions or fuzzy columns.

Notes: Clean, well-structured FSD for a clean V1 job. The AP8 subquery removal is correctly justified. The Append+trailer behavior documentation (Section 7) is excellent.

---

### daily_transaction_volume
**Verdict: FAIL**
- Traceability: PASS -- All 8 BRD requirements (BR-1 through BR-8) are traced. AP4 (unused columns) and AP8 (unused CTE with min/max) are correctly identified and eliminated.
- Module Chain: PASS -- Tier 1 is correct. V1 was already Tier 1; V2 maintains it.
- SQL/Logic: PASS -- The SQL correctly removes the unnecessary CTE (AP8) while preserving the four output columns. GROUP BY as_of, COUNT(*), ROUND(SUM(amount), 2), ROUND(AVG(amount), 2), ORDER BY as_of -- all match V1.
- Writer Config: PASS -- CsvFileWriter, includeHeader=true, trailerFormat="CONTROL|{date}|{row_count}|{timestamp}", writeMode=Append, lineEnding=CRLF -- all match V1.
- Proofmark Config: FAIL -- The trailer contains a `{timestamp}` token that resolves to UTC now at execution time. The BRD's non-deterministic fields section explicitly identifies this: "Trailer `{timestamp}` token: The `{timestamp}` in the trailer is UTC now at time of writing, making the trailer line non-deterministic across runs." The FSD acknowledges this in Section 8 but proposes starting strict with zero exclusions, deferring to resolution. **The problem is that with `trailer_rows: 0` for Append mode, trailers are treated as data rows by Proofmark. Every trailer line will contain a different timestamp between V1 and V2 runs, causing guaranteed comparison failures on those rows.** The FSD should proactively design for this known non-determinism rather than deferring to resolution. The correct approach is either: (a) acknowledge this will fail and pre-document the exact resolution path, or (b) recognize that since trailer lines are pipe-delimited (not comma-delimited CSV), Proofmark's CSV parser may handle them differently.

**FAIL reason:** The FSD does not adequately address the known non-deterministic `{timestamp}` in the trailer. While "start strict" is generally the right policy, here we have HIGH-confidence evidence from the BRD that the trailer timestamp will differ between runs. The FSD should at minimum document this as a known issue that WILL require resolution (not "may"), and ideally propose the specific Proofmark handling (e.g., whether trailer lines will parse as single-column CSV rows and thus need specific handling). This is not a show-stopper for implementation, but it represents incomplete Proofmark config design that will predictably cause a resolution cycle that could have been anticipated. Upgrading the discussion from "conservative approach: start strict and see" to "known issue: trailer timestamps will differ; resolution will require [specific approach]" would bring this to PASS.

---

### daily_wire_volume
**Verdict: PASS**
- Traceability: PASS -- All 6 BRD requirements (BR-1 through BR-6) are traced. AP4 (unused columns: wire_id, customer_id, direction, status, wire_timestamp) and AP8 (redundant WHERE clause) are correctly identified and eliminated. AP10 (hard-coded dates) is correctly analyzed as intentional business logic, not an anti-pattern -- the hard-coded dates define the job's semantic scope.
- Module Chain: PASS -- Tier 1 is correct. V1 was already Tier 1; V2 maintains it.
- SQL/Logic: PASS -- The SQL correctly removes the redundant WHERE clause (AP8) while preserving the core logic: GROUP BY as_of, COUNT(*), ROUND(SUM(amount), 2), ORDER BY as_of. The duplicate as_of column (BR-6: `as_of AS wire_date` AND bare `as_of`) is correctly preserved for output equivalence. The DataSourcing hard-coded dates (minEffectiveDate/maxEffectiveDate) are correctly preserved.
- Writer Config: PASS -- CsvFileWriter, includeHeader=true, writeMode=Append, lineEnding=LF, no trailer -- all match V1. The write mode implications section correctly explains that every execution appends the same full-quarter aggregation.
- Proofmark Config: PASS -- csv reader, header_rows=1, trailer_rows=0, threshold=100.0. No exclusions or fuzzy. All columns are deterministic. No trailer to worry about.

Notes: The AP10 analysis is well-reasoned. Recognizing that the hard-coded dates ARE the business logic (rather than an anti-pattern to eliminate) shows good judgment. The column reduction from 6 to 1 is aggressive but correct -- only `amount` and the auto-appended `as_of` are used.

---

### debit_credit_ratio
**Verdict: PASS**
- Traceability: PASS -- All 11 BRD requirements (BR-1 through BR-11) are traced. W4 (integer division) and W6 (double epsilon) are correctly identified as applicable and handled. AP1 (unused interest_rate/credit_limit from accounts), AP3 (unnecessary External), AP4 (unused transaction_id/description from transactions), AP6 (row-by-row iteration) are all identified and eliminated.
- Module Chain: PASS -- Tier 1 is well-justified. The FSD provides a comprehensive mapping table of V1 C# operations to SQL equivalents: Dictionary lookups to LEFT JOIN+COALESCE, foreach aggregation to GROUP BY+CASE, Convert.ToDouble to SQLite REAL, int/int division to SQLite integer division, GetValueOrDefault to COALESCE, first-encountered as_of to MIN().
- SQL/Logic: PASS -- The SQL correctly implements: integer division via SQLite's native int/int truncation (W4), double-precision accumulation via SQLite REAL type (W6), conditional counting/summing with SUM(CASE WHEN), zero-guard CASE expressions for both count ratio and amount ratio, LEFT JOIN with subquery for accounts deduplication (GROUP BY account_id), COALESCE for customer_id default 0, MIN(as_of) for deterministic date selection. The W4 handling is particularly elegant -- SQLite's integer division naturally matches C#'s int/int behavior without any extra code.
- Writer Config: PASS -- ParquetFileWriter, numParts=1, writeMode=Overwrite -- all match V1.
- Proofmark Config: PASS -- parquet reader, threshold=100.0. Starts strict. The W6 double-precision assessment is thorough, noting that IEEE 754 addition is not associative and accumulation order may differ between C# loop and SQLite SUM. The contingency FUZZY config is well-specified with appropriate tolerance values (1e-10) and evidence citations. Following the "start strict" best practice.

Notes: Excellent FSD. The W4 and W6 analysis is thorough and technically accurate. The SQL design handles both wrinkles naturally through SQLite's type system rather than requiring explicit workarounds.

---

## Cross-Batch Observations

1. **Empty-table edge case pattern:** Multiple jobs (customer_full_profile, customer_investment_summary, customer_transaction_activity, daily_balance_movement, debit_credit_ratio) face the same issue: the Transformation module's `RegisterTable` skips empty DataFrames, causing SQL to fail on missing tables. All FSDs handle this consistently by documenting the risk and staying Tier 1, deferring to resolution if needed. This is a systemic framework limitation that might warrant a documented pattern in the KNOWN_ANTI_PATTERNS.md or a framework-level fix.

2. **Row ordering consistency:** Multiple jobs note potential row ordering mismatches between V1's dictionary insertion order and V2's SQL ORDER BY (or lack thereof). This is consistently handled across all FSDs -- documented as a risk, deferred to Proofmark validation. Reasonable approach.

3. **BRD correction pattern:** The customer_value_score FSD identifies a BRD error (rounding precision). This is exactly the kind of issue the reviewer step is meant to catch. The correction should flow uphill per the BLUEPRINT's "changes flow uphill" principle.

4. **Trailer timestamp non-determinism:** The daily_transaction_volume job has a known non-deterministic trailer timestamp that the FSD acknowledges but does not fully resolve in its Proofmark config design. This should be addressed more proactively.
