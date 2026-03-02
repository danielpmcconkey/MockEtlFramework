# FSD Review -- Batch 03

## Summary
- Jobs reviewed: 10
- PASS: 9
- FAIL: 1

---

## Per-Job Reviews

### card_type_distribution
**Verdict: PASS**
- Traceability: PASS -- All BRD requirements (BR-1 through BR-10) are addressed in the FSD. W6 (double epsilon) is properly identified and handled via SQLite REAL matching C# double. AP1 (dead card_transactions source), AP3 (unnecessary External), AP4 (unused columns card_id/customer_id/card_status), and AP6 (row-by-row iteration) are all documented and eliminated. The traceability matrix in Section 9 maps every FSD decision back to a BRD requirement with evidence.
- Module Chain: PASS -- Tier 1 is the correct choice. GROUP BY + COUNT + REAL division is textbook SQL. The justification for removing the External module is well-supported.
- SQL/Logic: PASS -- The SQL correctly implements: GROUP BY card_type, COUNT(*) for card_count, CAST(COUNT(*) AS REAL) / CAST(total AS REAL) for pct_of_total (fraction, not percentage), and (SELECT as_of FROM cards LIMIT 1) for as_of. The detailed SQL notes correctly address: integer division avoidance via CAST, fraction vs percentage distinction, scalar subquery for total, as_of from first row, and ORDER BY for determinism. Empty input risk is documented with a reasonable mitigation strategy.
- Writer Config: PASS -- CsvFileWriter with includeHeader=true, trailerFormat="TRAILER|{row_count}|{date}", writeMode=Overwrite, lineEnding=LF. All match V1 exactly. Output path correctly changed to double_secret_curated.
- Proofmark Config: PASS -- reader: csv, header_rows: 1, trailer_rows: 1, threshold: 100.0. No exclusions or fuzzy overrides, which is correct since all columns are deterministic. The contingency plan for potential row ordering and floating-point serialization differences is well-documented.

Notes: Thorough FSD with excellent risk analysis. The row ordering risk (Dictionary iteration vs ORDER BY) is correctly identified and documented with a clear resolution path. The empty-input edge case analysis is detailed and pragmatic.

---

### communication_channel_map
**Verdict: PASS**
- Traceability: PASS -- All BRD requirements (BR-1 through BR-7) are addressed. AP3 (unnecessary External), AP4 (unused preference_id, email_id, phone_id columns), AP5 (asymmetric NULL handling: email->"N/A", phone->""), and AP6 (row-by-row iteration) are identified and handled correctly. AP5 is properly reproduced in output via COALESCE with different defaults. The traceability matrix comprehensively maps each FSD decision to BRD requirements.
- Module Chain: PASS -- Tier 1 is well-justified. The FSD convincingly demonstrates that every V1 C# pattern (dictionary lookups, foreach iteration, if/else priority chain) maps to SQL constructs (LEFT JOIN, GROUP BY + CASE, CASE WHEN priority hierarchy, COALESCE). No procedural logic remains that cannot be expressed in SQL.
- SQL/Logic: PASS -- The SQL correctly implements: priority hierarchy via nested CASE (MARKETING_EMAIL > MARKETING_SMS > PUSH_NOTIFICATIONS > None), opted_in=1 guard (matching boolean-to-integer conversion in ToSqliteValue), last-wins email/phone via GROUP BY HAVING MAX(rowid), asymmetric COALESCE defaults, and customer-driven output via FROM customers c with LEFT JOINs. The cross-date preference accumulation behavior is correctly analyzed.
- Writer Config: PASS -- CsvFileWriter with includeHeader=true, no trailer, writeMode=Overwrite, lineEnding=LF. All match V1 exactly.
- Proofmark Config: PASS -- reader: csv, header_rows: 1, trailer_rows: 0, threshold: 100.0. Starting strict with no exclusions is correct per the BLUEPRINT. The BRD's non-deterministic fields (email, phone from last-wins semantics) are acknowledged with a documented fallback exclusion config if needed.

Notes: The last-wins implementation via `GROUP BY customer_id HAVING rowid = MAX(rowid)` is a creative SQLite-specific approach. The rationale for why it matches V1's dictionary overwrite semantics is convincing: DataSourcing inserts rows in ORDER BY as_of order, and rowid tracks insertion order. The cross-date preference accumulation analysis (HashSet only adds, never removes, so MAX(CASE ... THEN 1) matches) is excellent.

---

### compliance_event_summary
**Verdict: PASS**
- Traceability: PASS -- All BRD requirements (BR-1 through BR-9) are addressed. W1 (Sunday skip) is properly handled via `strftime('%w', as_of) != '0'`. AP1 (dead accounts source), AP3 (unnecessary External), AP4 (unused event_id, customer_id columns), and AP6 (row-by-row Dictionary iteration) are all documented and eliminated. Every BRD requirement has a corresponding FSD implementation element in the traceability matrix.
- Module Chain: PASS -- Tier 1 is correct. GROUP BY + COUNT + COALESCE + strftime for Sunday detection is straightforward SQL. The External module replacement is well-justified.
- SQL/Logic: PASS -- The SQL correctly implements: COALESCE(event_type, '') and COALESCE(status, '') for NULL handling, COUNT(*) for event_count, GROUP BY including as_of (technically redundant for single-day runs but clean), WHERE strftime('%w', as_of) != '0' for Sunday skip, and ORDER BY for deterministic output. The SQL design notes are thorough and address edge cases.
- Writer Config: PASS -- CsvFileWriter with includeHeader=true, trailerFormat="TRAILER|{row_count}|{date}", writeMode=Overwrite, lineEnding=LF. All match V1 exactly.
- Proofmark Config: PASS -- reader: csv, header_rows: 1, trailer_rows: 1, threshold: 100.0. No exclusions or fuzzy needed since all columns are deterministic integers/strings.

Notes: The row ordering concern (V1 Dictionary iteration vs V2 ORDER BY) is correctly flagged as a potential Phase D issue. The empty-input edge case analysis is sound -- relies on data lake having daily snapshots which is a reasonable assumption for the date range.

---

### compliance_open_items
**Verdict: PASS**
- Traceability: PASS -- All BRD requirements (BR-1 through BR-9) are addressed. The FSD correctly identifies the BRD inconsistency where BR-1 text says "Open only" but the V1 source code filters for both Open and Escalated. The FSD follows source code as ground truth, which is the correct approach. W2 (weekend fallback) is properly implemented via SQLite strftime and date() functions. W9 (Overwrite mode) is documented. AP3 (unnecessary External), AP4 (unused review_date, prefix, suffix columns), and AP6 (row-by-row iteration) are eliminated.
- Module Chain: PASS -- Tier 1 is justified. Weekend fallback date computation uses native SQLite functions (strftime('%w') and date() with day modifiers). Status filtering is a simple WHERE clause. Customer name enrichment is a LEFT JOIN. NULL-to-empty coercion uses COALESCE. The CTE-based approach is clean and well-structured.
- SQL/Logic: PASS -- The SQL uses three CTEs (max_date, target, customer_latest) that correctly implement: weekend fallback (Saturday -1 day, Sunday -2 days to Friday), customer deduplication via ROW_NUMBER() OVER (PARTITION BY id ORDER BY as_of DESC), status filter for both Open and Escalated, and output as_of set to the target_date. The CAST(customer_id AS INTEGER) matches V1's Convert.ToInt32. The CROSS JOIN for single-row target CTE is appropriate.
- Writer Config: PASS -- ParquetFileWriter with numParts=1, writeMode=Overwrite. Matches V1 exactly.
- Proofmark Config: PASS -- reader: parquet, threshold: 100.0. No exclusions or fuzzy needed. All output columns are deterministic.

Notes: The BRD inconsistency handling is exemplary -- the FSD documents the discrepancy between BRD text and V1 source code, follows source code as ground truth, and recommends updating the BRD. The weekend fallback implementation using SQLite date functions is elegant. The empty-input edge case is correctly documented with a Tier 2 escalation path if needed.

---

### compliance_resolution_time
**Verdict: PASS**
- Traceability: PASS -- All BRD requirements (BR-1 through BR-7) are addressed. W4 (integer division) is properly identified and preserved via CAST to INTEGER on both operands. W9 (Overwrite mode) is documented. AP4 (unused event_id, customer_id columns) and AP8 (unused ROW_NUMBER window function) are eliminated. The cross join on 1=1 is correctly preserved for output equivalence with thorough documentation of why.
- Module Chain: PASS -- Tier 1 is correct since V1 is already a framework-only job (DataSourcing -> Transformation -> CsvFileWriter). No External module was used in V1, and none is needed in V2.
- SQL/Logic: PASS -- The V2 SQL correctly preserves the cross join (`JOIN compliance_events ON 1=1`) while removing the unused ROW_NUMBER() window function (AP8) and unnecessary columns from the CTE (event_date, review_date). The integer division via `CAST(SUM(...) AS INTEGER) / CAST(COUNT(*) AS INTEGER)` is preserved. The cross join inflation analysis is thorough -- resolved_count and total_days are inflated by factor M, but avg_resolution_days cancels out mathematically.
- Writer Config: PASS -- CsvFileWriter with includeHeader=true, trailerFormat="TRAILER|{row_count}|{date}", writeMode=Overwrite, lineEnding=LF. All match V1.
- Proofmark Config: PASS -- reader: csv, header_rows: 1, trailer_rows: 1, threshold: 100.0. No exclusions or fuzzy needed. All computations are integer-based.

Notes: Clean, focused FSD. The cross join preservation with clear documentation of why it must be kept (output equivalence) while explaining the mathematical impact (inflation cancels in avg) is exactly the right approach.

---

### compliance_transaction_ratio
**Verdict: PASS**
- Traceability: PASS -- All BRD requirements (BR-1 through BR-10) are addressed. W4 (integer division for events_per_1000_txns) and W7 (inflated trailer count from input rows rather than output rows) are properly identified and handled. AP3 is partially eliminated (grouping logic moved to SQL, but External retained for W7 trailer and cross-DataFrame computation). AP4 is explicitly NOT eliminated with a documented rationale (retaining V1 column list for structural safety). AP5 (NULL event_type -> "Unknown") is reproduced. AP6 (row-by-row grouping) is eliminated via SQL GROUP BY.
- Module Chain: PASS -- Tier 2 is justified. The FSD provides two independent reasons why Tier 1 is insufficient: (1) CsvFileWriter cannot produce the inflated trailer (W7 requires input row count, not output row count), and (2) empty transactions table would prevent SQL from referencing it (RegisterTable skips empty DataFrames). The External module is genuinely minimal -- it handles only cross-DataFrame computation and file I/O.
- SQL/Logic: PASS -- The SQL correctly implements GROUP BY with COALESCE(event_type, 'Unknown'), COUNT(*), and ORDER BY event_type. The remaining computation (txn_count, events_per_1000_txns, as_of) is correctly delegated to the External module. The External module design specifies integer division via `(eventCount * RatePerThousand) / txnCount` with a named constant, division-by-zero guard, and inflated trailer computation.
- Writer Config: PASS -- Direct file I/O via External module (no framework writer), matching V1. Overwrite mode, LF line endings, header, and inflated trailer all match V1 behavior.
- Proofmark Config: PASS -- reader: csv, header_rows: 1, trailer_rows: 1, threshold: 100.0. No exclusions or fuzzy needed. All columns are deterministic.

Notes: The AP4 decision to retain unused columns (rather than eliminating them) is explicitly justified -- while AP4 normally calls for removal, retaining V1's column list ensures structural identity for row counts used in the W7 trailer. This is a reasonable trade-off. The Tier 2 justification is thorough and convincing.

---

### covered_transactions
**Verdict: PASS**
- Traceability: PASS -- All BRD requirements (BR-1 through BR-13) are addressed. The FSD correctly identifies and documents the BRD error on BR-1: the BRD claims both Checking and Savings accounts, but V1 source code only filters for Checking. The FSD follows V1 source code as ground truth. No W-codes apply. AP7 (magic values for "Checking", "US") is addressed with named constants. AP6 (row-by-row iteration) is noted as not the problematic pattern (dictionary lookups are O(1), not nested loops).
- Module Chain: PASS -- Tier 3 is justified with two independent reasons: (1) snapshot fallback queries require `as_of <= @date` with no lower bound, which DataSourcing cannot express (it always injects __minEffectiveDate as lower bound), and (2) `DISTINCT ON` is PostgreSQL-specific and has no SQLite equivalent. The ParquetFileWriter is used as the framework writer, keeping the External module focused on data assembly.
- SQL/Logic: PASS -- The 5 PostgreSQL queries are well-designed with clear date patterns (exact match vs snapshot fallback). The in-memory join logic correctly implements the address earliest-start-date selection, customer deduplication, segment alphabetical selection, and sort order (customer_id ASC, transaction_id DESC). The zero-row null-placeholder behavior is documented.
- Writer Config: PASS -- ParquetFileWriter with numParts=4, writeMode=Append. Matches V1 exactly.
- Proofmark Config: PASS -- reader: parquet, threshold: 100.0. No exclusions or fuzzy needed. All 24 columns are deterministic. The amount column is passed through directly (no computation), so no floating-point concern.

Notes: The BRD correction on BR-1 (Checking only, not Checking + Savings) is a significant finding that demonstrates the FSD architect's diligence in verifying BRD claims against V1 source code. The Tier 3 justification is the strongest in the batch -- two independent, well-documented reasons that cannot be worked around.

---

### credit_score_average
**Verdict: PASS**
- Traceability: PASS -- All BRD requirements (BR-1 through BR-8) and open questions (OQ-1, OQ-2) are addressed. AP1 (dead segments source), AP3 (partially -- grouping/joining moved to SQL), AP4 (unused credit_score_id), and AP6 (partially -- nested foreach replaced with SQL + minimal loop) are all handled. W9 (Overwrite mode) is documented.
- Module Chain: PASS -- Tier 2 is well-justified with two concrete reasons: (1) decimal precision -- SQLite AVG() returns REAL (double, ~15-17 digits) while V1 uses C# decimal (28-29 digits), and 1459/2230 customers have non-integer averages; (2) DateOnly type preservation -- V1's as_of flows as DateOnly through CsvFileWriter.FormatField, producing a different format than SQLite's TEXT string passthrough. The External module handles ONLY these two concerns (decimal division and DateOnly reconstruction), with all joining/grouping/aggregation in SQL.
- SQL/Logic: PASS -- The SQL correctly implements: INNER JOIN between credit_scores and customers (matching V1's mutual exclusion behavior), SUM/COUNT instead of AVG (to enable decimal division in External), conditional aggregation via MAX(CASE WHEN LOWER(bureau) = ... THEN score END) for per-bureau scores, COALESCE for name fields, and GROUP BY. The SQL design rationale is thorough.
- Writer Config: PASS -- CsvFileWriter with includeHeader=true, trailerFormat="CONTROL|{date}|{row_count}|{timestamp}", writeMode=Overwrite, lineEnding=CRLF. All match V1 exactly. The CONTROL trailer format (different from TRAILER) and CRLF line endings are correctly preserved.
- Proofmark Config: PASS -- reader: csv, header_rows: 1, trailer_rows: 1, threshold: 100.0. No exclusions needed -- the {timestamp} in the trailer is handled by trailer_rows: 1 stripping it before comparison. No fuzzy needed since V2 uses the same C# decimal arithmetic as V1.

Notes: The Tier 2 justification is particularly well-reasoned. The decimal precision analysis (1459/2230 customers with non-integer averages) provides concrete evidence rather than theoretical concern. The External module design is truly minimal -- only decimal division and DateOnly reconstruction.

---

### credit_score_snapshot
**Verdict: PASS**
- Traceability: PASS -- All BRD requirements (BR-1 through BR-3) and open question OQ-1 are addressed. AP1 (dead branches source), AP3 (unnecessary External for trivial pass-through), AP4 (all branches columns unused), and AP6 (row-by-row foreach copy) are all eliminated.
- Module Chain: PASS -- Tier 1 is the only appropriate choice. The V1 External module's entire logic is a foreach loop copying every row field-by-field -- a textbook SELECT *. The justification is clear and the decision log in the appendix documents alternatives considered.
- SQL/Logic: PASS -- `SELECT credit_score_id, customer_id, bureau, score, as_of FROM credit_scores` is the correct replacement for the V1 foreach pass-through. Column order matches V1's outputColumns definition. The empty input edge case is documented with a reasonable mitigation strategy.
- Writer Config: PASS -- CsvFileWriter with includeHeader=true, no trailer, writeMode=Overwrite, lineEnding=CRLF. All match V1.
- Proofmark Config: PASS -- reader: csv, header_rows: 1, trailer_rows: 0, threshold: 100.0. No exclusions or fuzzy needed for a pure pass-through job.

Notes: Clean, concise FSD for a simple job. The design decisions log in the appendix adds useful context without cluttering the main document.

---

### cross_sell_candidates
**Verdict: FAIL**
- Traceability: PASS -- All BRD requirements (BR-1 through BR-10) and OQ-1 are addressed. AP3 (unnecessary External), AP4 (unused account_id, card_id, investment_id), AP5 (asymmetric representations -- "Yes"/"No Card" for cards, 1/0 for investments, "True"/"False" for account flags), and AP6 (row-by-row iteration) are all handled. W9 (Overwrite mode) is documented.
- Module Chain: PASS -- Tier 1 is correct. All V1 logic (dictionary lookups, foreach iteration, boolean flags, string concatenation) maps to SQL constructs (LEFT JOINs, GROUP BY + CASE, SUBSTR for concatenation). The justification is sound.
- SQL/Logic: PASS -- The SQL is complex but correct. It handles: boolean flags via MAX(CASE WHEN account_type = ... THEN 1 ELSE 0 END) with 'True'/'False' string output (matching C# bool.ToString()), card presence via COUNT(DISTINCT cd.customer_id), investment presence as integer 1/0, and missing_products via conditional concatenation with SUBSTR to strip leading "; ". The COUNT(DISTINCT) approach for cards/investments correctly avoids cross-join inflation from multiple LEFT JOINs. The as_of date format risk is thoroughly documented with a resolution path.
- Writer Config: PASS -- CsvFileWriter with includeHeader=true, no trailer, writeMode=Overwrite, lineEnding=LF. All match V1 and BRD. Output path correctly changed to double_secret_curated.
- Proofmark Config: FAIL -- **The as_of column has a known, documented date format mismatch risk.** The FSD correctly identifies in Section 4 that V1 passes DateOnly through CsvFileWriter.FormatField (producing culture-dependent format like "MM/dd/yyyy" or "10/15/2024") while V2 passes a string from SQLite TEXT (producing "yyyy-MM-dd" like "2024-10-15"). The FSD acknowledges these formats WILL differ. Despite this known mismatch, the Proofmark config starts with zero exclusions and zero fuzzy overrides, treating as_of as STRICT. While the BLUEPRINT says to "start with zero exclusions," the FSD has already identified with HIGH confidence that the as_of column will produce different output between V1 and V2. This is not a "maybe" -- the FSD explicitly states "These formats will differ." The Proofmark config should either: (a) note as_of as EXCLUDED with justification, or (b) the SQL should include the date reformatting fix now rather than deferring it as a Phase D resolution item. Leaving a known output mismatch for Phase D to catch when the FSD already diagnoses the problem is a material issue that will waste a resolution cycle.

**Primary failure reason:** The FSD identifies a definitive as_of date format mismatch (V1: DateOnly.ToString() culture-dependent format vs V2: SQLite "yyyy-MM-dd" string) but does not resolve it in the design. The SQL should either include the date reformatting (the SUBSTR-based conversion the FSD already provides), or the FSD should escalate to Tier 2 with a minimal External module that reconstructs the DateOnly object (similar to credit_score_average). The current design will produce incorrect output and require a guaranteed resolution cycle, which contradicts the FSD's purpose of providing a correct implementation specification.

Notes: This is an otherwise excellent FSD. The SQL logic is sophisticated and correct. The anti-pattern analysis is thorough. The only failure is the unresolved as_of date format issue. The FSD provides both the diagnosis and the fix (SUBSTR-based reformatting or Tier 2 escalation), but inexplicably defers the fix to Phase D rather than incorporating it into the design. Recommendation: Apply the SUBSTR date reformatting in the SQL now, or escalate to Tier 2 with a minimal External for DateOnly passthrough. This is the same DateOnly issue that credit_score_average correctly handles by escalating to Tier 2.

---

## Cross-Cutting Observations

1. **BRD Corrections:** Two FSDs (covered_transactions and compliance_open_items) identified BRD errors and documented corrections. This is exactly the right behavior -- following source code as ground truth over BRD text.

2. **DateOnly Passthrough Risk:** The cross_sell_candidates FSD identifies a systemic issue: any Tier 1 job that passes `as_of` through SQLite TEXT conversion will produce a different date format than V1's DateOnly.ToString(). This issue is also present in card_type_distribution, compliance_event_summary, and credit_score_snapshot, but those jobs may not exhibit the problem if CsvFileWriter handles DateOnly and string-formatted dates identically for the "yyyy-MM-dd" format. The credit_score_average FSD correctly addresses this by escalating to Tier 2. The cross_sell_candidates FSD identifies the problem but does not resolve it. The other Tier 1 jobs should be monitored during Phase D for the same issue.

3. **Empty Input Edge Cases:** Multiple FSDs note that Transformation.RegisterTable skips empty DataFrames, which would cause SQL errors if a table is referenced but not registered. All FSDs handle this consistently -- documenting the risk, noting it's unlikely for the expected date range, and providing escalation paths if needed. This is a reasonable approach.

4. **Row Ordering:** Several jobs replace V1's Dictionary iteration (non-deterministic hash order) with SQL ORDER BY (deterministic). This is noted as a potential Phase D comparison issue in each case. The FSDs consistently start with a reasonable ORDER BY and document the risk.
