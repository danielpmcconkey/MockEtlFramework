# FSD Review — Batch 02

## Summary
- Jobs reviewed: 10
- PASS: 9
- FAIL: 1

---

## Per-Job Reviews

### branch_visit_log
**Verdict: PASS**
- Traceability: PASS — All BRD requirements BR-1 through BR-10 and both open questions (OQ-1, OQ-2) are addressed in the FSD traceability matrix (Section 9). Each requirement maps to a specific FSD section and design decision.
- Module Chain: PASS — Tier 1 is justified. The V1 External module (BranchVisitEnricher) performs two LEFT JOIN-equivalent lookups and row-by-row iteration, all of which are expressible in SQL. AP3 and AP6 are correctly eliminated. The FSD thoroughly documents the edge case around Transformation.cs:46 skipping registration of empty DataFrames and reaches a reasonable conclusion that the net effect matches V1.
- SQL/Logic: PASS — The SQL correctly implements last-write-wins deduplication via MAX(as_of) subqueries for both branches and customers (BR-2, BR-3). The asymmetric NULL handling (BR-6: missing branch -> empty string via COALESCE; BR-7: missing customer -> NULL via CASE WHEN) is correctly modeled. ORDER BY matches V1's iteration order.
- Writer Config: PASS — ParquetFileWriter with numParts=3, writeMode=Append, outputDirectory changed to double_secret_curated. All parameters match V1.
- Proofmark Config: PASS — reader: parquet, threshold: 100.0, no exclusions or fuzzy overrides. All columns are deterministic. Correct and appropriately strict.

**Notes:** The FSD's thorough analysis of the empty DataFrame / table registration edge case (Section 5, SQL Notes) demonstrates good engineering judgment. The decision to proceed with Tier 1 is well-reasoned — the net effect of a SQL error on an empty table vs V1's empty DataFrame return is the same: no rows written for that date.

---

### branch_visit_purpose_breakdown
**Verdict: PASS**
- Traceability: PASS — All BRD requirements BR-1 through BR-9 are addressed. AP1 (dead-end segments table), AP4 (unused visit_id, customer_id columns), and AP8 (unused total_branch_visits window function) are all identified and eliminated. Both open questions (OQ-1, OQ-2) are resolved.
- Module Chain: PASS — Tier 1 is correctly maintained. V1 was already a framework-only chain. No External module needed.
- SQL/Logic: PASS — The V2 SQL correctly flattens the CTE into a direct query with JOIN + GROUP BY, producing identical output. The date-aligned JOIN (branch_id AND as_of), ORDER BY (as_of, branch_id, visit_purpose), and COUNT(*) semantics are all preserved. GROUP BY includes b.branch_name per SQLite requirements, which is correct.
- Writer Config: PASS — CsvFileWriter with includeHeader=true, trailerFormat="END|{row_count}", writeMode=Append, lineEnding=CRLF. All match V1.
- Proofmark Config: PASS — reader: csv, header_rows: 1, trailer_rows: 0. The trailer_rows: 0 is correct per CONFIG_GUIDE.md Example 4 (Append mode with embedded trailers). Threshold 100.0, no exclusions or fuzzy.

---

### branch_visit_summary
**Verdict: PASS**
- Traceability: PASS — All BRD requirements BR-1 through BR-7 are addressed. The FSD identifies and corrects a BRD error (BR-2 states LEFT JOIN, but V1 SQL uses INNER JOIN). This is excellent reviewer-level analysis. AP1/AP4 (unused visit_id, customer_id, visit_purpose columns) are eliminated.
- Module Chain: PASS — Tier 1 is correctly maintained. V1 was already framework-only. CTE is simplified but semantically equivalent.
- SQL/Logic: PASS — The V2 SQL correctly flattens the CTE and uses INNER JOIN (not LEFT JOIN as the BRD incorrectly states). The FSD provides clear evidence from branch_visit_summary.json:22 that the V1 keyword is `JOIN`, not `LEFT JOIN`. GROUP BY includes b.branch_name per SQLite requirements. ORDER BY preserved as as_of, branch_id.
- Writer Config: PASS — CsvFileWriter with includeHeader=true, trailerFormat="TRAILER|{row_count}|{date}", writeMode=Append, lineEnding=LF. All match V1.
- Proofmark Config: PASS — reader: csv, header_rows: 1, trailer_rows: 0 (correct for Append mode). Threshold 100.0.

**Notes:** The FSD's BRD correction (Section 3) is an important finding. The BRD states LEFT JOIN but V1 uses INNER JOIN. The FSD correctly identifies this discrepancy and documents its resolution. This is exactly the kind of analysis the reviewer step is designed to catch. The FSD should be commended for not blindly implementing the BRD's incorrect specification.

---

### card_authorization_summary
**Verdict: PASS**
- Traceability: PASS — All BRD requirements BR-1 through BR-10 are addressed in the traceability matrix. W4 (integer division) is correctly identified and reproduced. AP4 (unused customer_id, amount from card_transactions; unused customer_id from cards) and AP8 (unused ROW_NUMBER() window function and unused_summary CTE) are eliminated.
- Module Chain: PASS — Tier 1 is justified. V1 was already a framework-only chain. The SQL is simplified by removing dead code (AP8) while preserving the core join, grouping, and integer division logic.
- SQL/Logic: PASS — The V2 SQL correctly performs: INNER JOIN on card_id, GROUP BY card_type and as_of, conditional SUM for approved/declined counts, and integer division for approval_rate using CAST(... AS INTEGER) / CAST(... AS INTEGER). The W4 note about SQLite integer division behavior is accurate — when both operands are INTEGER, SQLite performs integer truncation. The decision to keep the CASTs for clarity despite them being technically redundant is sound.
- Writer Config: PASS — CsvFileWriter with includeHeader=true, trailerFormat="TRAILER|{row_count}|{date}", writeMode=Overwrite, lineEnding=LF. All match V1.
- Proofmark Config: PASS — reader: csv, header_rows: 1, trailer_rows: 1 (correct for Overwrite mode with trailer). No fuzzy needed since both V1 and V2 execute the same integer division in the same SQLite engine.

---

### card_customer_spending
**Verdict: PASS**
- Traceability: PASS — All BRD requirements BR-1 through BR-12 are addressed. W2 (weekend fallback) is reproduced in SQL. AP1 (dead accounts table), AP3 (unnecessary External), AP4 (unused columns), and AP6 (row-by-row iteration) are eliminated.
- Module Chain: PASS — Tier 1 is justified. The V1 External module performs weekend date logic, date filtering, customer lookup, and grouping — all expressible in SQL. The FSD provides a clear mapping of each V1 operation to its SQL equivalent. The potential type issue with as_of (DateOnly vs string in Parquet) is honestly documented with a contingency plan for Tier 2 escalation if needed.
- SQL/Logic: PASS — The weekend fallback CTE correctly uses strftime('%w') to detect Saturday (6) and Sunday (0) and applies date offsets to shift to Friday. The customer deduplication CTE replicates dictionary last-writer-wins via MAX(as_of). LEFT JOIN + COALESCE handles missing customers. WHERE ct.as_of = t.target_date correctly filters to the target date.
- Writer Config: PASS — ParquetFileWriter with numParts=1, writeMode=Overwrite. Matches V1.
- Proofmark Config: PASS — reader: parquet, threshold: 100.0. No exclusions or fuzzy. The potential as_of type mismatch and total_spending precision risks are documented but correctly deferred per the "start strict" principle.

**Notes:** Section 10 (Potential Issues) is thorough and honest. The as_of type issue (DateOnly vs string in Parquet) and total_spending precision concern (decimal vs double) are real risks, but the decision to start strict and address during Phase D if failures occur is the correct approach per the BLUEPRINT's guidance.

---

### card_expiration_watch
**Verdict: PASS**
- Traceability: PASS — All BRD requirements BR-1 through BR-10 are addressed. W2 (weekend fallback) is reproduced. AP3 (unnecessary External — business logic moved to SQL) and AP6 (row-by-row iteration — replaced with SQL JOINs) are eliminated. All edge cases from the BRD are covered.
- Module Chain: PASS — Tier 2 is justified. The FSD provides a clear and legitimate reason: the Transformation module stores DateOnly as TEXT and integers as long in SQLite, but V1's Parquet output uses DateOnly-typed and int-typed columns. Byte-identical Parquet output requires matching CLR types. The External module performs ZERO business logic — only type coercion (string->DateOnly, long->int). This is a textbook scalpel use of Tier 2.
- SQL/Logic: PASS — The SQL correctly implements weekend fallback via strftime/date functions, customer deduplication via MAX(as_of), julianday difference for days_until_expiry, 0-90 day window filtering, and LEFT JOIN + COALESCE for customer names. The CAST(julianday(...) AS INTEGER) correctly handles the day difference calculation. The note about julianday precision (note 2 in Section 11) correctly identifies that whole-day differences between date-only strings are exact.
- Writer Config: PASS — ParquetFileWriter with numParts=1, writeMode=Overwrite. Matches V1.
- Proofmark Config: PASS — reader: parquet, threshold: 100.0. No exclusions or fuzzy. All columns are deterministic.

**Notes:** The External module design (Section 9) is clean and well-documented. The type coercion map is explicit and the implementation sketch handles empty DataFrames correctly. The Tier 2 justification is one of the best in this batch — it clearly explains why Tier 1 is insufficient (type fidelity for Parquet) without over-engineering the solution.

---

### card_fraud_flags
**Verdict: FAIL**
- Traceability: FAIL — **Critical finding: The BRD contains a self-contradictory threshold value.** The BRD body text (BR-2, BR-3) states the threshold is $750 (`amount > 750m`), but the BRD's own traceability matrix labels reference "$500" for BR-2 and BR-3. The FSD correctly identifies this contradiction and follows the V1 source code (which uses $500), but the BRD itself needs correction before this can pass traceability review. Without the BRD fix, the FSD and BRD are materially inconsistent. The FSD documents this finding well (Section 3, "BRD Correction: Threshold Value"), citing `CardFraudFlagsProcessor.cs:49-50` as ground truth.
- Module Chain: PASS — Tier 1 is justified. The V1 External module performs a dictionary lookup (JOIN) and dual-condition filter, both trivially expressible in SQL.
- SQL/Logic: PASS — The SQL correctly implements: INNER JOIN on merchant_category_code = mcc_code, WHERE clause filtering on risk_level = 'High' AND ROUND(amount) > 500 (matching V1 ground truth), ROUND(CAST(amount AS REAL), 2) for Banker's rounding (W5), and column renaming (merchant_category_code AS mcc_code). The design notes on INNER JOIN equivalence to dictionary lookup and ROUND-before-comparison order of operations are thorough.
- Writer Config: PASS — CsvFileWriter with includeHeader=true, writeMode=Overwrite, lineEnding=LF, no trailer. All match V1.
- Proofmark Config: PASS — reader: csv, header_rows: 1, trailer_rows: 0 (correct: no trailer configured in V1). Threshold 100.0.

**Required action:** The BRD must be corrected to state $500 consistently in BR-2 and BR-3 body text. The FSD's identification of this discrepancy is correct and well-evidenced, but the FSD cannot PASS traceability when the BRD it traces to contains a materially incorrect value. Once the BRD is fixed, this FSD should pass.

**Additional note on W5 (Banker's rounding):** The FSD states that SQLite's ROUND() uses Banker's rounding. This is a common misconception — SQLite's ROUND() actually uses half-away-from-zero rounding for most values (it calls the C library's round() function). However, for this specific job, the rounding is applied BEFORE the >500 comparison. Since the rounding only affects values at the midpoint (e.g., 500.005), and the filter is strictly greater than 500, the rounding mode difference would only matter for edge cases at the $500 boundary. If this causes a Proofmark failure during Phase D, the resolution would be to escalate to Tier 2 with a minimal External for the rounding, or to add a FUZZY tolerance. The FSD is aware of this potential issue (Section 8, Proofmark Design Rationale mentions the fallback). This is a risk worth noting but does not rise to FAIL level on its own since the FSD has a documented mitigation plan.

---

### card_spending_by_merchant
**Verdict: PASS**
- Traceability: PASS — All BRD requirements BR-1 through BR-10 are addressed. AP3 (unnecessary External), AP4 (unused columns from both tables), and AP6 (row-by-row iteration) are eliminated. The as_of handling (BR-5: from first row via LIMIT 1) correctly mirrors V1's Rows[0] behavior given DataSourcing ordering.
- Module Chain: PASS — Tier 1 is justified. The V1 External module performs GROUP BY + COUNT + SUM + LEFT JOIN, all expressible in SQL. The FSD acknowledges the empty-table risk (Section 5, note 7) and the decimal-vs-double precision risk (note 3), both of which are addressed with appropriate fallback plans.
- SQL/Logic: PASS — The SQL correctly implements: GROUP BY merchant_category_code, COUNT(*) for txn_count, SUM(amount) for total_spending, LEFT JOIN with subquery deduplication for merchant categories, COALESCE for missing MCC codes, and the as_of from first row via scalar subquery. The ORDER BY on merchant_category_code is a reasonable choice for determinism, with a noted fallback if V1's output order differs.
- Writer Config: PASS — ParquetFileWriter with numParts=1, writeMode=Overwrite. Matches V1.
- Proofmark Config: PASS — reader: parquet, threshold: 100.0. No exclusions or fuzzy. The potential total_spending precision risk and row ordering concern are documented with clear mitigation plans.

---

### card_status_snapshot
**Verdict: PASS**
- Traceability: PASS — All BRD requirements BR-1 through BR-5 are addressed. AP4 (5 unused sourced columns) is eliminated. W10 (absurd numParts: 50 for ~3 rows) is correctly reproduced for output equivalence.
- Module Chain: PASS — Tier 1 is justified. V1 was already a pure Tier 1 chain (DataSourcing + Transformation + ParquetFileWriter). The SQL is unchanged because V1's SQL is already clean — no anti-patterns to fix in the SQL itself.
- SQL/Logic: PASS — The V2 SQL is identical to V1: `SELECT c.card_status, COUNT(*) AS card_count, c.as_of FROM cards c GROUP BY c.card_status, c.as_of`. This is correct — there are no SQL-level anti-patterns to address. The only improvement is in the DataSourcing config (removing unused columns).
- Writer Config: PASS — ParquetFileWriter with numParts=50 (W10 reproduced), writeMode=Overwrite. Matches V1 exactly.
- Proofmark Config: PASS — reader: parquet, threshold: 100.0. No exclusions or fuzzy. All columns are deterministic integer/text values.

**Notes:** The simplest FSD in this batch, which is appropriate — the V1 job is already clean SQL with no External module. The FSD correctly identifies the only actionable anti-pattern (AP4: unused columns) and the only wrinkle (W10: 50 parts for 3 rows) and handles both correctly.

---

### card_transaction_daily
**Verdict: PASS**
- Traceability: PASS — All BRD requirements BR-1 through BR-13 and all 5 edge cases are addressed. W3b (end-of-month MONTHLY_TOTAL) and W5 (Banker's rounding) are correctly identified and reproduced. AP1 (dead accounts/customers sourcing), AP3 (scope reduction from Tier 3 to Tier 2), AP4 (unused columns), and AP6 (row-by-row iteration for lookup moved to SQL) are eliminated.
- Module Chain: PASS — Tier 2 is well-justified. The FSD provides three concrete reasons why Tier 1 is insufficient: (1) Banker's rounding cannot be reliably done in SQLite (SQLite ROUND() is half-away-from-zero, not half-to-even), (2) decimal precision for monetary accumulation is lost when amounts are stored as SQLite REAL, and (3) the MONTHLY_TOTAL conditional row requires access to __maxEffectiveDate from shared state. The External module handles only these three operations — the card_type lookup is correctly moved to SQL. This is a proper Tier 3-to-Tier 2 reduction.
- SQL/Logic: PASS — The SQL correctly performs: LEFT JOIN cards on card_id AND as_of, COALESCE for Unknown card_type fallback. The decision to NOT aggregate in SQL (preserving individual rows for decimal accumulation in the External) is well-reasoned and documented (Section 5, note 3).
- Writer Config: PASS — CsvFileWriter with includeHeader=true, trailerFormat="TRAILER|{row_count}|{date}", writeMode=Overwrite, lineEnding=LF. All match V1.
- Proofmark Config: PASS — reader: csv, header_rows: 1, trailer_rows: 1 (correct for Overwrite mode with trailer). No exclusions or fuzzy. The V2 External module uses the same C# decimal type and Math.Round with MidpointRounding.ToEven as V1, so output should be byte-identical.

**Notes:** The External module pseudocode (Section 10) is thorough and demonstrates clear understanding of V1's behavior. The division-by-zero guard (edge case 5 from BRD) is handled for both per-card-type and MONTHLY_TOTAL rows. The SQL join on both card_id AND as_of (note 2) is actually more correct than V1's approach for multi-day ranges, while being equivalent for single-day auto-advance mode.

---

## Cross-Batch Observations

1. **BRD error identified:** The card_fraud_flags BRD contains a self-contradictory threshold value ($750 in body text vs $500 in traceability matrix). The FSD correctly identifies and resolves this using V1 source code as ground truth. The BRD needs correction.

2. **SQLite ROUND() behavior:** Two jobs in this batch (card_fraud_flags and card_transaction_daily) deal with rounding. card_transaction_daily correctly identifies that SQLite ROUND() is NOT Banker's rounding and escalates to Tier 2. card_fraud_flags assumes SQLite ROUND() IS Banker's rounding (Section 3, W5 treatment) — this is technically incorrect but may not matter in practice because the rounding is applied to amounts where exact midpoint values are rare. The FSD documents a fallback plan if Proofmark catches differences. This is noted but does not cause a FAIL since the mitigation is documented.

3. **Empty DataFrame / table registration edge case:** Multiple jobs (branch_visit_log, card_spending_by_merchant) identify the Transformation.cs:46 behavior where empty DataFrames cause tables to not be registered in SQLite, potentially causing SQL errors. All FSDs handle this appropriately — either by documenting the acceptable failure behavior or by noting the risk for Phase D resolution.

4. **Quality of tier justifications:** The Tier 2 justifications in this batch are strong. card_expiration_watch justifies Tier 2 for type coercion (framework limitation, not business logic). card_transaction_daily justifies Tier 2 for Banker's rounding precision and decimal arithmetic (genuine SQL limitation). Both correctly identify what SQL CAN handle vs what it cannot.
