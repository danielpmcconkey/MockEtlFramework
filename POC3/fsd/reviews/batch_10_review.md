# FSD Review -- Batch 10

## Summary
- Jobs reviewed: 11
- PASS: 8
- FAIL: 3

---

## Per-Job Reviews

### sms_opt_in_rate
**Verdict: PASS**
- Traceability: PASS -- All BRD requirements (BR-1 through BR-6) and anti-pattern codes (W4, AP4) are explicitly addressed in the FSD with evidence citations. The traceability matrix in Appendix is thorough.
- Module Chain: PASS -- Tier 1 is correct. V1 is already framework-only (DataSourcing + Transformation + ParquetFileWriter). No External module needed. All logic is standard SQL.
- SQL/Logic: PASS -- The SQL faithfully replicates V1's integer division (W4), MARKETING_SMS filter (BR-1), INNER JOIN semantics (BR-6), and GROUP BY segment_name, as_of (BR-5). The CASE WHEN + CAST integer division expression matches V1 exactly.
- Writer Config: PASS -- ParquetFileWriter with numParts=1, writeMode=Overwrite matches V1. Output path correctly uses V2 convention.
- Proofmark Config: PASS -- reader=parquet, threshold=100.0, no exclusions or fuzzy columns. BRD confirms no non-deterministic fields. Appropriate.

### suspicious_wire_flags
**Verdict: PASS**
- Traceability: PASS -- All BRD requirements (BR-1 through BR-11) are addressed. AP1 (dead-end accounts and customers), AP3 (unnecessary External), AP4 (counterparty_bank), AP6 (row-by-row), AP7 (magic values) all addressed with elimination strategies.
- Module Chain: PASS -- Tier 1 is justified. V1's External module does simple if/else-if filtering trivially expressible in SQL CASE WHEN. No procedural logic needed.
- SQL/Logic: PASS -- The FSD initially uses LIKE for case-sensitive matching, then self-corrects to INSTR() for proper case-sensitive behavior matching V1's String.Contains. The CASE WHEN priority replicates the if/else-if mutual exclusivity (BR-2, BR-11). COALESCE for NULL counterparty_name (BR-8). WHERE clause mirrors flag logic to exclude non-matching rows (BR-3). The self-correction is a positive signal of thorough analysis.
- Writer Config: PASS -- ParquetFileWriter with numParts=1, writeMode=Overwrite matches V1.
- Proofmark Config: PASS -- reader=parquet, threshold=100.0, no exclusions. Open question about CAST(amount AS REAL) vs Convert.ToDecimal type difference is noted but appropriately deferred to implementation verification.

### top_branches
**Verdict: PASS**
- Traceability: PASS -- All BRD requirements (BR-1 through BR-8) addressed. AP4 (visit_id unused) eliminated. OQ-1 and OQ-2 from BRD resolved with clear reasoning.
- Module Chain: PASS -- Tier 1 correct. V1 is already framework-only. SQL with CTE, RANK(), and JOIN is standard SQLite.
- SQL/Logic: PASS -- CTE structure preserved. Hardcoded date filter '2024-10-01' preserved for output equivalence (BR-1). RANK() window function preserved (BR-3). ORDER BY rank, vt.branch_id preserved (BR-4). Non-date-aligned join behavior analyzed and correctly identified as benign in single-day runs (BR-5, OQ-1).
- Writer Config: PASS -- CsvFileWriter with includeHeader=true, trailerFormat with CONTROL|{date}|{row_count}|{timestamp}, writeMode=Overwrite, lineEnding=LF all match V1.
- Proofmark Config: PASS -- reader=csv, header_rows=1, trailer_rows=1, threshold=100.0. The {timestamp} in the trailer is correctly handled by trailer_rows=1 stripping.

### top_holdings_by_value
**Verdict: PASS**
- Traceability: PASS -- All BRD requirements (BR-1 through BR-11) addressed. AP4 (4 unused columns from holdings, 1 from securities) and AP8 (unused_cte) eliminated. W10 (numParts=50) preserved.
- Module Chain: PASS -- Tier 1 correct. V1 is already framework-only. CTEs, ROW_NUMBER(), CASE WHEN are all standard SQL.
- SQL/Logic: PASS -- unused_cte correctly removed (AP8). Remaining CTEs (security_totals, ranked) preserved verbatim. ROW_NUMBER() ranking, CASE tier classification, WHERE rank <= 20, ORDER BY rank all match V1. Cross-date ranking behavior (no PARTITION BY as_of) correctly documented and preserved.
- Writer Config: PASS -- ParquetFileWriter with numParts=50 (W10 preserved), writeMode=Overwrite.
- Proofmark Config: PASS -- reader=parquet, threshold=100.0, no exclusions. ROW_NUMBER() tie-breaking risk documented with escalation path. Starting strict is the correct approach.

### transaction_anomaly_flags
**Verdict: PASS**
- Traceability: PASS -- All BRD requirements (BR-1 through BR-13) exhaustively traced. AP1 (dead-end customers), AP4 (txn_type), AP6 (partial -- row-by-row collection improved with LINQ), AP7 (named constants) addressed. W5 (banker's rounding) and BR-8 (mixed decimal/double precision) replicated.
- Module Chain: PASS -- Tier 2 is well justified. SQLite lacks SQRT(), and the mixed-precision decimal/double computation path (BR-8) must be replicated exactly in C# to match V1's IEEE 754 artifacts. Tier 1 is genuinely insufficient. Tier 3 is unnecessary because DataSourcing handles the data access.
- SQL/Logic: PASS (N/A for SQL -- External module logic). The External module pseudocode replicates V1's exact computation path: decimal average, double-space variance, Math.Sqrt back to decimal, Math.Round with MidpointRounding.ToEven. All business rules faithfully replicated.
- Writer Config: PASS -- CsvFileWriter with includeHeader=true, writeMode=Overwrite, lineEnding=LF, no trailer. All match V1.
- Proofmark Config: PASS -- reader=csv, header_rows=1, trailer_rows=0, threshold=100.0. Starting strict is correct since V2 replicates the exact C# computation path.

### transaction_category_summary
**Verdict: PASS**
- Traceability: PASS -- All BRD requirements (BR-1 through BR-7) addressed. AP1 (dead-end segments), AP4 (transaction_id and account_id), AP8 (vestigial CTE with ROW_NUMBER/COUNT OVER) all eliminated.
- Module Chain: PASS -- Tier 1 correct. V1 is already framework-only. The CTE simplification removes computational waste without affecting output.
- SQL/Logic: PASS -- Simplified SQL directly queries transactions with identical GROUP BY, aggregation functions (ROUND(SUM, 2), COUNT(*), ROUND(AVG, 2)), and ORDER BY as_of, txn_type. The analysis that the CTE's window functions are unused and removal is safe is correct.
- Writer Config: PASS -- CsvFileWriter with includeHeader=true, trailerFormat="END|{row_count}", writeMode=Append, lineEnding=LF all match V1. The Append behavior (header only on first run, trailer on every run) is correctly documented per CsvFileWriter.cs source.
- Proofmark Config: PASS -- reader=csv, header_rows=1, trailer_rows=0. The trailer_rows=0 is correct for an Append-mode file with embedded trailers (per CONFIG_GUIDE.md).

### transaction_size_buckets
**Verdict: PASS**
- Traceability: PASS -- All BRD requirements (BR-1 through BR-9) and edge cases addressed. AP1 (dead-end accounts), AP4 (transaction_id, txn_type), AP8 (three unnecessary CTEs with unused ROW_NUMBER) eliminated.
- Module Chain: PASS -- Tier 1 correct. V1 is already framework-only. CTE collapse into a single flat query is a clean AP8 elimination.
- SQL/Logic: PASS -- CASE WHEN bucket boundaries preserved exactly (same operators, thresholds, labels, ELSE clause). GROUP BY on the CASE expression with identical aggregation (COUNT(*), ROUND(SUM, 2), ROUND(AVG, 2)). ORDER BY as_of, amount_bucket preserves the string sort behavior (BR-6, Edge Case 4).
- Writer Config: PASS -- CsvFileWriter with includeHeader=true, no trailer, writeMode=Overwrite, lineEnding=LF.
- Proofmark Config: PASS -- reader=csv, header_rows=1, trailer_rows=0, threshold=100.0.
- Note: The FSD includes `account_id` in V2 DataSourcing columns even though it is unused by the V2 SQL (Open Question 1). This is harmless -- it does not affect output -- but is a minor AP4 inconsistency. Not a FAIL since it has zero impact on output correctness.

### wealth_tier_analysis
**Verdict: FAIL**
- Traceability: FAIL -- **The FSD identifies a critical discrepancy in BRD BR-2: the BRD states the Bronze threshold is `< $25,000` but V1 source code uses `< 10000m`.** The FSD correctly follows the V1 code (using 10000), but this means the BRD is wrong. While the FSD is doing the right thing by following the code, this is a material traceability issue -- the BRD and FSD disagree on a core business rule threshold. The FSD should have been blocked pending BRD correction rather than proceeding with the discrepancy documented as OQ-3.

  Additionally, **the FSD identifies a second BRD error in BR-6: the BRD states `pct_of_customers` uses `MidpointRounding.AwayFromZero` but V1 code uses `MidpointRounding.ToEven`.** Again, the FSD follows the code correctly, but two BRD errors in one job is a red flag.

- Module Chain: PASS -- Tier 1 is well justified. V1's External module is a textbook AP3 case: summing, CASE/WHEN classification, GROUP BY aggregation, and percentage computation are all SQL-expressible. The SQL uses CTEs with UNION ALL for wealth calculation, CASE WHEN for tiers, all_tiers for guaranteed 4-row output, and a customers_guard for the empty check -- all valid SQLite patterns.
- SQL/Logic: PASS (conditional on correct thresholds) -- The SQL logic is sound: UNION ALL for combining accounts+investments wealth, CASE WHEN for tier classification, LEFT JOIN against all_tiers for guaranteed output, COALESCE for zero-customer tiers. The as_of derivation from MAX(as_of) is flagged as OQ-2 -- a legitimate risk if accounts is empty but investments isn't.
- Writer Config: PASS -- CsvFileWriter with includeHeader=true, trailerFormat="TRAILER|{row_count}|{date}", writeMode=Overwrite, lineEnding=LF match V1.
- Proofmark Config: PASS -- reader=csv, header_rows=1, trailer_rows=1, threshold=100.0.

**FAIL reason: Two material BRD/FSD discrepancies on business rule thresholds (BR-2 Bronze threshold: BRD says 25000, FSD uses 10000) and rounding mode (BR-6: BRD says AwayFromZero, FSD uses ToEven). The FSD must not proceed to implementation until these BRD errors are formally corrected. The FSD's decisions to follow V1 code are correct, but the BRD needs amendment first to maintain traceability integrity.**

### weekend_transaction_pattern
**Verdict: FAIL**
- Traceability: PASS -- All BRD requirements (BR-1 through BR-11) addressed. AP3 (mostly eliminated -- External reduced to date injector), AP4 (txn_timestamp, txn_type, transaction_id, account_id removed), AP6 (foreach replaced with SQL), AP10 (retained with documentation).
- Module Chain: PASS -- Tier 2 is justified. The FSD goes through a thorough design journey arriving at the correct conclusion: pure Tier 1 is insufficient because the SQL Transformation module cannot access `__maxEffectiveDate` from shared state, and the DataSourcing must source 7 days of data for Sunday weekly summaries while the SQL needs to know which specific day is "today." The minimal External module injecting the effective date as a DataFrame is the right SCALPEL approach.
- SQL/Logic: FAIL -- **The SQL has a significant correctness concern with rounding mode divergence.** V1 uses `Math.Round(decimal, 2)` which defaults to `MidpointRounding.ToEven` (banker's rounding). The FSD's SQL uses SQLite `ROUND()` which uses arithmetic rounding (round half away from zero). The FSD acknowledges this in OQ-1 but proceeds with the SQL approach anyway, noting it will be fixed "if Proofmark comparison reveals a discrepancy." While the probability of hitting an exact midpoint may be low, this is a known precision mismatch between the V1 and V2 computation paths that the FSD itself identifies but does not resolve.

  Additionally, the SQL is quite complex (300+ lines of CTEs with repeated COALESCE/subquery patterns for ensuring both Weekday and Weekend rows appear). While functionally correct, the complexity introduces risk of subtle bugs.

- Writer Config: PASS -- CsvFileWriter with includeHeader=true, trailerFormat="TRAILER|{row_count}|{date}", writeMode=Overwrite, lineEnding=LF match V1.
- Proofmark Config: PASS -- reader=csv, header_rows=1, trailer_rows=1, threshold=100.0.

**FAIL reason: The FSD identifies a known rounding mode divergence (V1 banker's rounding via Math.Round vs V2 SQLite arithmetic rounding) but does not resolve it, deferring to "run and see." Since the External module is already part of the chain (for date injection), the rounding should be moved to the External module using Math.Round with MidpointRounding.ToEven, or the FSD should explicitly document why the divergence is acceptable (e.g., prove no midpoints exist in the data). Proceeding with a known potential mismatch violates the output equivalence mandate.**

### wire_direction_summary
**Verdict: FAIL**
- Traceability: PASS -- All BRD requirements (BR-1 through BR-9) addressed. W7 (inflated trailer count) correctly identified as the key wrinkle requiring an External module. AP3 (partially eliminated -- business logic moved to SQL), AP4 (3 unused columns removed), AP6 (row-by-row replaced with SQL GROUP BY).
- Module Chain: PASS -- Tier 2 is justified. The CsvFileWriter cannot produce the W7 inflated trailer count (it hardcodes `{row_count}` to df.Count on the output DataFrame). The External module is correctly scoped to ONLY file writing -- zero business logic.
- SQL/Logic: FAIL -- **Same rounding mode divergence as weekend_transaction_pattern.** V1 uses C# `Math.Round(decimal, 2)` which defaults to `MidpointRounding.ToEven` (banker's rounding). V2 SQL uses SQLite `ROUND()` which uses arithmetic rounding. The FSD explicitly acknowledges this in Section 4 Design Note on W5 and Open Question 1, but defers resolution to "run and see."

  Since the External module is already in the pipeline (for W7 CSV writing), the rounding could trivially be moved into the External module: read the raw SUM and COUNT from the SQL DataFrame, compute `Math.Round(total, 2, MidpointRounding.ToEven)` and `Math.Round(total/count, 2, MidpointRounding.ToEven)` in C#, then write. This would eliminate the rounding concern entirely with minimal additional complexity.

  Additionally, **Open Question 3 identifies a potential row ordering mismatch** between V1 (Dictionary iteration order, which may not be alphabetical) and V2 (ORDER BY direction, which is alphabetical). This is a known potential output difference that is not resolved.

- Writer Config: PASS -- The External module's file writing config (LF line endings, Overwrite, header, trailer with input count, no RFC 4180 quoting) correctly matches V1.
- Proofmark Config: PASS -- reader=csv, header_rows=1, trailer_rows=1, threshold=100.0. Starting strict is correct.

**FAIL reason: (1) Known rounding mode divergence between V1 (banker's rounding) and V2 (SQLite arithmetic rounding) with no resolution, despite the External module being available as a natural place to apply C# rounding. (2) Known potential row ordering mismatch (V1 Dictionary order vs V2 ORDER BY direction) that is not resolved. Both are known potential output differences that should be addressed before implementation.**

### wire_transfer_daily
**Verdict: PASS**
- Traceability: PASS -- All BRD requirements (BR-1 through BR-9) addressed. AP1 (dead-end accounts), AP3 (unnecessary External), AP4 (5 unused columns), AP6 (foreach replaced with GROUP BY) all eliminated. W3b (month-end MONTHLY_TOTAL) replicated via UNION ALL with HAVING clause.
- Module Chain: PASS -- Tier 1 is justified. The GROUP BY aggregation and conditional MONTHLY_TOTAL row are both expressible in SQL. The month-end detection using `strftime('%d', MAX(as_of), '+1 day') = '01'` is a clean SQLite equivalent of V1's DateTime.DaysInMonth check. No External module needed.
- SQL/Logic: PASS -- The UNION ALL structure correctly separates daily aggregation from the conditional MONTHLY_TOTAL row. The HAVING clause properly gates the summary row on both non-empty input (COUNT(*) > 0) and month-end (strftime check). The self-correction from WHERE to HAVING for the aggregate expression is documented and correct. The mixed-type wire_date column (date for daily, string for MONTHLY_TOTAL) matches V1. Rounding mode divergence (Math.Round ToEven vs SQLite ROUND) is noted as OQ-1 with appropriate risk assessment (LOW -- amounts have few decimal places, midpoints statistically unlikely).
- Writer Config: PASS -- ParquetFileWriter with numParts=1, writeMode=Overwrite matches V1.
- Proofmark Config: PASS -- reader=parquet, threshold=100.0, no exclusions. Contingency plan for fuzzy tolerance documented.
- Note: The rounding concern exists here too (same as wire_direction_summary), but the FSD correctly classifies it as LOW risk and the job uses Parquet output (not an External module for writing), making it impractical to move rounding to C# without escalating to Tier 2. The FSD's approach of starting strict and escalating if needed is the pragmatic choice here. The difference from wire_direction_summary (which I FAILed) is that wire_direction_summary already HAS an External module in the pipeline where rounding could trivially be fixed, while wire_transfer_daily would require adding one.

---

## Summary of Issues

### FAILed Jobs (3)

1. **wealth_tier_analysis** -- Two BRD errors identified (Bronze threshold 25000 vs code 10000, pct_of_customers rounding mode AwayFromZero vs code ToEven). BRD must be corrected before proceeding.

2. **weekend_transaction_pattern** -- Known rounding mode divergence (banker's vs arithmetic) not resolved despite External module being available in the chain to fix it.

3. **wire_direction_summary** -- Known rounding mode divergence not resolved despite External module being available. Row ordering mismatch not resolved.

### Common Theme

The rounding mode issue (V1 `Math.Round` with banker's rounding vs SQLite `ROUND` with arithmetic rounding) appears in multiple jobs but is only material when:
- The computation involves division that could produce midpoint values (averages, percentages)
- An External module is already in the V2 pipeline and could trivially apply the correct rounding

Jobs that use pure Tier 1 with no External module (like wire_transfer_daily) get a pass because fixing the rounding would require adding an External module, which contradicts the goal of Tier 1 simplicity. The correct approach for those is "start strict, escalate if needed."

But for jobs that already use Tier 2 (weekend_transaction_pattern, wire_direction_summary), leaving a known rounding divergence unresolved when the fix is trivial (move rounding to the existing External module) is an unnecessary risk.
