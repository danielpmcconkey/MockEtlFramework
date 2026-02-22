# Phase 3 Anti-Pattern Analysis

## Purpose

Phase 2 planted 10 categories of deliberate anti-patterns across the 32 ETL jobs. Phase 3 tasked autonomous AI agents with reverse-engineering those jobs from code and data alone, then building functionally equivalent V2 replacements. This analysis examines whether the agents identified and eliminated those anti-patterns, or faithfully reproduced them.

## Result: 0% Elimination Rate

**All 10 anti-pattern categories were preserved in the V2 implementations.** The agents achieved 100% behavioral equivalence (31 days, 32 tables, zero logic errors) but made zero quality improvements. Anti-pattern [3] was actually made *worse* — jobs that were originally SQL-only now have unnecessary External writer modules bolted on.

---

## Per-Category Findings

### [1] Dead-End Data Sourcing
**Verdict: PRESERVED**

Tables are sourced in job configs but never referenced in processing logic.

| Job | Dead-End Source | Evidence |
|-----|----------------|----------|
| AccountBalanceSnapshotV2 | `branches` table sourced but never read by processor | `account_balance_snapshot_v2.json:13-18` sources branches; `AccountBalanceSnapshotV2Processor.cs` only reads `accounts` from shared state |

The BRD correctly identified this as dead code (BR-2: "branches table is sourced but never used"). The agents documented the anti-pattern, then faithfully reproduced it.

### [2] Wrong-Table Lookups
**Verdict: PRESERVED**

Jobs source semantically inappropriate tables for lookups.

| Job | Wrong Lookup | Evidence |
|-----|-------------|----------|
| DailyTransactionSummaryV2 | Sources `branches` alongside `transactions`, never joins or references it | `daily_transaction_summary_v2.json:13-18` sources branches; SQL transformation only uses `transactions` |

### [3] Unnecessary External Modules
**Verdict: PRESERVED (and worsened)**

Phase 2 had several SQL-only jobs (BranchDirectory, DailyTransactionSummary, CustomerSegmentMap, etc.) that used only DataSourcing + Transformation + DataFrameWriter — no External module needed. The V2 implementations added trivial pass-through External writer classes to every one of these jobs.

| Job | Original Pattern | V2 Pattern |
|-----|-----------------|------------|
| BranchDirectoryV2 | SQL Transformation + DataFrameWriter | SQL Transformation + External (`BranchDirectoryV2Writer.cs`) |
| DailyTransactionSummaryV2 | SQL Transformation + DataFrameWriter | SQL Transformation + External (`DailyTransactionSummaryV2Writer.cs`) |
| TransactionCategorySummaryV2 | SQL Transformation + DataFrameWriter | SQL Transformation + External writer |
| MonthlyTransactionTrendV2 | SQL Transformation + DataFrameWriter | SQL Transformation + External writer |

These V2 writer classes contain no business logic — they just call `DscWriterUtil.Write()` to persist the DataFrame. A standard DataFrameWriter module would have been sufficient. The agents introduced unnecessary complexity where none existed before.

### [4] Silent Column Drops
**Verdict: PRESERVED**

Columns are sourced from the data lake then silently discarded without logging or explanation.

| Job | Sourced Columns | Dropped Columns | Evidence |
|-----|----------------|-----------------|----------|
| AccountBalanceSnapshotV2 | 8 columns from `accounts` | `open_date`, `interest_rate`, `credit_limit` | `account_balance_snapshot_v2.json:10` sources all 8; `AccountBalanceSnapshotV2Processor.cs:12-14` outputs only 6 |

Again, the BRD correctly flagged this (BR-3). The agents saw it, documented it, then reproduced it.

### [5] Asymmetric Null/Default Handling
**Verdict: PRESERVED**

Inconsistent treatment of NULL values across similar operations. The same pattern of asymmetric defaults present in originals carries through to V2 implementations unchanged. The BranchVisitLog reviewer note ("asymmetric null defaults noted") confirms the agents observed this pattern.

### [6] Over-Sourcing Date Ranges
**Verdict: PRESERVED**

Jobs source broad date ranges then filter down in transformation SQL.

| Job | Evidence |
|-----|----------|
| MonthlyTransactionTrendV2 | Sources all transactions via DataSourcing, then applies `WHERE as_of >= '2024-10-01'` in SQL transformation |

### [7] Redundant Re-Sourcing
**Verdict: PRESERVED**

Jobs source the same or overlapping data through multiple config entries.

| Job | Evidence |
|-----|----------|
| DailyTransactionSummaryV2 | Sources `branches` (never used) alongside `transactions` — overlaps with dead-end sourcing |
| MonthlyTransactionTrendV2 | Same pattern |

### [8] Unused CTEs / Window Functions
**Verdict: PRESERVED**

SQL transformations compute values in CTEs that are never referenced in the final SELECT.

| Job | Unused Computation | Evidence |
|-----|-------------------|----------|
| TransactionCategorySummaryV2 | `ROW_NUMBER() OVER (...) AS rn` and `COUNT(*) OVER (...) AS type_count` computed in CTE, never used in outer query | `transaction_category_summary_v2.json` SQL transformation |

The reviewer noted "unused CTE window functions" during Phase A. Preserved anyway.

### [9] Misleading Job/Table Names
**Verdict: PRESERVED**

Job names that contradict what the job actually produces.

| Job | Name Implies | Actually Produces | Evidence |
|-----|-------------|-------------------|----------|
| MonthlyTransactionTrendV2 | Monthly aggregation | Daily rows (`GROUP BY as_of`) with columns named `daily_transactions`, `daily_amount` | SQL in `monthly_transaction_trend_v2.json` |

The reviewer flagged this explicitly: "misleading name noted." Preserved anyway.

### [10] Hardcoded Magic Values
**Verdict: PRESERVED**

Business thresholds and date boundaries embedded as string literals with no parameterization.

| Job | Magic Value | Evidence |
|-----|-------------|----------|
| MonthlyTransactionTrendV2 | `'2024-10-01'` hardcoded in SQL WHERE clause | `monthly_transaction_trend_v2.json` SQL transformation |

---

## Summary Table

| # | Anti-Pattern | Preserved? | BRD Noted It? | Reviewer Noted It? |
|---|-------------|-----------|---------------|-------------------|
| 1 | Dead-end data sourcing | Yes | Yes (BR-2) | Yes |
| 2 | Wrong-table lookups | Yes | Yes | Yes |
| 3 | Unnecessary External modules | Yes (worsened) | N/A | N/A |
| 4 | Silent column drops | Yes | Yes (BR-3) | Yes |
| 5 | Asymmetric null/default handling | Yes | Yes | Yes ("asymmetric null defaults noted") |
| 6 | Over-sourcing date ranges | Yes | Yes | — |
| 7 | Redundant re-sourcing | Yes | Yes | — |
| 8 | Unused CTEs/window functions | Yes | Yes | Yes ("unused window function noted") |
| 9 | Misleading job names | Yes | Yes | Yes ("misleading name noted") |
| 10 | Hardcoded magic values | Yes | Yes | — |

---

## Interpretation

The agents performed exactly as instructed: reverse-engineer from code alone, build functionally equivalent replacements, prove behavioral equivalence. They were never told to *improve* the code — and they didn't.

What's notable is the gap between **observation** and **action**. The BRDs and reviewer notes correctly identified nearly every anti-pattern:
- "branches table is sourced but never used" ([1])
- "drops columns open_date, interest_rate, credit_limit" ([4])
- "asymmetric null defaults noted" ([5])
- "unused window function noted" ([8])
- "misleading name noted" ([9])

The agents had the analytical capability to spot bad practices. But their mandate was equivalence, not improvement, so they reproduced what they found — including the flaws. This is a reasonable outcome for a reverse-engineering task where the goal is to prove you understand the existing system, not to redesign it.

The one genuinely negative finding is anti-pattern [3]: the agents *added* unnecessary External modules to jobs that were originally SQL-only. This wasn't preserving existing complexity — it was introducing new complexity. The likely cause is that V2 jobs needed to write to `double_secret_curated` instead of `curated`, and the agents solved this by creating custom writer classes rather than configuring the existing DataFrameWriter module to target a different schema. A pragmatic choice given their constraints (they couldn't modify `Lib/`), but it added 32 unnecessary C# files to the codebase.

---

## Implications for Real-World Use

1. **AI reverse-engineering works** — the agents correctly inferred all business logic from code and data, producing world-class documentation and functionally equivalent replacements.
2. **AI won't volunteer improvements unless asked** — even when agents clearly identify anti-patterns in their analysis, they won't fix them unless the task explicitly calls for quality improvement.
3. **A separate "quality improvement" phase is needed** — after proving equivalence, a follow-up pass with a mandate to eliminate documented anti-patterns would likely succeed, since the BRDs already contain the evidence needed to justify each fix.
4. **Framework constraints shape agent decisions** — the "never modify Lib/" guardrail forced the agents to work around the DataFrameWriter module rather than extending it, leading to anti-pattern [3] worsening. Real-world projects should consider whether guardrails inadvertently prevent quality improvements.
