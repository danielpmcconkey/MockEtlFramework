# FSD Review -- Batch 07

## Summary
- Jobs reviewed: 10
- PASS: 10
- FAIL: 0

---

## Per-Job Reviews

### inter_account_transfers
**Verdict: PASS**
- Traceability: PASS -- All 8 BRD business rules (BR-1 through BR-8) and all 6 edge cases are addressed in the FSD's traceability matrix (Section 9). AP1, AP3, AP4, AP6, AP10 are all discussed with clear V2 dispositions. W9 is noted and reproduced.
- Module Chain: PASS -- Tier 2 is well-justified. The greedy first-match-wins sequential matching algorithm (BR-8) is genuinely iteration-order-dependent and cannot be reliably expressed in a SQL self-join. The FSD correctly downgrades from V1's Tier 3 to Tier 2 by moving candidate pair generation into SQL and keeping only the greedy assignment in the External module.
- SQL/Logic: PASS -- The SQL JOIN conditions correctly implement BR-2 (same amount, same timestamp via CAST AS TEXT, different account_id). The ORDER BY (debit_txn_id, credit_txn_id) provides deterministic iteration order for the greedy algorithm. The External module algorithm (Section 10) correctly uses matchedDebits and matchedCredits HashSets to enforce BR-3 and BR-4.
- Writer Config: PASS -- ParquetFileWriter with source=output, numParts=1, writeMode=Overwrite matches BRD exactly. Output path correctly changed to double_secret_curated.
- Proofmark Config: PASS -- Parquet reader, threshold 100.0, no exclusions or fuzzy columns. Risk assessment for iteration-order sensitivity is thoughtful and includes a clear resolution path.

### investment_account_overview
**Verdict: PASS**
- Traceability: PASS -- All 10 BRD business rules (BR-1 through BR-10) are mapped in the traceability matrix (Section 9). W1 (Sunday skip) is reproduced. AP1, AP3, AP4, AP6 are all addressed with clear dispositions.
- Module Chain: PASS -- Tier 2 is justified. The FSD provides two concrete reasons why Tier 1 fails: (1) W1 Sunday skip requires `__maxEffectiveDate` from shared state, which is not accessible in SQLite SQL; (2) Transformation.RegisterTable skips empty DataFrames, causing SQL failures on days with no data. Both are valid framework limitations. DataSourcing handles data retrieval; CsvFileWriter handles output.
- SQL/Logic: PASS -- N/A for SQL (Tier 2 with External handling join logic). The External module pseudocode (Section 10) correctly implements: Sunday guard on DayOfWeek, empty-input guard for both investments and customers, Dictionary-based customer lookup with null coalescing to empty strings, 1:1 investment-to-output mapping, row-level as_of preservation (BR-5), and Convert.ToDecimal for current_value (BR-6).
- Writer Config: PASS -- CsvFileWriter with includeHeader=true, trailerFormat=TRAILER|{row_count}|{date}, writeMode=Overwrite, lineEnding=LF. All match BRD.
- Proofmark Config: PASS -- CSV reader, header_rows=1, trailer_rows=1, threshold=100.0, no exclusions or fuzzy columns. Appropriate for deterministic output.

### investment_risk_profile
**Verdict: PASS**
- Traceability: PASS -- All 9 BRD business rules (BR-1 through BR-9) are traced in Section 9. AP1, AP3, AP4, AP6, AP7 are eliminated. AP5 (asymmetric NULLs) is correctly reproduced in output.
- Module Chain: PASS -- Tier 1 is the correct choice. The FSD clearly demonstrates that every V1 operation (NULL coalescing, CASE thresholds, type casting, column pass-through) maps directly to SQL. No procedural logic exists. Excellent AP3 elimination.
- SQL/Logic: PASS -- **Critical BRD correction identified and handled correctly.** The FSD documents that the BRD states the High Value threshold as >250000, but V1 source code uses >200000. The FSD correctly follows V1 source code (200000) for output equivalence. The SQL CASE expression correctly uses COALESCE(current_value, 0) before threshold comparison to match V1's null-then-compare behavior. The three asymmetric NULL defaults (0, "Unknown", "") are all correctly implemented via COALESCE.
- Writer Config: PASS -- CsvFileWriter with includeHeader=true, writeMode=Overwrite, lineEnding=LF, no trailer. Matches BRD.
- Proofmark Config: PASS -- CSV reader, header_rows=1, trailer_rows=0, threshold=100.0. Correct for a no-trailer CSV.

Note: The BRD threshold correction (200000 vs 250000) is an important catch that prevents a data-level mismatch. Well done.

### large_transaction_log
**Verdict: PASS**
- Traceability: PASS -- All 10 BRD business rules (BR-1 through BR-10) are addressed in the traceability matrix (Section 9). AP1 (dead-end addresses), AP3, AP4, AP6, AP7 are all eliminated. The FSD honestly discusses the BR-6 edge case (empty accounts/customers producing different behavior in SQL vs V1) and documents why it's theoretical.
- Module Chain: PASS -- Tier 1 is correct. The amount filter, two-step LEFT JOIN lookup, and COALESCE defaults are all standard SQL. No procedural logic required.
- SQL/Logic: PASS -- The SQL correctly implements: (1) LEFT JOIN accounts ON account_id AND as_of, (2) LEFT JOIN customers ON customer_id AND as_of, (3) COALESCE(a.customer_id, 0) for missing accounts, (4) COALESCE(c.first_name/last_name, '') for missing customers, (5) WHERE t.amount > 500 (strict greater-than). The as_of join condition is justified by the single-day auto-advance execution model. The design notes about BR-9 (dictionary overwrite equivalence in single-day runs) are thorough and correct.
- Writer Config: PASS -- ParquetFileWriter with numParts=3, writeMode=Append. Matches BRD exactly.
- Proofmark Config: PASS -- Parquet reader, threshold=100.0. Row ordering risk is noted with a clear plan to add ORDER BY if needed.

### large_wire_report
**Verdict: PASS**
- Traceability: PASS -- All 7 BRD business rules (BR-1 through BR-7) are mapped in Section 9. W5 (banker's rounding) is addressed with a detailed risk analysis. AP3, AP6, AP7 are eliminated.
- Module Chain: PASS -- Tier 1 is correct. The filter, LEFT JOIN, NULL coalescing, and rounding are all SQL-expressible. The customer dedup CTE with ROW_NUMBER() OVER (PARTITION BY id ORDER BY as_of DESC) correctly replicates V1's last-write-wins dictionary behavior (BR-7).
- SQL/Logic: PASS -- The SQL design is solid. The double COALESCE layers (inner for NULL source data, outer for LEFT JOIN miss) correctly replicate V1's behavior. The CAST(customer_id AS INTEGER) matches V1's Convert.ToInt32. The W5 rounding analysis is honest: SQLite ROUND uses half-away-from-zero while V1 uses banker's rounding. The risk assessment correctly identifies this as LOW likelihood (requires exact .XX5 midpoint amounts) with a clear escalation path to Tier 2 if Proofmark fails.
- Writer Config: PASS -- CsvFileWriter with includeHeader=true, writeMode=Overwrite, lineEnding=LF, no trailer. Matches BRD.
- Proofmark Config: PASS -- CSV reader, header_rows=1, trailer_rows=0, threshold=100.0. Correct.

Note: The W5 rounding risk is real but acceptable at design time. The FSD's approach (start with SQLite ROUND, escalate to Tier 2 External if Proofmark fails) is the right strategy.

### loan_portfolio_snapshot
**Verdict: PASS**
- Traceability: PASS -- All 6 BRD business rules (BR-1 through BR-6) are mapped in Section 9. AP1 (dead-end branches), AP3, AP4, AP6 are all eliminated.
- Module Chain: PASS -- Tier 1 is the obvious correct choice. This is a pure column projection (SELECT with no WHERE, no JOIN, no aggregation). The V1 External module was entirely unnecessary.
- SQL/Logic: PASS -- The SQL is a straightforward SELECT of 8 columns from loan_accounts. No transforms, no filters, no joins. The empty DataFrame edge case is noted with appropriate mitigation discussion. The as_of auto-append behavior is correctly documented.
- Writer Config: PASS -- ParquetFileWriter with numParts=1, writeMode=Overwrite. Matches BRD exactly.
- Proofmark Config: PASS -- Parquet reader, threshold=100.0. For a pure pass-through job with no computed fields, strict comparison is appropriate.

### loan_risk_assessment
**Verdict: PASS**
- Traceability: PASS -- All 9 BRD business rules (BR-1 through BR-9) and relevant edge cases are traced in Section 12. AP1 (dead-end customers and segments), AP3, AP4, AP6, AP7 are all addressed. W9 is noted and reproduced. **Critical BRD correction: BR-2 threshold for "Low Risk" is >= 750 in V1 code, not >= 700 as BRD states.** The FSD correctly follows V1 source code.
- Module Chain: PASS -- Tier 2 is well-justified with two concrete reasons: (1) SQLite AVG returns double, but V1's Parquet schema uses decimal for avg_credit_score -- ParquetFileWriter maps these to different Parquet column types; (2) DateOnly type preservation -- SQLite stores as_of as text, but V1's Parquet schema uses DateOnly. The External module handles ONLY type casting and the empty-input guard. All business logic (LEFT JOIN, AVG, CASE) is in SQL.
- SQL/Logic: PASS -- The SQL LEFT JOIN with subquery AVG + GROUP BY customer_id correctly replicates V1's score-per-customer averaging across all bureaus and dates. The CASE expression uses corrected thresholds (750/650/550) with IS NOT NULL catch-all for "Very High Risk" and ELSE for "Unknown". The External module pseudocode correctly handles decimal casting, DateOnly reconstruction, and the compound empty-input guard.
- Writer Config: PASS -- ParquetFileWriter with numParts=2, writeMode=Overwrite. Matches BRD.
- Proofmark Config: PASS -- Parquet reader, threshold=100.0, no exclusions. The potential precision concern for double-to-decimal conversion is documented but expected to be a non-issue for integer credit scores averaged across small sets.

Note: The open questions (especially OQ-2 about empty table Transformation failure and OQ-4 about DBNull.Value vs null in Parquet) are well-flagged for the Developer to investigate. These don't affect the FSD's correctness -- they're implementation concerns to resolve during coding.

### marketing_eligible_customers
**Verdict: PASS**
- Traceability: PASS -- All 9 BRD business rules (BR-1 through BR-9) are addressed. **Critical BRD correction identified and documented:** The BRD claims only 2 channels (MARKETING_EMAIL, MARKETING_SMS) are required, but V1 source code at MarketingEligibleProcessor.cs:62-64 explicitly includes PUSH_NOTIFICATIONS in the requiredTypes set. The FSD correctly follows V1 code (3 channels) for output equivalence. W2 (weekend fallback) and W9 (Overwrite) are reproduced. AP4 (6 unused columns across 3 tables), AP6, AP7 are all addressed.
- Module Chain: PASS -- Tier 2 is justified. The weekend fallback logic requires procedural access to `__maxEffectiveDate` from shared state for conditional date computation. The conditional preference date filtering (filter only on weekends, process all rows on weekdays) is inherently procedural. DataSourcing handles all data retrieval; CsvFileWriter handles output.
- SQL/Logic: PASS -- N/A for SQL. The External module algorithm (Section 4) correctly implements: weekend fallback date computation, conditional preference date filtering, 3-channel eligibility check, customer existence validation, last-wins email lookup, empty-input guard, and targetDate as output as_of. The pseudocode is thorough and well-commented.
- Writer Config: PASS -- CsvFileWriter with includeHeader=true, writeMode=Overwrite, lineEnding=LF, no trailer. Matches BRD.
- Proofmark Config: PASS -- CSV reader, header_rows=1, trailer_rows=0, threshold=100.0. The row ordering risk (Dictionary iteration order) is noted but mitigated by matching DataSourcing insertion order.

Note: The BRD correction (3 channels vs 2) is a significant catch. The FSD's Appendix B comprehensively documents the discrepancy with evidence. This is exactly the kind of analysis a reviewer wants to see.

### merchant_category_directory
**Verdict: PASS**
- Traceability: PASS -- All 5 BRD business rules (BR-1 through BR-5) are traced in Section 9. AP1 (dead-end cards sourcing) is eliminated. W9 is discussed and correctly identified as intentional Append mode behavior, not a bug.
- Module Chain: PASS -- Tier 1 is correct. V1 is already Tier 1 (DataSourcing -> Transformation -> CsvFileWriter). The only structural change is removing the dead cards DataSourcing entry.
- SQL/Logic: PASS -- The SQL is identical to V1: `SELECT mc.mcc_code, mc.mcc_description, mc.risk_level, mc.as_of FROM merchant_categories mc`. Pure pass-through with no computation. The FSD correctly preserves the V1 SQL verbatim.
- Writer Config: PASS -- CsvFileWriter with includeHeader=true, writeMode=Append, lineEnding=LF, no trailer. Matches BRD exactly. The Append mode behavior is thoroughly documented: header written once on file creation, data accumulates (20 rows/day).
- Proofmark Config: PASS -- CSV reader, header_rows=1, trailer_rows=0, threshold=100.0. Correct for a deterministic pass-through with Append mode.

Note: The W12 analysis is a nice touch -- the FSD correctly identifies that W12 does NOT apply because V1 uses the framework's CsvFileWriter (which correctly suppresses headers on Append), not a manual External module CSV writer.

### monthly_revenue_breakdown
**Verdict: PASS**
- Traceability: PASS -- All 10 BRD business rules (BR-1 through BR-10) are traced in Section 13. W3c (end-of-quarter boundary), W5 (banker's rounding), and W9 (Overwrite mode) are all addressed. AP1 (dead-end customers), AP3, AP4, AP6, AP7, AP9 are all handled. The AP9 documentation (misleading "monthly" name) is appropriate.
- Module Chain: PASS -- Tier 2 is well-justified with two concrete reasons: (1) W5 banker's rounding requires C# Math.Round with MidpointRounding.ToEven, not available in SQLite; (2) BR-9 as_of from __maxEffectiveDate requires shared state access not available in SQL. The External module is truly minimal: reads pre-aggregated single-row DataFrame, applies rounding, injects as_of, conditionally appends Oct 31 rows.
- SQL/Logic: PASS -- The Transformation SQL correctly uses CROSS JOIN of two unconditional aggregation subqueries. The overdraft filter (`WHEN fee_waived = 0`) correctly handles the boolean-to-integer conversion in SQLite. COALESCE(SUM(...), 0) handles empty data. The External module pseudocode correctly applies banker's rounding, constructs 2 daily rows, and conditionally appends 2 quarterly rows on Oct 31 using the same daily values (BR-6). The empty DataFrame fallback (OQ-1) is addressed in the design.
- Writer Config: PASS -- CsvFileWriter with includeHeader=true, trailerFormat=TRAILER|{row_count}|{date}, writeMode=Overwrite, lineEnding=LF. Matches BRD exactly.
- Proofmark Config: PASS -- CSV reader, header_rows=1, trailer_rows=1, threshold=100.0. The potential SQLite REAL vs C# decimal precision concern for SUM aggregation is noted with a clear escalation path (fuzzy tolerance or Tier 3 escalation).

Note: OQ-3 (SQLite REAL vs C# decimal precision) is a legitimate concern. The FSD's approach (start strict, add fuzzy tolerance if Proofmark detects a difference) is the correct strategy. For simple sums of financial amounts, double precision should be sufficient, but it's good to have the fallback documented.

---

## Cross-Batch Observations

1. **BRD Corrections:** Two jobs in this batch (investment_risk_profile and marketing_eligible_customers) identified material BRD errors and correctly chose to follow V1 source code over the BRD for output equivalence. loan_risk_assessment also caught a threshold discrepancy (700 vs 750). All three FSDs document the corrections with clear V1 evidence. This is excellent work.

2. **Tier Justifications:** The tier selections across the batch are well-reasoned. Jobs that could be Tier 1 are Tier 1 (investment_risk_profile, large_transaction_log, loan_portfolio_snapshot, merchant_category_directory). Jobs that need Tier 2 provide concrete, verifiable reasons (shared state access, SQLite limitations, type fidelity, greedy algorithms). No job uses Tier 3.

3. **Empty DataFrame Edge Case:** Multiple FSDs (large_wire_report, loan_portfolio_snapshot, loan_risk_assessment, monthly_revenue_breakdown) identify the Transformation.RegisterTable empty-DataFrame skip behavior as a potential runtime issue. This is a known framework limitation. All FSDs handle it appropriately -- either documenting it as unlikely in production data or designing External module fallbacks.

4. **SQLite Type Fidelity:** The loan_risk_assessment FSD's analysis of decimal vs double type propagation through SQLite is thorough and identifies a real concern for Parquet schema matching. The Tier 2 solution (External module for type casting) is the correct approach.

5. **Proofmark Configs:** All 10 jobs start with strict configurations (threshold=100.0, no exclusions, no fuzzy columns) per best practices. Risk areas are documented with clear escalation paths for Phase D resolution. This is the right approach.
