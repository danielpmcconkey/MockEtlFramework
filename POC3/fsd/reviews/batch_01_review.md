# FSD Review -- Batch 01

## Summary
- Jobs reviewed: 10
- PASS: 9
- FAIL: 1

---

## Per-Job Reviews

### account_balance_snapshot
**Verdict: PASS**
- Traceability: PASS -- All BRD requirements (BR-1 through BR-6) addressed. AP1, AP3, AP4, AP6 all identified and eliminated with clear justification. No W-codes apply, correctly identified.
- Module Chain: PASS -- Tier 1 is justified. The V1 External module is a pure column-select passthrough (AP3). DataSourcing directly produces the output schema since `as_of` is auto-appended. Skipping even the Transformation module is correct and clever.
- SQL/Logic: PASS -- N/A (no Transformation module). DataSourcing produces the exact 6-column output schema. The `as_of` auto-append behavior is correctly cited with framework evidence (DataSourcing.cs:69-72).
- Writer Config: PASS -- ParquetFileWriter, numParts=2, writeMode=Append all match V1. Source changed from `output` to `accounts` with correct justification. Output directory changed to `double_secret_curated` per V2 convention.
- Proofmark Config: PASS -- Parquet reader, 100% threshold, no exclusions, no fuzzy. All fields are deterministic passthroughs. BRD confirms no non-deterministic fields.

Clean, well-reasoned FSD. The two-module chain (DataSourcing -> ParquetFileWriter) is the simplest possible design and correctly justified.

---

### account_customer_join
**Verdict: PASS**
- Traceability: PASS -- All BRD requirements (BR-1 through BR-6) addressed in the traceability matrix. AP1 (addresses removed), AP3 (External replaced with SQL), AP6 (foreach replaced with LEFT JOIN) all eliminated.
- Module Chain: PASS -- Tier 1 is justified. The V1 External module performs a dictionary-based lookup join, which is a textbook SQL LEFT JOIN with COALESCE for defaults.
- SQL/Logic: PASS -- The LEFT JOIN with COALESCE correctly replicates the dictionary lookup with empty-string defaults (BR-3). Join on both `customer_id` AND `as_of` is well-reasoned and documented. The ORDER BY adds determinism. The empty-DataFrame edge case (BR-4) is thoroughly discussed, with the honest acknowledgment that V2 may diverge from V1 when `accounts` has rows but `customers` is empty -- but this is a theoretical edge case that cannot occur in practice (same effective dates for both tables).
- Writer Config: PASS -- ParquetFileWriter, numParts=2, writeMode=Overwrite all match V1. Output directory changed per V2 convention.
- Proofmark Config: PASS -- Parquet reader, 100% threshold, no exclusions, no fuzzy. Correct rationale provided.

The SQL design rationale is exceptionally thorough. The multi-day dictionary collision analysis (Section 5) demonstrates deep understanding of V1 behavior.

---

### account_overdraft_history
**Verdict: PASS**
- Traceability: PASS -- All BRD requirements (BR-1 through BR-6) addressed. AP4 (unused columns) correctly identified and eliminated. W9 and W10 correctly identified and reproduced.
- Module Chain: PASS -- Tier 1 is correct. V1 is already a framework-only chain (DataSourcing + Transformation + ParquetFileWriter). No External module in V1, so no AP3 to eliminate.
- SQL/Logic: PASS -- SQL is functionally identical to V1. INNER JOIN on account_id + as_of (BR-1, BR-2), ORDER BY as_of then overdraft_id (BR-3), only account_type selected from accounts (BR-5). Event_timestamp correctly excluded from both DataSourcing and SQL (BR-6).
- Writer Config: PASS -- ParquetFileWriter, numParts=50, writeMode=Overwrite all match V1. The FSD correctly identifies W9 (Overwrite loses prior days) and W10 (50 parts is excessive) and reproduces both.
- Proofmark Config: PASS -- Parquet reader, 100% threshold, no exclusions, no fuzzy. All fields are deterministic passthroughs.

Straightforward review -- this job was already clean in V1. The FSD correctly identifies the only improvement opportunity (AP4) and implements it.

---

### account_status_summary
**Verdict: PASS**
- Traceability: PASS -- All BRD requirements (BR-1 through BR-7) addressed. AP1 (segments removed), AP4 (customer_id and current_balance removed), AP6 (foreach replaced with LINQ GroupBy) all correctly handled.
- Module Chain: PASS -- Tier 2 is justified with strong reasoning. The Transformation module's `RegisterTable()` skips empty DataFrames (Transformation.cs:47), which would cause SQL failures on weekend dates when accounts has zero rows. Since the executor halts gap-fill on failure (JobExecutorService.cs:88), this would block all subsequent date processing. The FSD correctly identifies that the framework cannot be modified (guardrails) and Tier 2 is the minimum escalation. The External module handles both the empty-data guard AND the GROUP BY logic, which is logical since separating them across modules would be awkward.
- SQL/Logic: PASS -- N/A (logic is in the External module). The LINQ GroupBy + Count() correctly replicates V1's Dictionary-based counting. The as_of handling preserves the DateOnly type for correct CsvFileWriter formatting (MM/dd/yyyy). The pseudocode is clear and complete.
- Writer Config: PASS -- CsvFileWriter with includeHeader=true, trailerFormat=TRAILER|{row_count}|{date}, writeMode=Overwrite, lineEnding=LF all match V1 exactly.
- Proofmark Config: PASS -- CSV reader, 1 header row, 1 trailer row, 100% threshold. Row ordering concern acknowledged with a reasonable mitigation plan.

**Note on cross-job consistency:** This FSD escalates to Tier 2 for the empty-DataFrame issue, while `account_type_distribution` (see below) stays at Tier 1 and accepts the risk for the same issue. Both approaches are defensible, but the inconsistency is worth noting for the developer. The Tier 2 approach here is arguably more robust since it guarantees correct behavior on weekends.

**Minor note:** The FSD's Section 4 includes a detailed analysis of the `as_of` date format (MM/dd/yyyy vs yyyy-MM-dd), which is excellent. This is a subtle difference that could easily cause Proofmark failures if missed.

---

### account_type_distribution
**Verdict: FAIL**
- Traceability: PASS -- All BRD requirements (BR-1 through BR-8) addressed. AP1 (branches removed), AP3 (External replaced with SQL), AP4 (unused columns removed), AP6 (foreach replaced with GROUP BY) all handled.
- Module Chain: FAIL -- **Tier 1 is chosen despite the known empty-DataFrame failure on weekend dates.** The FSD extensively documents this risk (Section 5 caveat, Appendix Risk Register) and acknowledges that V1 produces a header+trailer CSV on weekends while V2 would throw an error. The executor halts on failure, blocking all subsequent dates. This is the exact same issue that `account_status_summary` correctly escalated to Tier 2 for. The FSD's own words: "If this edge case causes runtime failures, a Tier 2 escalation... may be needed." This is a known, predictable failure, not a speculative risk. The date range 2024-10-01 to 2024-12-31 includes weekends, so this WILL fail. The FSD should use Tier 2 (like account_status_summary) or provide a concrete mitigation.
- SQL/Logic: PASS -- The SQL itself is correct. GROUP BY, COUNT, CAST AS REAL for percentage, scalar subqueries for total_accounts and as_of are all well-designed. The analysis of SQLite REAL vs C# double (IEEE 754 equivalence) is solid.
- Writer Config: PASS -- CsvFileWriter with includeHeader=true, trailerFormat=END|{row_count}, writeMode=Overwrite, lineEnding=LF all match V1.
- Proofmark Config: PASS -- CSV reader, 1 header row, 1 trailer row, 100% threshold. Reasonable contingency plan for row ordering.

**Reason for FAIL:** The FSD acknowledges a guaranteed runtime failure on weekend dates and chooses to proceed anyway, deferring the fix to Phase D. This is materially incorrect -- the V2 implementation as specified will not produce byte-identical output to V1 for weekend dates. The fix is straightforward (Tier 2 with a minimal External module, identical to the approach used by account_status_summary), and the FSD even documents this as the likely resolution. The FSD should be revised to use Tier 2 from the start, or at minimum provide a concrete SQL-level mitigation (e.g., a CREATE TABLE fallback pattern) rather than deferring the known failure.

---

### account_velocity_tracking
**Verdict: PASS**
- Traceability: PASS -- All BRD requirements (BR-1 through BR-10) addressed in the traceability matrix. AP1 (credit_limit/apr removed), AP3 (business logic moved to SQL), AP4 (transaction_id/txn_timestamp/txn_type/description removed), AP6 (foreach replaced with SQL GROUP BY) all handled. W12 (header every append) correctly identified and reproduced via External module.
- Module Chain: PASS -- Tier 2 is well-justified. The SQL Transformation handles all business logic (GROUP BY, SUM, LEFT JOIN, COALESCE). The External module is minimal and handles only: (1) W12 direct CSV write with header re-emitted on every append, and (2) as_of column injection from __maxEffectiveDate. The framework's CsvFileWriter cannot replicate W12 (CsvFileWriter.cs:47 suppresses headers in append mode). This is textbook Tier 2 -- one specific operation that can't be in SQL/framework.
- SQL/Logic: PASS -- The SQL correctly implements GROUP BY account_id+as_of, LEFT JOIN with DISTINCT accounts subquery for customer_id lookup, COALESCE to 0 for missing customers (BR-2), ROUND(SUM, 2) for amounts (BR-3), ORDER BY as_of+account_id (BR-4). The CAST(as_of AS TEXT) for txn_date correctly matches V1's ToString() behavior via the Transformation module's DateOnly-to-TEXT conversion.
- Writer Config: PASS -- Direct file I/O via External (no framework writer), matching V1. Append mode with LF line endings, header re-emitted on every run (W12). Output set to empty DataFrame to prevent framework writing (BR-7).
- Proofmark Config: PASS -- CSV reader, 1 header row, 0 trailer rows, 100% threshold. The rationale for header_rows=1 with W12 (embedded headers in data will match between V1 and V2) is correct.

**Notes:**
- The risk of SQLite REAL vs C# decimal for total_amount is well-documented with a clear mitigation strategy (Section 10, "Risk: SQLite REAL vs C# decimal"). This is an honest assessment, not a hand-wave.
- The External module handles the empty-data edge case (weekend accounts) by checking source DataFrames before accessing velocity_output. This handles the Transformation.RegisterTable empty-table issue that affects other jobs.
- The External module design is thorough -- full pseudocode, clear separation of concerns, explicit output column order documentation.

---

### bond_maturity_schedule
**Verdict: PASS**
- Traceability: PASS -- All BRD requirements (BR-1 through BR-11) addressed. AP3 (External replaced with SQL), AP4 (exchange, holding_id, investment_id, customer_id, quantity, cost_basis removed), AP6 (nested foreach replaced with LEFT JOIN + GROUP BY), AP9 (misleading name documented) all handled.
- Module Chain: PASS -- Tier 1 is well-justified. Every V1 operation (filter, join, aggregate, coalesce, round) maps directly to SQL. No procedural logic required.
- SQL/Logic: PASS -- The SQL is well-designed. LEFT JOIN (not INNER) correctly preserves bonds with no holdings (BR-6). COUNT(h.current_value) correctly returns 0 for unmatched bonds (vs COUNT(*) which would return 1). COALESCE for null strings (BR-10). ROUND for total_held_value (BR-5). MAX(s.as_of) cleverly approximates __maxEffectiveDate without needing shared state access (Section 5, rationale #5). GROUP BY s.security_id collapses multi-day duplicates, which is arguably better than V1's behavior for multi-day ranges but identical for single-day (normal) runs.
- Writer Config: PASS -- ParquetFileWriter, numParts=1, writeMode=Overwrite all match V1.
- Proofmark Config: PASS -- Parquet reader, 100% threshold, no exclusions, no fuzzy. W5 (banker's rounding vs arithmetic rounding) risk is documented with a clear escalation path.

**Minor note on W5:** The FSD correctly identifies that `Math.Round(decimal, 2)` defaults to MidpointRounding.ToEven while SQLite's ROUND uses half-away-from-zero. The FSD's argument that this difference only matters at exact midpoints (X.XX5) and is unlikely for sums of 2-decimal monetary values is reasonable. The risk is documented, not ignored.

---

### branch_card_activity
**Verdict: PASS**
- Traceability: PASS -- All BRD requirements (BR-1 through BR-9) addressed. AP1 (segments removed), AP4 (card_id, authorization_status, country, first_name, last_name removed) correctly handled. W10 (numParts=50) correctly identified and reproduced.
- Module Chain: PASS -- Tier 1 is correct. V1 is already a framework-only chain. No External module involved.
- SQL/Logic: PASS -- SQL is functionally identical to V1. The modulo branch assignment (BR-1), JOIN on customers as existence filter (BR-4), MAX(branch_id) subquery across all dates (BR-6), GROUP BY branch+date (BR-7), ROUND(SUM, 2) (BR-8) are all preserved. The SQL correctly does NOT filter on authorization_status (BR-2).
- Writer Config: PASS -- ParquetFileWriter, numParts=50, writeMode=Overwrite match V1. W10 documented.
- Proofmark Config: PASS -- Parquet reader, 100% threshold, no exclusions, no fuzzy. All columns are deterministic. Both V1 and V2 use SQLite for aggregation, so ROUND behavior is identical.

**Note on JOIN without as_of:** The SQL joins `customers c ON ct.customer_id = c.id` without an `as_of` condition, matching V1. For multi-day runs this could produce cross-date matches, but since V1 behaves the same way, V2 correctly replicates this behavior.

**Note on missing ORDER BY:** The V1 SQL (reproduced in V2) has no ORDER BY clause. Row ordering in the output depends on SQLite's GROUP BY implementation. Since both V1 and V2 use the same SQL in the same engine, ordering should be identical. Worth watching during Proofmark comparison.

---

### branch_directory
**Verdict: PASS**
- Traceability: PASS -- All BRD requirements (BR-1 through BR-5) addressed. No AP-codes apply -- the job is already clean. The AP8 concern (semantically meaningless ORDER BY in ROW_NUMBER) is correctly preserved for output equivalence rather than "fixed."
- Module Chain: PASS -- Tier 1 is correct. V1 is already a framework-only chain. All logic (ROW_NUMBER dedup, column selection, ordering) is SQL.
- SQL/Logic: PASS -- SQL is identical to V1. The ROW_NUMBER() OVER (PARTITION BY branch_id ORDER BY branch_id) is preserved exactly, including the meaningless ORDER BY, because changing it could alter which row wins the dedup (and thus change the as_of value in output). The CTE is genuine (serves the dedup purpose), not unused (AP8). Well-documented reasoning.
- Writer Config: PASS -- CsvFileWriter with includeHeader=true, writeMode=Overwrite, lineEnding=CRLF, no trailer. All match V1.
- Proofmark Config: PASS -- CSV reader, 1 header row, 0 trailer rows, 100% threshold. The decision to start strict despite as_of being theoretically non-deterministic is well-reasoned: both V1 and V2 source the same data in the same order through the same framework pipeline, so the same row should win the ROW_NUMBER. Contingency plan for EXCLUDED as_of is documented.

Clean, minimal FSD for a clean job. The analysis of as_of non-determinism is thoughtful -- the FSD correctly identifies that theoretical non-determinism does not imply practical non-determinism when both implementations use the same data path.

---

### branch_transaction_volume
**Verdict: PASS**
- Traceability: PASS -- All BRD requirements (BR-1 through BR-9) addressed. AP1 (branches and customers tables removed), AP4 (description, interest_rate, transaction_id, txn_type removed), AP9 (misleading name documented) all handled.
- Module Chain: PASS -- Tier 1 is correct. V1 is already a framework-only chain. SQL is straightforward JOIN + GROUP BY + aggregation.
- SQL/Logic: PASS -- SQL is identical to V1. INNER JOIN on account_id + as_of (BR-1), GROUP BY account_id+customer_id+as_of (BR-2), ROUND(SUM, 2) (BR-3), ORDER BY as_of+account_id (BR-4), COUNT(*) (BR-9). The stream-of-consciousness analysis in Section 3 about which columns to remove is a bit verbose but ultimately reaches the correct conclusion.
- Writer Config: PASS -- ParquetFileWriter, numParts=1, writeMode=Overwrite match V1.
- Proofmark Config: PASS -- Parquet reader, 100% threshold, no exclusions, no fuzzy. Both V1 and V2 use identical SQL in the same SQLite engine, so ROUND/SUM behavior is identical.

**Minor style note:** The FSD's Section 3 includes a visible stream-of-consciousness about whether to remove `transaction_id` (starting with "However, `transaction_id` is explicitly listed..." and continuing with "Wait -- we need to be careful. Let me re-examine..."). This reads like draft notes rather than a finished specification. It reaches the correct conclusion but should be cleaned up for clarity. This is a documentation quality issue, not a correctness issue, and does not warrant a FAIL.

---

## Cross-Cutting Observations

1. **Empty-DataFrame Consistency:** The `account_status_summary` FSD escalates to Tier 2 for the Transformation.RegisterTable empty-DataFrame issue, while `account_type_distribution` stays at Tier 1 and accepts the same risk. The Tier 2 approach is more robust. `account_type_distribution` should be revised to match. This inconsistency is the basis for the only FAIL in this batch.

2. **W5 (Banker's Rounding) Risk:** Two jobs (`bond_maturity_schedule`, `account_velocity_tracking`) involve ROUND operations where V1 uses C# Math.Round (banker's rounding) and V2 uses SQLite ROUND (half-away-from-zero). Both FSDs correctly document this risk with clear escalation paths. This is a known risk, not an ignored one.

3. **Quality of Analysis:** The FSDs in this batch are generally excellent. Traceability matrices are thorough, anti-pattern analysis is comprehensive, and SQL design rationale is well-documented. The `account_customer_join` and `bond_maturity_schedule` FSDs stand out for particularly thoughtful edge case analysis.

4. **Proofmark Configs:** All FSDs start with the correct default (zero exclusions, zero fuzzy, 100% threshold) and provide contingency plans for known risks. This is the right approach.
