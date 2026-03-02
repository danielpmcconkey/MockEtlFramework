# FSD Review -- Batch 04

## Summary
- Jobs reviewed: 10
- PASS: 8
- FAIL: 2

---

## Per-Job Reviews

### customer_360_snapshot
**Verdict: PASS**
- Traceability: PASS -- Every BRD requirement (BR-1 through BR-11) is addressed in the FSD. W2 is reproduced, AP1/AP3/AP4/AP6 are eliminated. All mappings are present in the Section 9 traceability matrix with consistent evidence citations.
- Module Chain: PASS -- Tier 1 selection is well-justified. The FSD correctly identifies that all V1 business logic (weekend fallback, joins, GROUP BY, COALESCE) maps cleanly to SQL. The argument that `strftime('%w')` handles day-of-week extraction is valid.
- SQL/Logic: PASS -- The SQL in Section 5 correctly implements weekend fallback via `date_calc` CTE, per-customer aggregation via LEFT JOINs to `account_agg`, `card_agg`, `investment_agg`, and COALESCE for default zeros. The logic matches all BRD business rules. The rounding discussion (W5) is thorough and honest about the SQLite vs C# difference, with a reasonable risk assessment and contingency plan.
- Writer Config: PASS -- ParquetFileWriter with numParts=1, writeMode=Overwrite matches V1 exactly. Output path correctly changed to `double_secret_curated`.
- Proofmark Config: PASS -- Strict config with `reader: parquet`, `threshold: 100.0`, no exclusions, no fuzzy. Appropriate contingency documented for potential rounding issues. Config schema matches CONFIG_GUIDE.md.

**Notes:** The FSD correctly identifies and documents the edge case where empty DataFrames could cause SQLite table-not-found errors (Transformation.cs:46 behavior). This is a known framework limitation and the risk is assessed as LOW. Solid work overall.

---

### customer_address_deltas
**Verdict: PASS**
- Traceability: PASS -- All 16 BRD requirements (BR-1 through BR-16) are addressed in the FSD. Every entry in the traceability matrix (Section 9) maps to a BRD requirement. AP6 is partially addressed with justification (dictionary-based delta comparison is inherently procedural). AP7 is eliminated (named constants for CompareFields and DateFormat).
- Module Chain: PASS -- Tier 3 selection is well-justified with two independent reasons: (1) cross-date data access incompatible with DataSourcing's single-day effective date window, and (2) PostgreSQL-specific `DISTINCT ON` for customer name lookup. Both reasons individually justify an External module. The FSD correctly notes that even with Tier 3, anti-pattern remediation is applied.
- SQL/Logic: PASS -- Not applicable for the Transformation module (Tier 3), but the PostgreSQL queries in Section 5 correctly implement address fetching (Query 1) and customer name lookup with `DISTINCT ON` (Query 2). The External module design in Section 10 is thorough with detailed pseudocode covering all business rules.
- Writer Config: PASS -- ParquetFileWriter with numParts=1, writeMode=Append matches V1 exactly. Output path correctly changed.
- Proofmark Config: PASS -- Strict config with `reader: parquet`, `threshold: 100.0`, no exclusions, no fuzzy. Justified by the fact that all 13 output columns are deterministic.

**Notes:** Good handling of the sentinel row behavior for baseline and no-delta cases. The `Normalize` and `FormatDate` helper method designs correctly replicate V1's string comparison semantics.

---

### customer_address_history
**Verdict: PASS**
- Traceability: PASS -- All 7 BRD requirements (BR-1 through BR-7) are addressed. Both open questions (OQ-1 branches unused, OQ-2 address_id excluded) are resolved in the FSD. AP1 (dead-end branches sourcing) eliminated. AP4 (unused address_id column) eliminated. AP8 (unnecessary subquery) eliminated.
- Module Chain: PASS -- Tier 1 is the obvious correct choice. The logic is a straightforward filter + order, no procedural operations needed.
- SQL/Logic: PASS -- V2 SQL correctly simplifies the V1 nested subquery to a flat `SELECT ... FROM addresses a WHERE a.customer_id IS NOT NULL ORDER BY a.customer_id`. The output equivalence argument is sound -- removing a semantically empty subquery wrapper produces identical results.
- Writer Config: PASS -- ParquetFileWriter with numParts=2, writeMode=Append matches V1 exactly. The FSD includes a detailed (and corrected) analysis of ParquetFileWriter Append mode behavior, noting that same-named part files get overwritten. This is good attention to framework internals.
- Proofmark Config: PASS -- Strict config with `reader: parquet`, `threshold: 100.0`, no exclusions, no fuzzy. Appropriate for a pass-through projection job.

**Notes:** Clean, straightforward FSD for a simple job. The Append mode analysis in Section 7 is a nice detail showing the architect understands the framework's actual file I/O behavior.

---

### customer_attrition_signals
**Verdict: PASS**
- Traceability: PASS -- All 10 BRD requirements (BR-1 through BR-10) are addressed. W5 (banker's rounding) and W6 (double epsilon) are correctly identified and reproduced. AP4 (unused `amount` column in transactions) and AP6 (row-by-row iteration) are eliminated. AP7 (magic values) is eliminated with named constants. The **critical BRD weight discrepancy** (dormancy=40 vs declining_txn=35 in code, swapped in BRD text) is caught and documented with the correct resolution: follow the source code, not the BRD text. This is exactly the right call for output equivalence.
- Module Chain: PASS -- Tier 2 selection is well-justified with three independent reasons: (1) banker's rounding for avg_balance requires C# `decimal`, not SQLite `double`; (2) Parquet schema requires proper C# types (int, decimal, double) that SQLite cannot provide; (3) `__maxEffectiveDate` (DateOnly) is in shared state, not accessible from SQL. The aggregation portion is correctly moved to SQL, keeping the External minimal.
- SQL/Logic: PASS -- The SQL correctly implements LEFT JOINs for customer-to-account stats and customer-to-transaction counts, with INNER JOIN for the transactions-to-accounts mapping (correctly dropping transactions with unknown account_id). COALESCE for defaults. The External module design correctly handles type conversions from SQLite types (long, double) to proper C# types (int, decimal).
- Writer Config: PASS -- ParquetFileWriter with numParts=1, writeMode=Overwrite matches V1 exactly.
- Proofmark Config: PASS -- Strict config starting point is appropriate. The analysis that W6 double arithmetic produces exact results for these specific weights (40.0, 35.0, 25.0 with 0/1 binary factors) is mathematically sound. Good contingency documented.

**Notes:** The BRD weight discrepancy catch is excellent work. The FSD correctly prioritizes source code over BRD text for output equivalence. The scoring formula is clearly documented with named constants. The type conversion notes for SQLite-to-C# are thorough.

---

### customer_branch_activity
**Verdict: PASS**
- Traceability: PASS -- All 10 BRD requirements (BR-1 through BR-10) are addressed. AP1 (dead-end branches table) eliminated. AP3 (partially eliminated -- External retained for framework limitation). AP4 (unused columns removed). AP6 (foreach loops replaced with LINQ). All output-affecting behaviors (BR-5 single as_of, BR-6 null names, BR-9 dictionary insertion order, BR-10 cross-date aggregation) are preserved and documented.
- Module Chain: PASS -- Tier 2 selection is justified by a legitimate framework limitation: `Transformation.RegisterTable` skips empty DataFrames (Transformation.cs:46), which would cause SQL to fail with a table-not-found error when either source is empty. BR-3 and BR-4 require returning an empty DataFrame with correct schema in these cases. The justification includes specific code references.
- SQL/Logic: PASS -- While no SQL is used (Tier 2 with LINQ in External), the logical equivalent SQL is documented for traceability. The LINQ implementation in Section 10 correctly replicates V1 behavior: GroupBy preserving first-encounter order, last-write-wins for customer names, single as_of from first row.
- Writer Config: PASS -- CsvFileWriter with includeHeader=true, writeMode=Append, lineEnding=CRLF, no trailerFormat matches V1 exactly.
- Proofmark Config: PASS -- CSV reader with header_rows=1, trailer_rows=0, threshold=100.0. Correct for a CSV with header, no trailer, Append mode.

**Notes:** The CsvFileWriter Append behavior note (header only written on first execution) is a useful detail. The External module pseudocode is clean and well-commented with BRD requirement references.

---

### customer_compliance_risk
**Verdict: FAIL**
- Traceability: PASS -- All 12 BRD requirements (BR-1 through BR-12) are addressed. The account_id/customer_id mismatch bug handling (hardcoding high_txn_count=0) is well-reasoned and properly documented.
- Module Chain: PASS -- Tier 1 is correctly chosen. The FSD provides a comprehensive argument for why all V1 logic maps to SQL. AP1 (dead-end transactions table), AP3 (unnecessary External), AP4 (unused columns), AP6 (row-by-row iteration), AP7 (magic values) are all addressed.
- SQL/Logic: PASS -- The SQL correctly implements the business rules with LEFT JOINs for compliance event and wire transfer counts, hardcoded 0 for high_txn_count, and the risk score formula.
- Writer Config: PASS -- CsvFileWriter with includeHeader=true, writeMode=Overwrite, lineEnding=LF matches V1 exactly.
- Proofmark Config: **FAIL** -- The FSD states that "SQLite's `ROUND()` function uses banker's rounding by default" (Section 3, W5 row, and Section 5 note #6). **This is incorrect.** SQLite's `ROUND()` uses half-away-from-zero rounding (standard mathematical rounding), NOT banker's rounding (MidpointRounding.ToEven). The V1 code explicitly uses `Math.Round(riskScore, 2, MidpointRounding.ToEven)`. While the FSD notes this is a non-issue in practice because all risk scores are integer-valued (BR-8), the documented claim about SQLite's rounding behavior is factually wrong. This is a documentation error that could mislead the developer. Additionally, if the data ever changed to produce non-integer risk scores, this incorrect assumption would cause output divergence.

**Material Issue:** The incorrect claim about SQLite ROUND() behavior appears in two places (Anti-Pattern Analysis W5 row and SQL Design note #6). While BR-8 makes this a non-issue for current data, the FSD is presenting incorrect technical information as the basis for design decisions. The developer reading this FSD could apply this incorrect assumption to other jobs. This should be corrected to accurately state that SQLite ROUND() uses half-away-from-zero, but that the practical impact is nil because all inputs produce integer results.

---

### customer_contact_info
**Verdict: PASS**
- Traceability: PASS -- All 6 BRD requirements (BR-1 through BR-6) are addressed. AP1 (dead-end segments sourcing) is eliminated. The traceability matrix in Section 9 covers all requirements and writer configuration parameters.
- Module Chain: PASS -- Tier 1 is the obvious correct choice. V1 itself is already framework-only (no External module). The only change is removing the dead-end segments DataSourcing. The SQL is identical to V1.
- SQL/Logic: PASS -- SQL correctly uses UNION ALL with 'Phone'/'Email' literals, aliases for contact_subtype and contact_value, and ORDER BY customer_id, contact_type, contact_subtype. Exact match to V1 SQL.
- Writer Config: PASS -- ParquetFileWriter with numParts=2, writeMode=Append matches V1 exactly.
- Proofmark Config: PASS -- Strict config with `reader: parquet`, `threshold: 100.0`, no exclusions, no fuzzy. Appropriate for a pass-through UNION ALL job with no floating-point operations.

**Notes:** The AP4 analysis is nuanced -- phone_id and email_id are sourced but not in the output SELECT. The FSD correctly decides to retain them to match V1's DataSourcing fingerprint, which is a reasonable conservative choice.

---

### customer_contactability
**Verdict: PASS**
- Traceability: PASS -- All 9 BRD requirements (BR-1 through BR-9) are addressed. W2 (weekend fallback) and W9 (Overwrite mode) are preserved. AP1 (dead-end segments) and AP4 (unused prefix/suffix columns) are eliminated. AP3 is partially addressed (External retained for weekend logic). AP6 is retained with justification (dictionary-based joins within External).
- Module Chain: PASS -- Tier 2 selection is justified by the need to access `__maxEffectiveDate` from shared state for weekend fallback computation, and the conditional date filtering logic that depends on comparing computed dates. The FSD correctly explains that `__maxEffectiveDate` is not accessible as a scalar value from within a Transformation SQL query.
- SQL/Logic: PASS -- No SQL (Tier 2 External), but the algorithm in Section 5 correctly implements all business rules: weekend fallback (BR-3), conditional date filtering (BR-4), MARKETING_EMAIL opt-in (BR-1), triple-lookup requirement (BR-2), last-wins dictionary overwrite (BR-8). The pseudocode in Section 10 is complete and well-commented.
- Writer Config: PASS -- ParquetFileWriter with numParts=1, writeMode=Overwrite matches V1 exactly.
- Proofmark Config: PASS -- Strict config starting point is appropriate. The analysis of email/phone non-determinism (same DataSourcing produces same row order, making last-wins behavior consistent between V1 and V2) is sound. The BRD notes non-deterministic fields for email/phone but the FSD correctly argues these are deterministic BETWEEN V1 and V2 given identical DataSourcing.

**Notes:** Good edge case coverage in the appendix. The weekend fallback pseudocode is clean and readable.

---

### customer_credit_summary
**Verdict: PASS**
- Traceability: PASS -- All 10 BRD requirements (BR-1 through BR-10) are addressed. W9 (Overwrite mode) is preserved. AP1 (dead-end segments) is eliminated. AP4 (unused columns across 3 tables -- 7 columns total) is eliminated. AP6 (foreach loops replaced with LINQ) is eliminated.
- Module Chain: PASS -- Tier 2 selection is well-justified by the `decimal` precision requirement for `avg_credit_score`. The FSD provides a concrete example: three scores [750, 680, 710] averaging to different string representations in C# decimal vs SQLite double. This is a legitimate technical reason that Tier 1 cannot satisfy. DataSourcing still handles data fetching, CsvFileWriter handles output -- the External is truly minimal.
- SQL/Logic: PASS -- No SQL (Tier 2 External), but the pseudocode in Section 10 correctly implements all business rules using LINQ: `ToLookup` for credit scores (preserving the ability to compute decimal Average), `GroupBy().ToDictionary()` for loans and accounts. `DBNull.Value` for missing credit scores correctly matches V1's behavior and CSV rendering.
- Writer Config: PASS -- CsvFileWriter with includeHeader=true, writeMode=Overwrite, lineEnding=LF, no trailerFormat matches V1 exactly.
- Proofmark Config: PASS -- CSV reader with header_rows=1, trailer_rows=0, threshold=100.0. No exclusions or fuzzy needed since V2 uses the same decimal arithmetic as V1.

**Notes:** The Tier 2 justification is one of the strongest in this batch. The decimal precision argument is concrete and verifiable. The AP4 cleanup is thorough (7 unused columns across 3 tables).

---

### customer_demographics
**Verdict: FAIL**
- Traceability: PASS -- All 11 BRD requirements (BR-1 through BR-11) and 3 open questions are addressed. AP1 (segments), AP3 (unnecessary External), AP4 (unused columns), AP6 (foreach loops) are all eliminated. Edge cases (under-18 age, NULL birthdate, multiple phones/emails) are documented.
- Module Chain: PASS -- Tier 1 selection is justified. The age calculation, age bracket CASE, and first-phone/email selection are all expressible in SQL. The `MIN(rowid)` technique for first-encountered selection in SQLite is clever and sound.
- SQL/Logic: **FAIL** -- The age bracket SQL has an off-by-one issue relative to the BRD. The BRD states the V1 switch expression has the bracket "26-35" covering ages 26 through 35 inclusive. Looking at the BRD (BR-2), it says `< 26: "18-25"`, `26-35: "26-35"`, etc. The V1 code at [CustomerDemographicsBuilder.cs:68-76] uses a switch expression. The FSD's SQL uses `< 26` for the first bracket and `<= 35` for the second. However, looking more carefully at the V1 code evidence, the switch expression pattern `< 26` then `<= 35` then `<= 45` etc. is exactly what the FSD implements. So the SQL logic itself matches. **However, there is a more material concern:** The FSD uses `strftime('%m-%d', c.birthdate) > strftime('%m-%d', c.as_of)` for the birthday adjustment, but the FSD itself identifies in the Risks appendix (Risk 2) that this could diverge from V1's `DateOnly` comparison for leap year Feb 29 birthdays. The FSD acknowledges this risk but does not resolve it -- it defers to "Verify with Proofmark." For a Feb 29 birthdate with a non-leap-year as_of date, V1's `birthdate > asOfDate.AddYears(-age)` would produce `March 1` (since Feb 29 doesn't exist in non-leap years), whereas the SQL compares raw `02-29` strings. This means V1 would consider the birthday as having occurred (03-01 is NOT > the as_of date in Feb), while V2 would consider it as NOT having occurred (02-29 > 02-28). This is a potential 1-year age discrepancy for Feb 29 birthdays in non-leap years, which would produce different output. The risk is acknowledged but not mitigated.
- Writer Config: PASS -- CsvFileWriter with includeHeader=true, writeMode=Overwrite, lineEnding=CRLF matches V1 exactly.
- Proofmark Config: PASS -- CSV reader with header_rows=1, trailer_rows=0, threshold=100.0. Appropriate for a job with no floating-point arithmetic.

**Material Issue:** The leap year birthday edge case (Risk 2 in the FSD's own appendix) is a known potential source of output divergence that is documented but not addressed. While the FSD honestly identifies this risk, the fact that it could produce incorrect output for Feb 29 birthdays means the SQL logic does not guarantee output equivalence. The FSD should either:
1. Verify that no customers in the data have Feb 29 birthdates (a database query would settle this), or
2. Add a leap-year special case to the SQL CASE expression, or
3. Escalate to Tier 2 with a minimal External module for the age calculation only.

Simply deferring to "verify with Proofmark" means the developer will implement code that may produce wrong output, only to discover it during Phase D comparison. The FSD should resolve known risks before implementation, not punt them to testing.

---

## Summary Table

| Job | Traceability | Module Chain | SQL/Logic | Writer Config | Proofmark Config | Verdict |
|-----|-------------|-------------|-----------|---------------|------------------|---------|
| customer_360_snapshot | PASS | PASS | PASS | PASS | PASS | **PASS** |
| customer_address_deltas | PASS | PASS | PASS | PASS | PASS | **PASS** |
| customer_address_history | PASS | PASS | PASS | PASS | PASS | **PASS** |
| customer_attrition_signals | PASS | PASS | PASS | PASS | PASS | **PASS** |
| customer_branch_activity | PASS | PASS | PASS | PASS | PASS | **PASS** |
| customer_compliance_risk | PASS | PASS | PASS | PASS | FAIL | **FAIL** |
| customer_contact_info | PASS | PASS | PASS | PASS | PASS | **PASS** |
| customer_contactability | PASS | PASS | PASS | PASS | PASS | **PASS** |
| customer_credit_summary | PASS | PASS | PASS | PASS | PASS | **PASS** |
| customer_demographics | PASS | PASS | FAIL | PASS | PASS | **FAIL** |

## Required Revisions

### customer_compliance_risk
1. **Correct the SQLite ROUND() behavior claim.** Replace all instances of "SQLite ROUND uses banker's rounding" with the accurate statement: "SQLite ROUND uses half-away-from-zero rounding (standard mathematical rounding), NOT banker's rounding (MidpointRounding.ToEven)." Preserve the existing analysis that this has no practical impact because all risk scores are integer-valued (BR-8), but ensure the technical claim is factually correct.
2. **Update Anti-Pattern Analysis W5 row** and **SQL Design note #6** to reflect the corrected rounding behavior.

### customer_demographics
1. **Resolve the Feb 29 leap year birthday risk** before implementation. The FSD identifies this as Risk 2 but defers resolution. Options:
   - Query the database for Feb 29 birthdates: `SELECT COUNT(*) FROM datalake.customers WHERE EXTRACT(MONTH FROM birthdate) = 2 AND EXTRACT(DAY FROM birthdate) = 29`. If zero, document this as evidence and note the SQL is safe for current data.
   - If Feb 29 birthdates exist, modify the SQL to handle the leap year edge case, or escalate the age calculation to a Tier 2 External module.
2. The FSD must not present a known output-affecting risk as "verify with Proofmark" -- known risks should be resolved at design time.
