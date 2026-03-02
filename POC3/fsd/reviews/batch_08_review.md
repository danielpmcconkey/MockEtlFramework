# FSD Review — Batch 08

## Summary
- Jobs reviewed: 10
- PASS: 9
- FAIL: 1

## Per-Job Reviews

### monthly_transaction_trend
**Verdict: PASS**
- Traceability: PASS — All BRD requirements (BR-1 through BR-7) are addressed in the FSD traceability matrix with clear evidence citations. Edge cases (hardcoded date filter, unused branches source, CTE pass-through) are all mapped to anti-pattern eliminations.
- Module Chain: PASS — Tier 1 is correct. V1 is already Tier 1, and V2 stays Tier 1. All logic (COUNT, SUM, AVG, ROUND, GROUP BY, ORDER BY) is straightforward SQL. No reason to escalate.
- SQL/Logic: PASS — V2 SQL correctly reproduces the GROUP BY as_of aggregation with COUNT(*), ROUND(SUM(amount), 2), ROUND(AVG(amount), 2), and ORDER BY as_of. The CTE removal (AP8) and hardcoded date filter removal (AP10) are justified with solid reasoning — the CTE was a pure pass-through, and the date filter was redundant with framework date injection.
- Writer Config: PASS — source=monthly_trend, outputFile uses V2 path, includeHeader=true, writeMode=Append, lineEnding=LF, no trailerFormat. All match V1 exactly (except output path per V2 convention).
- Proofmark Config: PASS — csv reader, header_rows=1, trailer_rows=0, threshold=100.0. No exclusions or fuzzy columns needed (no non-deterministic fields).

Clean, thorough FSD. Good documentation of AP1, AP4, AP8, AP10 eliminations with evidence.

---

### overdraft_amount_distribution
**Verdict: PASS**
- Traceability: PASS — All BRD requirements (BR-1 through BR-8) and edge cases (EC-1 through EC-6) are addressed. W7 (inflated trailer) and W9 (overwrite) are explicitly documented. AP4 and AP6 eliminations are traced. The FSD adds OQ-1 (as_of format risk) and OQ-2 (SQLite REAL vs decimal) which show careful analysis.
- Module Chain: PASS — Tier 2 is well-justified. The CsvFileWriter's `{row_count}` token resolves to the output DataFrame count, not the input row count. Since W7 requires the inflated input count in the trailer, a minimal External is necessary. All business logic (bucketing, aggregation) is moved to SQL, making the External a pure I/O adapter. Good scalpel usage.
- SQL/Logic: PASS — The CASE/WHEN bucketing matches V1's if/else chain exactly (BR-1). HAVING COUNT(*) > 0 handles empty bucket exclusion (BR-2). CROSS JOIN for as_of from first row (BR-5) is correct given DataSourcing orders by as_of. The ORDER BY CASE enforcing dictionary insertion order (EC-5) is a nice detail. AP6 eliminated (foreach replaced by GROUP BY).
- Writer Config: PASS — Direct StreamWriter I/O matching V1. Output path uses V2 convention. Header, line ending (Environment.NewLine), trailer format, and Overwrite mode all documented and matching.
- Proofmark Config: PASS — csv reader, header_rows=1, trailer_rows=1, threshold=100.0. No exclusions initially, with a clear rationale for starting strict and adding fuzzy on total_amount if needed in Phase D.

The OQ-1 regarding as_of format (DateOnly.ToString() vs SQLite yyyy-MM-dd) is a HIGH-impact risk that is correctly identified and has a clear resolution path. Good work.

---

### overdraft_by_account_type
**Verdict: PASS**
- Traceability: PASS — All BRD requirements (BR-1 through BR-8) and edge cases (EC-1 through EC-5) are covered in the traceability matrix. W4 (integer division) and W9 (overwrite) are explicitly addressed. AP3, AP4, AP6 eliminations are well-documented.
- Module Chain: PASS — Tier 1 is justified. All V1 operations (dictionary lookup, counting, integer division, scalar extraction) map to standard SQL patterns. The account type lookup via MAX(as_of) subquery (BR-7) is a clean SQL equivalent of V1's dictionary overwrite. The INNER JOIN + LEFT JOIN pattern correctly handles "Unknown" type exclusion (EC-3).
- SQL/Logic: PASS — The SQL is well-structured with clear CTEs. account_type_counts counts ALL rows (inflated, matching V1 BR-2). last_seen_type uses MAX(as_of) to replicate dictionary overwrite (BR-7). overdraft_type_counts uses INNER JOIN to exclude unknown types (EC-3). Integer division in the final SELECT naturally replicates W4 since both operands are SQLite integers. COALESCE for NULL handling is consistent (AP5 addressed).
- Writer Config: PASS — ParquetFileWriter, source=output, outputDirectory uses V2 path, numParts=1, writeMode=Overwrite. All match V1.
- Proofmark Config: PASS — parquet reader, threshold=100.0, no exclusions or fuzzy. Rationale is sound: all columns are deterministic, overdraft_rate is always 0 (integer), and Proofmark is row-order-independent for Parquet.

OQ-1 regarding Parquet column type differences (INT32 vs INT64, DECIMAL vs INT64) is a real risk. The mitigation path (Tier 2 escalation for type casting) is documented. This is a Phase D concern, not a design issue. The FSD is honest about the risk rather than ignoring it.

---

### overdraft_customer_profile
**Verdict: PASS**
- Traceability: PASS — All BRD requirements (BR-1 through BR-10) and edge cases (EC-1 through EC-6) are mapped. W2 (weekend fallback) is the primary wrinkle, addressed in detail. W5 (banker's rounding) risk is acknowledged. AP1, AP3, AP4, AP6 all eliminated.
- Module Chain: PASS — Tier 1 is justified. The weekend fallback (W2) is implementable in SQL via strftime('%w') and date() functions. Customer lookup via GROUP BY/HAVING is SQL-native. The entire V1 External module (103 lines of procedural C#) is replaced by a single SQL Transformation. The W5 rounding risk is honestly acknowledged with a clear Tier 2 escalation path if Proofmark detects a midpoint mismatch.
- SQL/Logic: PASS — The `effective` CTE correctly computes weekend fallback using strftime. The `customer_lookup` CTE with `GROUP BY id HAVING as_of = MAX(as_of)` replicates V1's dictionary-overwrite semantics. The `filtered_events` CTE properly filters to target_date, and on weekends produces empty output matching V1 behavior (EC-1). COALESCE for missing customers (BR-8) is correct. The `ROUND(total * 1.0 / count, 2)` for avg_overdraft preserves the division logic.
- Writer Config: PASS — ParquetFileWriter, source=output, outputDirectory uses V2 path, numParts=1, writeMode=Overwrite. All match V1.
- Proofmark Config: PASS — parquet reader, threshold=100.0, no exclusions or fuzzy. Starting strict is correct. The rationale for not adding fuzzy on avg_overdraft (fix the code rather than mask the difference) shows the right approach.

The W5 risk acknowledgment is mature — identifying the SQLite ROUND vs Math.Round difference and providing a concrete escalation path. The SQL design for weekend fallback using `LIMIT 1` from overdraft_events is clever and correct for single-day execution.

---

### overdraft_daily_summary
**Verdict: PASS**
- Traceability: PASS — All BRD requirements (BR-1 through BR-9) and edge cases (EC-1 through EC-6) are covered in the traceability matrix. W3a (weekly total on Sunday) and W9 (overwrite) are addressed. AP1, AP3, AP4, AP6 all eliminated with clear evidence.
- Module Chain: PASS — Tier 1 is correct. The WEEKLY_TOTAL row is elegantly handled via UNION ALL with a WHERE clause gated on strftime('%w', MAX(as_of)) = '0'. No procedural logic needed.
- SQL/Logic: PASS — Daily GROUP BY as_of with COUNT(*), SUM(overdraft_amount), SUM(fee_amount) matches V1 (BR-1). No fee_waived filter (BR-2) is correctly preserved. event_date = as_of for daily rows (BR-3). The UNION ALL for WEEKLY_TOTAL (BR-4) correctly sums ALL rows (no GROUP BY) matching V1's groups.Values.Sum behavior (EC-1). The WHERE subquery for Sunday detection is correct. as_of column appears twice in output matching V1 (BR-3).
- Writer Config: PASS — CsvFileWriter, source=output, outputFile uses V2 path, includeHeader=true, trailerFormat=TRAILER|{row_count}|{date}, writeMode=Overwrite, lineEnding=LF. All match V1.
- Proofmark Config: PASS — csv reader, header_rows=1, trailer_rows=1, threshold=100.0. No exclusions or fuzzy. Rationale is sound.

OQ-2 about row ordering guarantee (SQLite GROUP BY without explicit ORDER BY) is a valid concern, but correctly identified as a Phase D validation item. The UNION ALL structure naturally preserves the ascending date order followed by WEEKLY_TOTAL.

---

### overdraft_fee_summary
**Verdict: PASS**
- Traceability: PASS — All BRD requirements (BR-1 through BR-8) are addressed. AP4 (unused columns) and AP8 (unused ROW_NUMBER CTE) are eliminated. Edge cases EC-1 through EC-5 are covered.
- Module Chain: PASS — Tier 1 is correct. V1 is already Tier 1 (framework-only), and V2 stays Tier 1 with a cleaner SQL query.
- SQL/Logic: PASS — The V2 SQL removes the dead ROW_NUMBER CTE (AP8) and selects directly from overdraft_events. The GROUP BY fee_waived, as_of with ROUND(SUM()), COUNT(*), ROUND(AVG()) matches V1 exactly. ORDER BY fee_waived preserves V1's sort order (BR-6). The output equivalence argument is solid — removing a CTE whose only unique column (rn) was never referenced cannot change the result.
- Writer Config: PASS — CsvFileWriter, source=fee_summary, outputFile uses V2 path, includeHeader=true, writeMode=Overwrite, lineEnding=LF, no trailerFormat. All match V1.
- Proofmark Config: PASS — csv reader, header_rows=1, trailer_rows=0, threshold=100.0. No exclusions or fuzzy. All output columns are deterministic aggregations.

OQ-2 about boolean representation (0/1 vs True/False in CSV) is a valid edge case to verify, but correctly identified as a framework-level concern that applies equally to V1 and V2. Clean, focused FSD.

---

### overdraft_recovery_rate
**Verdict: PASS**
- Traceability: PASS — All BRD requirements (BR-1 through BR-7) and edge cases (EC-1 through EC-6) are addressed. W4 (integer division) and W5 (banker's rounding) are both handled. AP3, AP4, AP6 are eliminated.
- Module Chain: PASS — Tier 1 is correct. The entire V1 External module is just counting and integer division — textbook SQL aggregation. SQLite naturally performs integer division when both operands are integers.
- SQL/Logic: PASS — SUM(CASE WHEN fee_waived = 0/1 THEN 1 ELSE 0 END) correctly counts charged and waived events. The integer division SUM(...)/COUNT(*) naturally replicates W4. The division-by-zero guard (CASE WHEN COUNT(*) = 0) handles the empty data case. MAX(as_of) correctly replicates V1's maxDate.ToString("yyyy-MM-dd"). The CREATE TABLE IF NOT EXISTS preamble is a pragmatic solution for the empty DataFrame edge case. The BRD correction (4 decimal places vs BRD's stated 2) shows attention to detail — following source code over BRD when they disagree.
- Writer Config: PASS — CsvFileWriter, source=output, outputFile uses V2 path, includeHeader=true, trailerFormat=TRAILER|{row_count}|{date}, writeMode=Overwrite, lineEnding=LF. All match V1.
- Proofmark Config: PASS — csv reader, header_rows=1, trailer_rows=1, threshold=100.0. No exclusions or fuzzy. Since recovery_rate is always 0 (integer), no floating-point concerns.

OQ-1 (empty DataFrame behavior difference on intermediate days) is well-analyzed. Since Overwrite mode means only the final day's output matters, and the final day has data, this is a non-issue for Proofmark. OQ-3 about multi-statement SQL support is a valid implementation-time concern. Solid FSD.

---

### payment_channel_mix
**Verdict: PASS**
- Traceability: PASS — All BRD requirements (BR-1 through BR-7) are addressed. Edge cases (empty channels, unused columns, multi-date ranges) are covered. AP4 is the only applicable anti-pattern and it is eliminated.
- Module Chain: PASS — Tier 1 is correct. V1 is already Tier 1, V2 stays Tier 1. The SQL is identical to V1's (it was already clean).
- SQL/Logic: PASS — The three-way UNION ALL with per-channel GROUP BY, COUNT(*), ROUND(SUM(amount), 2) is preserved exactly from V1. Table-qualified as_of references (BR-7) are maintained. No ORDER BY (BR-6) is preserved. The FSD correctly identifies that no SQL simplification is needed — the V1 SQL is already clean.
- Writer Config: PASS — ParquetFileWriter, source=output, outputDirectory uses V2 path, numParts=1, writeMode=Overwrite. All match V1.
- Proofmark Config: PASS — parquet reader, threshold=100.0, no exclusions or fuzzy. Both V1 and V2 execute the same SQL in the same SQLite engine, so results are bit-identical. Row ordering non-determinism is handled by Proofmark's set-based comparison.

Straightforward job, clean FSD. The only change from V1 is AP4 column reduction across three DataSourcing modules. No issues.

---

### peak_transaction_times
**Verdict: FAIL**
- Traceability: PASS — All BRD requirements (BR-1 through BR-10) are addressed. Edge cases are covered. W7 (inflated trailer) is the primary wrinkle. AP1, AP3 (partial), AP4, AP6 are handled.
- Module Chain: PASS — Tier 2 is justified for two reasons: (1) W7 requires inflated input count in trailer, which CsvFileWriter's {row_count} cannot produce; (2) UTF-8 BOM encoding mismatch between StreamWriter default and framework CsvFileWriter.
- SQL/Logic: FAIL — The SQL uses `strftime('%H', txn_timestamp)` to extract the hour from the timestamp. However, the BRD documents that V1's `txn_timestamp` is a PostgreSQL `timestamp` type. When DataSourcing reads this, it becomes a CLR `DateTime`. The Transformation module's `ToSqliteValue` method maps `DateTime` via `DateTime dt => dt.ToString("o")` (ISO 8601 format like `2024-10-01T14:30:00.0000000`). SQLite's `strftime('%H', ...)` can parse ISO 8601 strings and extract the hour, so this should work. However, the BRD (BR-3) states V1 uses `Math.Round(kvp.Value.total, 2)` which defaults to `MidpointRounding.ToEven` (banker's rounding), while the SQL uses `ROUND(SUM(CAST(amount AS REAL)), 2)` which uses round-half-away-from-zero. The FSD acknowledges this in OQ-1 as a risk, but the BRD explicitly states `Math.Round(kvp.Value.total, 2)` with no explicit MidpointRounding parameter. **Correction: `Math.Round(value, 2)` without a MidpointRounding parameter DOES default to `MidpointRounding.ToEven` in .NET.** The FSD correctly identifies this as W5 (banker's rounding) risk. This is a known potential mismatch, but the FSD does not flag W5 as an applicable wrinkle in its analysis, even though the BRD evidence clearly shows `Math.Round` usage. W5 should be explicitly listed as APPLICABLE (or at least RISK) rather than being buried only in OQ-1.
- Writer Config: PASS — Direct StreamWriter matching V1. Output path uses V2 convention. Header, LF line ending, trailer with inflated count, Overwrite mode, UTF-8 BOM all documented.
- Proofmark Config: FAIL — The FSD does not include any column overrides despite the identified W5/rounding risk. More critically, the `total_amount` column uses `ROUND(SUM(CAST(amount AS REAL)), 2)` in SQLite (round-half-away-from-zero) while V1 uses `Math.Round(decimal_sum, 2)` (banker's rounding on decimal accumulation). The FSD also uses `CAST(amount AS REAL)` which forces double arithmetic, while V1 accumulates with `Convert.ToDecimal(row["amount"])` in decimal. This is both a W5 issue AND a potential W6-like issue (decimal vs double accumulation). The Proofmark config should either start with a FUZZY tolerance on `total_amount`, or the FSD should explicitly acknowledge that this combination of rounding mode AND arithmetic type differences is a compound risk that may require Tier 2 resolution.

**Failure reason**: The FSD identifies two separate precision risks (decimal vs double accumulation, and MidpointRounding.ToEven vs round-half-away-from-zero) but does not call out W5 as an applicable wrinkle in its wrinkle replication section. The BRD clearly shows `Math.Round(kvp.Value.total, 2)` which triggers W5. The wrinkle analysis section (Section 6) only discusses W7, omitting W5 entirely. For a job where the V1 code uses both `decimal` accumulation and banker's rounding, and the V2 SQL uses `double` (REAL) accumulation with standard rounding, the compound precision risk needs explicit W5 acknowledgment in the wrinkle section, not just a buried OQ.

---

### portfolio_concentration
**Verdict: PASS**
- Traceability: PASS — All BRD requirements (BR-1 through BR-10) and edge cases are comprehensively addressed. W4 (integer division), W6 (double arithmetic), AP1, AP3, AP4, AP6, AP7 are all covered with detailed evidence and implementation plans.
- Module Chain: PASS — Tier 2 is well-justified with two concrete reasons: (1) Parquet type fidelity (SQLite returns long/INT64, V1 uses int/INT32 for customer_id and investment_id, and decimal/DECIMAL for sector_pct); (2) W4 integer division requires C# cast semantics ((int)doubleValue) which differs from SQLite's CAST to 64-bit integer. The External module is a true SCALPEL — only type coercion and W4, zero business logic.
- SQL/Logic: PASS — The SQL correctly implements: LEFT JOIN for sector lookup with COALESCE for Unknown default (BR-3, BR-10), subquery for customer total value (BR-2), GROUP BY (customer_id, investment_id, sector) for sector aggregation (BR-1), and MAX(as_of) for the date column (BR-7). The JOIN condition includes as_of matching (design note 4) which is the relationally correct approach. W6 is replicated through SQLite REAL arithmetic. The decision to NOT compute sector_pct in SQL (delegating to External for W4) is correct.
- Writer Config: PASS — ParquetFileWriter, source=output, outputDirectory uses V2 path, numParts=1, writeMode=Overwrite. All match V1.
- Proofmark Config: PASS — parquet reader, threshold=100.0, FUZZY on sector_value and total_value (tolerance=1e-10 absolute) for W6 double-accumulation order variance. This is appropriate — the fuzzy tolerance is extremely tight (well below any meaningful financial difference) but accommodates potential differences in floating-point addition order between V1's foreach loop and SQLite's SUM.

This is a complex job with two wrinkles (W4, W6) and multiple anti-pattern eliminations. The FSD handles it exceptionally well. The External module design (Section 11) is thorough, with clear algorithm steps, type mapping for Parquet schema equivalence, and named constants. The output type map table explicitly verifying CLR types against V1 is excellent. The only potential concern is the securities JOIN including as_of matching (design note 4), which differs from V1's dictionary-overwrite-across-all-dates behavior for multi-day ranges, but the FSD correctly notes this is moot for single-day auto-advance.

---

## Cross-Batch Observations

1. **Consistent quality**: 9 of 10 FSDs are thorough, well-structured, and show careful analysis of both wrinkles and anti-patterns.

2. **peak_transaction_times failure**: The W5 omission from the wrinkle section is the primary concern. The FSD identifies the risk in OQ-1 but does not promote it to an explicit wrinkle acknowledgment. Given that V1 uses `Math.Round` (banker's rounding) with `decimal` accumulation, and V2 uses SQLite `ROUND` (half-away-from-zero) with `REAL` (double) accumulation, this is a compound precision risk that deserves first-class treatment in the wrinkle section.

3. **Parquet type fidelity**: Multiple Parquet-output jobs (overdraft_by_account_type, portfolio_concentration) identify the SQLite type system mismatch (long vs int, etc.) as an open question. portfolio_concentration handles this by using Tier 2; overdraft_by_account_type documents a Tier 2 escalation path. Both approaches are valid.

4. **Decimal vs REAL**: Several jobs note the decimal-to-REAL conversion in SQLite as a potential precision risk. Most correctly start strict and document Phase D escalation paths. This is the right approach.
