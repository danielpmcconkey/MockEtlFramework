# FSD Review -- Batch 06

## Summary
- Jobs reviewed: 10
- PASS: 9
- FAIL: 1

---

## Per-Job Reviews

### do_not_contact_list
**Verdict: PASS**
- Traceability: PASS -- All BRD requirements (BR-1 through BR-7) and all identified anti-patterns (AP3, AP6, AP4) and wrinkles (W1) are addressed in the FSD. The traceability matrix in Section 9 maps every BRD requirement to a specific FSD design decision with evidence.
- Module Chain: PASS -- Tier 1 is well justified. The Sunday skip (W1) is expressible via `strftime('%w', ...)`, the "all opted out" check maps to `GROUP BY / HAVING`, and the customer lookup is a standard `INNER JOIN`. No procedural logic is needed.
- SQL/Logic: PASS -- The SQL correctly implements: (1) Sunday skip via `WHERE strftime('%w', cp.as_of) != '0'`, (2) all-opted-out check via `HAVING COUNT(*) = SUM(CASE WHEN cp.opted_in = 0 THEN 1 ELSE 0 END)` with `COUNT(*) > 0`, (3) customer existence via `INNER JOIN`, (4) NULL coalescing via `COALESCE`, (5) as_of via `MIN(cp.as_of)`. Business rules match BRD.
- Writer Config: PASS -- CsvFileWriter with `includeHeader: true`, `trailerFormat: "TRAILER|{row_count}|{date}"`, `writeMode: Overwrite`, `lineEnding: LF`. Matches V1 exactly (path change only).
- Proofmark Config: PASS -- `reader: csv`, `header_rows: 1`, `trailer_rows: 1` (Overwrite + trailerFormat = 1). No exclusions/fuzzy justified. Correct per BLUEPRINT rules.

Notes: Thorough FSD. The AP4 elimination (removing `preference_id` and `preference_type`) is a nice touch. The row ordering discussion (ORDER BY customer_id) is well-reasoned.

---

### dormant_account_detection
**Verdict: PASS**
- Traceability: PASS -- All BRD requirements (BR-1 through BR-12) are addressed. W2 (weekend fallback) is documented and reproduced. AP3, AP4, AP6 are identified and eliminated. The traceability matrix covers every requirement.
- Module Chain: PASS -- Tier 1 justified. Weekend fallback via SQLite `strftime/date` functions, dormancy detection via LEFT JOIN anti-pattern, customer lookup via LEFT JOIN with MAX(as_of). All standard SQL operations. The FSD proactively documents the type risk (int vs long, decimal vs double through SQLite) and provides a clear escalation path if Phase D reveals issues.
- SQL/Logic: PASS -- The CTE structure is clean: (1) `target` CTE computes weekend-adjusted date, (2) `active_accounts` CTE finds accounts with transactions on target date, (3) `customer_lookup` CTE implements last-write-wins via MAX(as_of), (4) final SELECT uses anti-join pattern. The as_of override to target_date string matches BR-10. Multi-date duplication preserved per BR-12.
- Writer Config: PASS -- ParquetFileWriter with `numParts: 1`, `writeMode: Overwrite`. Matches V1.
- Proofmark Config: PASS -- `reader: parquet`, `threshold: 100.0`. No exclusions/fuzzy, with documented risk for potential type mismatches and clear escalation path. Correct approach per best practices (start strict).

Notes: The type risk documentation (Section 4, Section 1) is excellent engineering. Proactively flagging that SQLite's type coercion could cause Parquet column type differences (int? vs long?, decimal? vs double?) and documenting the Tier 2 escalation path is exactly what a good FSD should do. The weekend fallback SQL logic using `strftime('%w', MAX(a.as_of))` from the accounts table is correct -- since DataSourcing filters to the executor's effective date range, `MAX(a.as_of)` equals `__maxEffectiveDate`.

---

### email_opt_in_rate
**Verdict: PASS**
- Traceability: PASS -- All BRD requirements (BR-1 through BR-7) are addressed. W4 (integer division) is documented and reproduced. AP1 (phone_numbers dead-end) and AP4 (preference_id unused) are identified and eliminated. Traceability matrix is comprehensive.
- Module Chain: PASS -- Tier 1 is correct. V1 already uses the exact same Tier 1 pattern (DataSourcing -> Transformation -> ParquetFileWriter). No External module existed in V1. The only changes are removing the dead-end phone_numbers source and trimming unused columns.
- SQL/Logic: PASS -- The SQL is functionally identical to V1's SQL. The W4 integer division (`CAST(SUM(...) AS INTEGER) / CAST(COUNT(*) AS INTEGER)`) is preserved and documented. JOINs, WHERE, GROUP BY all match BRD specifications.
- Writer Config: PASS -- ParquetFileWriter with `numParts: 1`, `writeMode: Overwrite`. Matches V1.
- Proofmark Config: PASS -- `reader: parquet`, `threshold: 100.0`. No exclusions/fuzzy justified -- all columns are deterministic integers or strings.

Notes: Clean and straightforward. The W4 handling discussion (Section 3) is well-reasoned -- keeping the same SQL expression is indeed the cleanest approach since SQLite natively replicates the truncation behavior.

---

### executive_dashboard
**Verdict: PASS**
- Traceability: PASS -- All BRD requirements (BR-1 through BR-10) are addressed. W5 (banker's rounding) and W9 (overwrite) are documented and reproduced. AP1 (branches/segments dead-end), AP4 (unused columns), AP6 (row-by-row iteration) are identified and eliminated. The traceability matrix maps every requirement.
- Module Chain: PASS -- Tier 2 is justified. The FSD provides three specific reasons why Tier 1 is insufficient: (1) guard clause requires producing an empty DataFrame with correct column schema when tables are empty, but Transformation's RegisterTable skips empty DataFrames causing SQL errors, (2) as_of fallback logic (customer -> transaction) is awkward in SQL, (3) vertical pivot combined with guard clause makes SQL brittle. The External module handles ONLY these operations while DataSourcing pulls all data. This is a legitimate scalpel use.
- SQL/Logic: PASS -- N/A for SQL (no Transformation module). The External module algorithm (Section 10) correctly implements: guard clause on three DataFrames, as_of resolution with fallback, 9 metrics in fixed order, LINQ-based aggregation (AP6 elimination), and banker's rounding with explicit `MidpointRounding.ToEven`.
- Writer Config: PASS -- CsvFileWriter with `includeHeader: true`, `trailerFormat: "SUMMARY|{row_count}|{date}|{timestamp}"`, `writeMode: Overwrite`, `lineEnding: LF`. Matches V1 exactly.
- Proofmark Config: PASS -- `reader: csv`, `header_rows: 1`, `trailer_rows: 1`. The non-deterministic `{timestamp}` token in the trailer is handled by `trailer_rows: 1` which strips the trailer from comparison entirely. Smart approach -- no column exclusion needed. No fuzzy columns needed since V1 uses decimal throughout (no W6).

Notes: The Tier 2 justification is solid. The AP1 elimination (removing branches and segments DataSourcing) and AP4 elimination (trimming to only used columns across all 5 tables) are thorough. The metric computation detail in Section 10 is precise.

---

### fee_revenue_daily
**Verdict: PASS**
- Traceability: PASS -- All BRD requirements (BR-1 through BR-8) and edge cases (EC-1 through EC-5) are addressed. W3b (end-of-month boundary), W6 (double epsilon), and W9 (overwrite) are documented and reproduced. AP3, AP4, AP6 are eliminated. AP10 is documented as intentionally retained (hardcoded dates required for EC-1). The FSD also corrects a BRD error (BR-2 says "prior business day" but V1 code filters to the current effective date).
- Module Chain: PASS -- Tier 2 is justified. The Transformation module cannot access scalar shared-state values (`__maxEffectiveDate`), and the SQL needs this date for daily row filtering and last-day-of-month detection. A minimal External module materializes the date into a one-row DataFrame. All business logic remains in SQL. Clean scalpel use.
- SQL/Logic: PASS -- The SQL uses UNION ALL to produce the daily row and conditional MONTHLY_TOTAL row. The daily aggregation correctly filters `WHERE oe.as_of = edr.effective_date`. The monthly total correctly scans ALL rows (no as_of filter on oe) to replicate EC-1. The last-day-of-month detection via `date(edr.effective_date, 'start of month', '+1 month', '-1 day')` is correct. The EXISTS subquery correctly suppresses the MONTHLY_TOTAL when no data exists for the effective date (matching BR-8). W6 is naturally reproduced through SQLite REAL type.
- Writer Config: PASS -- CsvFileWriter with `includeHeader: true`, `writeMode: Overwrite`, `lineEnding: LF`, no trailer. Matches V1.
- Proofmark Config: PASS -- `reader: csv`, `header_rows: 1`, `trailer_rows: 0`. No fuzzy columns initially, with documented fallback for W6 double-precision differences. Correct strict-first approach.

Notes: The BRD correction in Section 9 is commendable -- the FSD identifies that BR-2 is inaccurate and documents the actual V1 behavior. The W6 analysis (Section 4, SQL Design Note 1) explaining how decimal->REAL mapping naturally reproduces double-precision accumulation is insightful. The AP10 retention is well-justified: the hardcoded date range is required for EC-1 output equivalence.

---

### fee_waiver_analysis
**Verdict: PASS**
- Traceability: PASS -- All BRD requirements (BR-1 through BR-8) are addressed. W9 is documented and reproduced. AP1 (dead-end accounts table) and AP4 (unused columns) are identified and eliminated. The critical design decision section on removing the dead-end LEFT JOIN is well-documented with risk analysis.
- Module Chain: PASS -- Tier 1 is correct. V1 already uses Tier 1 (DataSourcing -> Transformation -> CsvFileWriter) with no External module. The only structural change is removing the dead-end accounts DataSourcing entry and the associated LEFT JOIN from the SQL.
- SQL/Logic: PASS -- The SQL correctly implements: GROUP BY fee_waived and as_of, NULL coalescing via CASE WHEN, ROUND(SUM(...), 2) for total_fees, ROUND(AVG(...), 2) for avg_fee, ORDER BY fee_waived. The dead-end LEFT JOIN removal is justified -- since accounts has unique (account_id, as_of) pairs, the LEFT JOIN was 1:1 and removing it doesn't change results.
- Writer Config: PASS -- CsvFileWriter with `includeHeader: true`, `writeMode: Overwrite`, `lineEnding: LF`, no trailer. Matches V1.
- Proofmark Config: PASS -- `reader: csv`, `header_rows: 1`, `trailer_rows: 0`. No exclusions/fuzzy justified. Both V1 and V2 execute the same SQL through SQLite, so arithmetic paths are identical.

Notes: The dead-end JOIN removal analysis (Section 3) is thorough. The FSD correctly notes that if accounts has duplicate (account_id, as_of) pairs, this change could affect output, and flags it as the first hypothesis if Proofmark fails. Good defensive documentation.

---

### fund_allocation_breakdown
**Verdict: PASS**
- Traceability: PASS -- All BRD requirements (BR-1 through BR-13) are addressed. W8 (stale trailer date) and W9 (overwrite) are documented and reproduced. AP1 (investments dead-end), AP3 (unnecessary External), AP4 (unused columns), AP6 (row-by-row iteration) are all identified and eliminated.
- Module Chain: PASS -- Tier 1 is justified. The V1 External module's operations (JOIN, GROUP BY, COUNT, SUM, ROUND, ORDER BY, CSV writing) are all standard SQL operations, and the trailer with hardcoded stale date can be replicated via CsvFileWriter's `trailerFormat` with a literal `2024-10-01` string.
- SQL/Logic: PASS -- The SQL correctly implements: LEFT JOIN for security type lookup with COALESCE to 'Unknown', GROUP BY COALESCE(s.security_type, 'Unknown'), COUNT(*) for holding_count, ROUND(SUM(h.current_value), 2) for total_value, CASE WHEN division guard for avg_value, MAX(h.as_of) for as_of, ORDER BY security_type. The `* 1.0` in the avg_value division ensures real-valued division rather than integer division.
- Writer Config: PASS -- CsvFileWriter with `includeHeader: true`, `trailerFormat: "TRAILER|{row_count}|2024-10-01"`, `writeMode: Overwrite`, `lineEnding: LF`. The hardcoded `2024-10-01` in the trailerFormat string correctly replicates W8 (stale trailer date). The `{row_count}` token will be substituted with the DataFrame row count (number of grouped security types), matching BR-8.
- Proofmark Config: PASS -- `reader: csv`, `header_rows: 1`, `trailer_rows: 1`. No exclusions/fuzzy initially, with documented risk for SQLite ROUND vs C# Math.Round rounding differences.

Notes: The risk register in the Appendix is a nice touch. The W8 replication approach (hardcoding `2024-10-01` directly in the trailerFormat string rather than using `{date}`) is clever and correct. The SQLite ROUND vs Math.Round rounding risk analysis is well-documented. One minor note: the FSD mentions the empty input edge case could differ (V1 writes no file, V2 writes header+trailer), but correctly assesses this as unlikely for the test data range and documents the mitigation.

---

### high_balance_accounts
**Verdict: PASS**
- Traceability: PASS -- All BRD requirements (BR-1 through BR-6) are addressed. AP3, AP4, AP6 are identified and eliminated. The FSD also corrects a BRD error: BR-6 states as_of comes from `__maxEffectiveDate` but the actual V1 code uses the account row's as_of. The correction is documented and justified (in single-day execution, both values are identical).
- Module Chain: PASS -- Tier 1 is well justified. Balance filtering (`WHERE > 10000`) and customer name lookup (`LEFT JOIN + COALESCE`) are trivial SQL operations. No procedural logic needed.
- SQL/Logic: PASS -- The SQL correctly implements: LEFT JOIN for customer names with COALESCE to empty string, WHERE CAST(a.current_balance AS REAL) > 10000 for the strictly-greater-than threshold, column selection matching V1's output schema (account_id, customer_id, account_type, current_balance, first_name, last_name, as_of). No ORDER BY matches V1's lack of explicit sorting.
- Writer Config: PASS -- CsvFileWriter with `includeHeader: true`, `writeMode: Overwrite`, `lineEnding: LF`, no trailer. Matches V1.
- Proofmark Config: PASS -- `reader: csv`, `header_rows: 1`, `trailer_rows: 0`. No exclusions/fuzzy justified -- all values are pass-through, no arithmetic.

Notes: The BR-6 correction (Section 4 note) is good -- catching that the BRD incorrectly describes the as_of source. The Appendix on BR-4 edge case analysis (empty customers table causing SQLite table registration failure) is thorough. The FSD correctly identifies this as a theoretical divergence point unlikely to manifest with the actual test data.

---

### high_risk_merchant_activity
**Verdict: PASS**
- Traceability: PASS -- All BRD requirements (BR-1 through BR-11) are addressed. AP3, AP4, AP6, AP7 are identified and eliminated. W9 is documented and reproduced. The traceability matrix is comprehensive with detailed evidence links.
- Module Chain: PASS -- Tier 1 is justified. The V1 External module performs a dictionary lookup (equivalent to JOIN) and a single filter on risk_level -- trivially expressible in SQL. No procedural logic needed.
- SQL/Logic: PASS -- The SQL correctly implements: INNER JOIN for MCC lookup (replicating V1's dictionary-based exclusion of unknown MCCs), WHERE risk_level = 'High' filter, column selection with merchant_category_code renamed to mcc_code, pass-through of amount without rounding. The as_of join condition (`ct.as_of = mc.as_of`) is well-justified in the design notes and risk analysis.
- Writer Config: PASS -- CsvFileWriter with `includeHeader: true`, `writeMode: Overwrite`, `lineEnding: LF`, no trailer. Matches V1.
- Proofmark Config: PASS -- `reader: csv`, `header_rows: 1`, `trailer_rows: 0`. No exclusions/fuzzy justified -- all fields are deterministic pass-throughs. The empty output analysis (both V1 and V2 produce header-only files) confirms trivial Proofmark pass.

Notes: The appendices are thorough. Appendix B's empty output analysis is helpful -- confirming that both V1 and V2 will produce header-only files because high-risk MCC codes don't appear in card_transactions. Appendix C's as_of JOIN condition risk analysis is well-reasoned. The AP7 handling (documenting the "High" magic value with a SQL comment) is correct -- while the SQL still uses the literal string, it's clearly documented rather than being an unexplained magic value.

---

### holdings_by_sector
**Verdict: FAIL**
- Traceability: PASS -- All BRD requirements (BR-1 through BR-10) are addressed. W7 (inflated trailer count) and W9 (overwrite) are documented and reproduced. AP4 and AP6 are eliminated. AP3 is partially addressed (business logic moved to SQL, External handles only W7 file write).
- Module Chain: PASS -- Tier 2 is justified. CsvFileWriter's `{row_count}` token substitutes with the output DataFrame's row count (grouped sectors, e.g., 8), but V1's trailer requires the INPUT holdings row count (e.g., 1303). There is no mechanism to override `{row_count}` with a custom value. The External module is a legitimate scalpel for W7 replication.
- SQL/Logic: PASS -- The SQL correctly implements: LEFT JOIN for sector lookup with COALESCE to 'Unknown', GROUP BY with COUNT and ROUND(SUM(...), 2), subquery for as_of, ORDER BY sector.
- Writer Config: PASS -- The External module handles file writing directly, matching V1's output format (header, data rows, trailer with inflated count, LF line endings, Overwrite mode).
- Proofmark Config: **FAIL** -- The Proofmark config specifies `trailer_rows: 1`, but the External module writes the CSV file directly (bypassing CsvFileWriter). Because the External module is the last module in the chain and writes the file itself, the framework's CsvFileWriter is not used. The config assumes the trailer will be at the end of the file as a single line, which is correct for the External module's Overwrite output. However, the fundamental issue is that the FSD uses `trailer_rows: 1` to strip the trailer from comparison, which is correct because the trailer contains the W7 inflated count that would be the same between V1 and V2 (since both use the input holdings count). **On closer analysis, this is actually correct** -- the trailer IS at the end of the file in both V1 and V2 (both use Overwrite mode), and `trailer_rows: 1` correctly strips it. The Proofmark comparison would strip the trailer from both files before comparing data rows.

**CORRECTION -- Re-evaluation:** Let me reconsider. The real issue with this FSD is the **ROUND() rounding mode discrepancy**. The FSD states in Section 5, SQL Design Note 4: "SQLite's `ROUND(x, 2)` uses banker's rounding (round half to even). V1 uses `Math.Round(totalValue, 2)` which defaults to `MidpointRounding.ToEven` (banker's rounding). These produce identical results." But this is INCORRECT. **SQLite's ROUND() uses round-half-away-from-zero, NOT banker's rounding.** The CONFIG_GUIDE examples and general SQLite documentation confirm this. This is the same rounding difference correctly identified in the fund_allocation_breakdown FSD (Section 8, Proofmark Risk). The holdings_by_sector FSD incorrectly states they use the same rounding mode, whereas they use different modes. While the practical risk is low (aggregated sums rarely land on exact 0.005 midpoints), the FSD contains an incorrect technical claim about SQLite ROUND behavior.

**Revised Verdict: FAIL**
- Traceability: PASS
- Module Chain: PASS
- SQL/Logic: **FAIL** -- The FSD contains an incorrect statement about SQLite ROUND behavior. Section 5, SQL Design Note 4 states: "SQLite's `ROUND(x, 2)` uses banker's rounding (round half to even)." This is wrong. SQLite's ROUND uses round-half-away-from-zero. The fund_allocation_breakdown FSD (same batch) correctly identifies this difference. While the output impact is likely negligible for this dataset, the FSD should document this as a potential risk (as fund_allocation_breakdown does) rather than incorrectly claiming the rounding modes are identical.
- Writer Config: PASS
- Proofmark Config: PASS

The fix required is straightforward: correct the SQL Design Note 4 to acknowledge that SQLite ROUND uses round-half-away-from-zero (not banker's rounding), note the theoretical divergence risk for values at exact 0.005 midpoints, and document the Proofmark fuzzy fallback strategy (as fund_allocation_breakdown does). This does not require changing the SQL or the implementation -- just correcting the documentation of the rounding behavior.

---

## Cross-Job Observations

1. **Consistent quality**: 9 of 10 FSDs demonstrate thorough analysis, clean tier justification, and comprehensive traceability. The batch shows a strong understanding of the framework architecture and anti-pattern taxonomy.

2. **BRD corrections**: Two FSDs (fee_revenue_daily, high_balance_accounts) proactively identify and correct BRD errors. This is the right behavior -- FSDs should trace back to source code, not blindly trust the BRD.

3. **Type risk documentation**: The dormant_account_detection and fund_allocation_breakdown FSDs both proactively document SQLite type coercion risks (int->long, decimal->double) and provide clear escalation paths. This is excellent defensive engineering.

4. **SQLite ROUND inconsistency**: The fund_allocation_breakdown FSD correctly identifies the SQLite ROUND vs C# Math.Round rounding difference, while the holdings_by_sector FSD incorrectly states they use the same mode. This inconsistency across the batch suggests the architect may have been uncertain about SQLite's rounding behavior.

5. **W7 handling**: The holdings_by_sector FSD's Tier 2 justification for W7 (inflated trailer count) is solid -- CsvFileWriter's `{row_count}` token genuinely cannot be overridden with a custom value, making the External module necessary.

6. **Dead-end source elimination**: Multiple FSDs (email_opt_in_rate, executive_dashboard, fee_waiver_analysis, fund_allocation_breakdown) correctly identify and eliminate AP1 dead-end sources. Good pattern recognition.
