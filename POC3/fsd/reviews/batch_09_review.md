# FSD Review -- Batch 09

## Summary
- Jobs reviewed: 10
- PASS: 10
- FAIL: 0

---

## Per-Job Reviews

### portfolio_value_summary
**Verdict: PASS**
- Traceability: PASS -- All BRD requirements BR-1 through BR-10 are addressed in the FSD traceability matrix with clear design decisions. W2 (weekend fallback) is properly handled. AP1 (dead-end investments sourcing), AP3 (unnecessary External partially eliminated), AP4 (unused holdings columns), and AP6 (row-by-row iteration partially addressed) are all documented with justifications.
- Module Chain: PASS -- Tier 2 is justified. The FSD correctly identifies that (a) `__maxEffectiveDate` is a scalar in shared state unavailable to SQLite, preventing weekend fallback in SQL, and (b) `Transformation.RegisterTable` skips empty DataFrames, which would cause SQL failures on zero-data days. DataSourcing still handles data access, so Tier 3 is unnecessary. The reasoning is sound.
- SQL/Logic: PASS -- N/A for SQL (Tier 2 with External). The External module pseudocode correctly implements weekend fallback (Saturday -1, Sunday -2), customer lookup via dictionary with empty-string defaults, decimal aggregation with `Math.Round(totalValue, 2)`, and `as_of` set to `targetDate`. Logic matches BRD business rules.
- Writer Config: PASS -- ParquetFileWriter with source=output, numParts=1, writeMode=Overwrite matches V1 config. Output path correctly uses `double_secret_curated/`.
- Proofmark Config: PASS -- reader: parquet, threshold: 100.0, no exclusions, no fuzzy. BRD confirms no non-deterministic fields. Starting strict is appropriate.

**Notes:** Thorough FSD with excellent documentation of the Tier 2 justification. The open questions about row ordering determinism and empty-input guard asymmetry are well-flagged. The handling of AP1 (removing investments entirely) is clean. Minor note: the FSD correctly identifies that W5 (banker's rounding) applies as a MONITOR item since both V1 and V2 use the same `Math.Round` call -- this is a thoughtful distinction.

---

### preference_by_segment
**Verdict: PASS**
- Traceability: PASS -- All BRD requirements BR-1 through BR-10 are addressed. W5 (banker's rounding), W7 (trailer inflated count), and W9 (overwrite mode) are all handled with clean replication strategies. AP3 (partially eliminated -- data access moved to DataSourcing), AP4 (preference_id removed), AP6 (partially addressed with LINQ), and AP7 (magic "Unknown" string given named constant) are documented.
- Module Chain: PASS -- The FSD initially considers Tier 1 SQL but correctly identifies three blockers: (1) BR-9's dictionary-overwrite join semantics cannot be expressed in SQL JOINs, (2) W5 banker's rounding is not available in SQLite's ROUND(), and (3) W7's inflated trailer count cannot be produced by CsvFileWriter. The iterative reasoning process (starting with SQL, discovering limitations, revising) is transparent and well-documented. The final chain is DataSourcing x3 -> External, which is effectively Tier 2 (DataSourcing + External). This is justified.
- SQL/Logic: PASS -- The final design removes the Transformation SQL entirely, deferring all business logic to the External module. The External module handles dictionary-based customer-to-segment mapping (last-write-wins per BR-9), group-by aggregation with banker's rounding, and direct CSV file I/O with the inflated trailer. The logic correctly replicates V1 behavior.
- Writer Config: PASS -- The External module writes the CSV directly (matching V1's StreamWriter behavior) with: header row, LF line endings, TRAILER|{inputCount}|{dateStr} format, and overwrite mode (append: false). All parameters match V1. No framework CsvFileWriter is in the chain, which is correct since V1 also bypasses it.
- Proofmark Config: PASS -- reader: csv, header_rows: 1, trailer_rows: 1 (Overwrite mode with trailer), threshold: 100.0, no exclusions. The trailer_rows: 1 is correct per CONFIG_GUIDE.md mapping for "trailerFormat present + writeMode: Overwrite." The potential risk around BR-9 non-determinism is noted but starting strict is the right call.

**Notes:** The FSD's iterative design process is unusually transparent -- it shows the architect starting with SQL, hitting limitations, and revising. This is good engineering documentation. The BR-9 dictionary-overwrite semantics are a genuine SQL blocker. The Proofmark config correctly uses trailer_rows: 1 for the Overwrite-mode trailer.

---

### preference_change_count
**Verdict: PASS**
- Traceability: PASS -- All BRD requirements BR-1 through BR-7 are addressed. AP1 (dead-end customers table), AP4 (preference_id, updated_date), AP8 (unnecessary RANK() CTE and dual CTE structure), and AP9 (misleading name -- documented, cannot rename) are all handled with clean eliminations or documentation. The traceability matrix maps every BRD requirement to a specific FSD design element and V2 implementation detail.
- Module Chain: PASS -- Tier 1 is correct and well-justified. V1 already uses a Tier 1 chain (DataSourcing -> Transformation -> ParquetFileWriter) with no External module. The only V2 changes are anti-pattern eliminations (removing unused sources/columns, simplifying SQL). Tier 1 remains the natural choice.
- SQL/Logic: PASS -- The V2 SQL correctly simplifies V1's two-CTE query into a single direct SELECT with GROUP BY. The RANK() window function is provably unused (rnk is never referenced downstream), so its removal is safe. The aggregate expressions (COUNT(*), MAX(CASE WHEN...)) are preserved identically. Column order matches V1's final SELECT. The opted_in boolean-to-integer mapping through SQLite is correctly documented.
- Writer Config: PASS -- ParquetFileWriter with source=pref_counts, numParts=1, writeMode=Overwrite matches V1 exactly. Output path uses double_secret_curated/.
- Proofmark Config: PASS -- reader: parquet, threshold: 100.0, no exclusions, no fuzzy. All output columns are deterministic integer/date values. BRD confirms no non-deterministic fields. Starting strict is correct.

**Notes:** Clean, well-structured FSD. The AP8 elimination (removing the dead RANK() CTE) is well-justified with proof that the rnk column is unreferenced. The open question about row ordering in Parquet output is a reasonable concern but appropriately deferred to Phase D testing.

---

### preference_summary
**Verdict: PASS**
- Traceability: PASS -- All BRD requirements BR-1 through BR-8 are addressed. AP1 (customers table), AP3 (External module replaced with SQL), AP4 (preference_id, updated_date, customer_id), and AP6 (foreach loop replaced with GROUP BY) are all eliminated. The traceability matrix is comprehensive, mapping every BRD requirement and every anti-pattern to specific FSD decisions.
- Module Chain: PASS -- Tier 1 is correct. V1's PreferenceSummaryCounter.cs performs textbook GROUP BY + conditional counting -- entirely expressible in SQL. No procedural logic, no cross-date queries, no snapshot fallback. The External module is a clear AP3 violation, and Tier 1 replacement is the right move.
- SQL/Logic: PASS -- The V2 SQL correctly replicates V1's logic: COALESCE(preference_type, '') for NULL handling, SUM(CASE WHEN opted_in = 1) for opted_in_count, SUM(CASE WHEN opted_in = 0) for opted_out_count, total_customers as the sum of the two cases (matching V1's `optedIn + optedOut` exactly), MIN(as_of) for the as_of column (matching V1's first-row behavior given DataSourcing's ORDER BY as_of), and ORDER BY MIN(rowid) to replicate V1's Dictionary insertion order. Each design choice is thoroughly justified with evidence.
- Writer Config: PASS -- CsvFileWriter with includeHeader=true, trailerFormat=TRAILER|{row_count}|{date}, writeMode=Overwrite, lineEnding=LF. All match V1 exactly. The trailer uses standard CsvFileWriter {row_count} token, which correctly reflects output row count (not inflated -- BR-7 confirms standard framework behavior).
- Proofmark Config: PASS -- reader: csv, header_rows: 1, trailer_rows: 1, threshold: 100.0, no exclusions, no fuzzy. The trailer_rows: 1 is correct for Overwrite mode with trailerFormat. BRD confirms no non-deterministic fields.

**Notes:** Excellent FSD. The ORDER BY MIN(rowid) approach for replicating Dictionary insertion order is creative and well-justified. The empty DataFrame edge case (Transformation.RegisterTable skips empty DataFrames) is honestly documented as a known limitation that does not affect the validation date range. The total_customers computation using the sum of two CASE expressions (rather than COUNT(*)) is a deliberate choice to mirror V1's exact logic -- good attention to detail.

---

### preference_trend
**Verdict: PASS**
- Traceability: PASS -- All BRD requirements BR-1 through BR-4 are addressed. AP4 (preference_id and customer_id removed from DataSourcing) is the only applicable anti-pattern and is properly eliminated. The traceability matrix maps every BRD requirement and the AP4 elimination to specific FSD decisions.
- Module Chain: PASS -- Tier 1 is correct. V1 already uses Tier 1 (DataSourcing -> Transformation -> CsvFileWriter) with no External module. V2 preserves this structure, only removing unused columns. No tier escalation needed.
- SQL/Logic: PASS -- The V2 SQL is identical to V1's SQL (no changes needed -- V1's SQL is clean and minimal). The SUM(CASE WHEN) expressions, GROUP BY preference_type/as_of, and absence of ORDER BY all match V1 exactly. The boolean-to-integer mapping through SQLite is correctly documented.
- Writer Config: PASS -- CsvFileWriter with includeHeader=true, writeMode=Append, lineEnding=LF, no trailerFormat. All match V1 exactly. The Append mode semantics (header on first write only, cumulative growth, re-run duplication) are correctly documented.
- Proofmark Config: PASS -- reader: csv, header_rows: 1, trailer_rows: 0, threshold: 100.0, no exclusions, no fuzzy. The trailer_rows: 0 is correct (no trailer). BRD confirms no non-deterministic fields. Starting strict is appropriate.

**Notes:** Straightforward FSD for a simple job. The V1 SQL is already clean, so V2 preserves it identically. The only material change is the AP4 elimination of unused columns. Well-documented open question about the missing ORDER BY clause.

---

### product_penetration
**Verdict: PASS**
- Traceability: PASS -- All BRD requirements BR-1 through BR-7 are addressed. W4 (integer division) is properly handled -- the SQL preserves V1's CAST(cnt AS INTEGER) / CAST(total_customers AS INTEGER) expression. AP4 (unused columns from customers, accounts, cards, investments) is eliminated. The traceability matrix maps every BRD requirement to a specific FSD decision.
- Module Chain: PASS -- Tier 1 is correct. V1 already uses Transformation with SQL (BRD BR-7 confirms no External module). All logic is natively SQL-expressible. No tier escalation needed.
- SQL/Logic: PASS -- The V2 SQL is functionally identical to V1's SQL. The CTE structure (customer_counts, account_holders, card_holders, investment_holders, product_stats), the integer division CAST expressions, the UNION ALL, the cross-join `JOIN customers ON 1=1`, and the `LIMIT 3` are all preserved. The FSD correctly documents that the integer division behavior is inherent in SQLite's handling of INTEGER operands and does not need artificial injection. The W4 documentation explains the output implications (0 or 1 only, never fractional).
- Writer Config: PASS -- CsvFileWriter with includeHeader=true, writeMode=Overwrite, lineEnding=LF, no trailer. All match V1.
- Proofmark Config: PASS -- reader: csv, header_rows: 1, trailer_rows: 0, threshold: 100.0, no exclusions, no fuzzy. The BRD flags as_of as potentially non-deterministic (MEDIUM confidence) due to the cross-join pattern, but the FSD correctly starts strict and documents a fallback plan if Proofmark fails on this column.

**Notes:** Good FSD that preserves V1's SQL exactly while eliminating unused columns across all four DataSourcing modules. The W4 handling is pragmatic -- since SQLite natively performs integer division on INTEGER operands, the SQL naturally produces the V1 bug's output without any special code. The cross-join `as_of` non-determinism concern is well-flagged with a clear mitigation strategy.

---

### quarterly_executive_kpis
**Verdict: PASS**
- Traceability: PASS -- All BRD requirements BR-1 through BR-10 are addressed. W2 (weekend fallback) is replicated via SQL CASE on strftime('%w'). W5 (banker's rounding) is replicated via SQLite ROUND(). AP2 (duplicated logic with executive_dashboard -- documented, not fixable), AP3 (External module eliminated), AP4 (unused columns from all 5 source tables), AP6 (foreach loops replaced with SQL aggregation), and AP9 (misleading "quarterly" name -- documented, not fixable) are all handled.
- Module Chain: PASS -- Tier 1 is well-justified. V1 uses an External module that performs row counting, summing, and building 8 output rows -- all expressible in SQL via COUNT(*), SUM(), UNION ALL, and CASE. The weekend fallback is achievable in SQLite using strftime('%w') and date() functions. The guard clause (empty customers = 0 rows) is replicated via WHERE EXISTS subquery. No procedural logic required.
- SQL/Logic: PASS -- The V2 SQL is comprehensive and well-designed. The UNION ALL produces the 8 KPI rows in fixed order matching V1's list. Each branch uses WHERE EXISTS (SELECT 1 FROM customers) to replicate the guard clause. COUNT(*) is used for row counts (not DISTINCT, matching V1's count++ pattern per BR-7). SUM for balances/amounts/values. The CAST to REAL ensures consistent numeric type across UNION ALL branches. The outer ROUND(kpi_value, 2) replicates V1's Math.Round. The weekend fallback CASE expression correctly maps Saturday(6) to -1 day and Sunday(0) to -2 days. One minor note: the FSD claims "SQLite's ROUND() uses banker's rounding by default" -- this is actually implementation-dependent. SQLite's C library round() typically uses "round half away from zero," NOT banker's rounding. However, the FSD also correctly notes this is effectively a no-op since all source values already have <= 2 decimal places and counts are integers. Since no midpoint case can arise in practice, the rounding mode difference is moot for this specific job. This does not constitute a FAIL since the output will be identical regardless.
- Writer Config: PASS -- ParquetFileWriter with source=output, numParts=1, writeMode=Overwrite. All match V1.
- Proofmark Config: PASS -- reader: parquet, threshold: 100.0, no exclusions, no fuzzy. BRD confirms no non-deterministic fields.

**Notes:** Ambitious Tier 1 replacement of a 5-table External module. The SQL design is sound. The WHERE EXISTS guard clause pattern is a clean alternative to V1's null/empty check. One technical inaccuracy worth noting for the developer: the FSD states SQLite's ROUND uses banker's rounding, but in fact SQLite uses "round half away from zero" (C library round()). This would matter if any kpi_value had a midpoint at 2 decimal places, but the FSD correctly argues this cannot happen with the current data types (integer counts and numeric(12,2)/numeric(14,2) sums). The CAST to REAL for integer counts is a design choice worth monitoring -- if Parquet stores these as doubles rather than longs, there could be a type mismatch. The FSD's open question #6 acknowledges this risk.

---

### regulatory_exposure_summary
**Verdict: PASS**
- Traceability: PASS -- All BRD requirements BR-1 through BR-11 are addressed. W2 (weekend fallback) is replicated in SQL via strftime/date functions. W5 (banker's rounding) is handled by the External module using decimal Math.Round. AP2 (documented, not fixable), AP3 (partially eliminated -- aggregation/joins moved to SQL), AP4 (unused columns from compliance_events, wire_transfers, accounts), AP6 (row-by-row iteration replaced with SQL GROUP BY), and AP7 (magic weights given named constants) are all handled.
- Module Chain: PASS -- Tier 2 is well-justified. Two genuine blockers prevent Tier 1: (1) SQLite REAL (double) cannot match V1's decimal arithmetic for the exposure score formula involving division by 10000, and (2) SQLite ROUND() does not guarantee banker's rounding matching C#'s Math.Round(decimal, 2). The minimal External module handles ONLY the decimal arithmetic and rounding -- all aggregation, joining, filtering, and NULL coalescing are in SQL. This is textbook Tier 2 scalpel usage.
- SQL/Logic: PASS -- The SQL is well-structured with CTEs for weekend fallback (effective, target), customer date filtering with fallback (date_filtered_customers, fallback_customers), and aggregations (comp_agg, wire_agg, acct_agg). The LEFT JOINs with COALESCE correctly replicate V1's dictionary-based lookup with default-zero behavior. The External module pseudocode correctly applies Math.Round(rawTotalBalance, 2) and the exposure score formula with decimal arithmetic and named constants. The as_of is set from the SQL-computed target_date.
- Writer Config: PASS -- ParquetFileWriter with source=output, numParts=1, writeMode=Overwrite. All match V1.
- Proofmark Config: PASS -- reader: parquet, threshold: 100.0, no exclusions, no fuzzy. BRD confirms no non-deterministic fields. The risk register correctly flags potential SQLite REAL precision issues for total_balance SUM, with a clear mitigation strategy.

**Notes:** Thorough and well-designed FSD with a detailed risk register. The Tier 2 justification is among the strongest in this batch -- the decimal arithmetic and banker's rounding concerns are legitimate SQLite limitations. The SQL's fallback_customers CTE pattern (using UNION ALL with complementary WHERE conditions) is clever. The External module scope is genuinely minimal (just the arithmetic and rounding pass). The AP7 elimination with named constants (ComplianceWeight, WireWeight, BalanceDivisor) is well-executed.

---

### repeat_overdraft_customers
**Verdict: PASS**
- Traceability: PASS -- All BRD requirements BR-1 through BR-7 and edge cases EC-1 through EC-8 are addressed. AP3 (External module eliminated), AP4 (5 unused overdraft_events columns removed), AP6 (foreach loops replaced with SQL), and AP7 (magic threshold 2 documented via SQL comment) are all handled. The traceability mapping is clean.
- Module Chain: PASS -- Tier 1 is correct and well-justified. All V1 business logic maps to standard SQL: GROUP BY with COUNT/SUM for aggregation, HAVING for threshold filtering, LEFT JOIN for customer lookup, COALESCE for NULL handling, and a subquery for customer deduplication (last-loaded-wins).
- SQL/Logic: PASS -- The V2 SQL correctly implements: (1) customer deduplication via MAX(rowid) per id (replicating V1's dictionary-overwrite behavior), (2) overdraft aggregation across all dates with COUNT(*) and SUM(overdraft_amount), (3) HAVING COUNT(*) >= 2 for the repeat threshold, (4) MIN(as_of) from overdraft_events for the scalar as_of value (matching V1's first-row behavior), and (5) LEFT JOIN with COALESCE for missing customer names. The use of SQLite rowid for deduplication is well-justified (rowids reflect insertion order, which matches DataSourcing's ORDER BY as_of).
- Writer Config: PASS -- ParquetFileWriter with source=output, numParts=1, writeMode=Overwrite. All match V1.
- Proofmark Config: PASS -- reader: parquet, threshold: 100.0, no exclusions, no fuzzy. BRD confirms no non-deterministic fields. V1 uses decimal for overdraft amounts, so no epsilon concerns.

**Notes:** Clean Tier 1 replacement of an External module. The customer deduplication strategy using MAX(rowid) is sound and well-documented with SQLite guarantees. The open question about SQLite REAL precision for SUM(overdraft_amount) is acknowledged with a reasonable mitigation (fuzzy column if needed). The HAVING threshold comment serves as AP7 documentation since SQL does not support named constants.

---

### securities_directory
**Verdict: PASS**
- Traceability: PASS -- All BRD requirements BR-1 through BR-7 and edge cases are addressed. AP1 (dead-end holdings sourcing) and AP4 (all unused holdings columns) are eliminated. The traceability matrix maps every BRD requirement to a specific FSD section.
- Module Chain: PASS -- Tier 1 is correct. V1 already uses Tier 1 (DataSourcing -> Transformation -> CsvFileWriter). The only change is removing the dead-end holdings DataSourcing module. No tier escalation needed.
- SQL/Logic: PASS -- The V2 SQL is identical to V1's SQL: `SELECT s.security_id, s.ticker, s.security_name, s.security_type, s.sector, s.exchange, s.as_of FROM securities s ORDER BY s.security_id`. No changes needed -- V1's SQL is clean and correct. The result is stored as `securities_dir` (matching V1's resultName).
- Writer Config: PASS -- CsvFileWriter with source=securities_dir, includeHeader=true, writeMode=Overwrite, lineEnding=LF, no trailer. All match V1 exactly.
- Proofmark Config: PASS -- reader: csv, header_rows: 1, trailer_rows: 0, threshold: 100.0, no exclusions, no fuzzy. All columns are deterministic pass-throughs. BRD confirms no non-deterministic fields.

**Notes:** The simplest FSD in this batch -- it is a pass-through job with no business logic beyond ORDER BY. The only material V2 change is removing the dead-end holdings DataSourcing (AP1). Well-structured and concise.
