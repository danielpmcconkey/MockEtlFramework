# Known Anti-Patterns Reference

This document catalogs every known anti-pattern in the V1 codebase. Your V2 implementations must produce **byte-identical output** to V1 while eliminating as many of these anti-patterns as possible.

There are two categories:

1. **Output-Affecting Wrinkles (W-codes):** These are bugs or quirks in V1 that change the output data. Your V2 **must reproduce the same output behavior** — but your code should be clean, well-documented, and make it obvious that the behavior is intentional replication, not accidental. Add a comment explaining what V1 does and why you're matching it.

2. **Code-Quality Anti-Patterns (AP-codes):** These are implementation problems that do NOT affect output data. Your V2 **must eliminate these.** There is no reason to reproduce bad code structure when cleaner alternatives produce the same output.

---

## Output-Affecting Wrinkles

These affect the actual data output. V2 must reproduce the same output, but with clean, intentional code.

| ID | Name | V1 Behavior | V2 Prescription |
|----|------|-------------|-----------------|
| W1 | Sunday skip | Returns empty DataFrame on Sundays (0-row output file) | Reproduce the behavior. Use a clear guard clause with a comment: `// V1 behavior: no output on Sundays`. |
| W2 | Weekend fallback | Uses previous Friday's data on Saturday/Sunday | Reproduce the behavior. Implement the date logic cleanly with a comment explaining the fallback. |
| W3a | End-of-week boundary | Appends WEEKLY_TOTAL summary row(s) on Sundays | Reproduce the behavior. Implement the boundary check and summary row generation cleanly. |
| W3b | End-of-month boundary | Appends MONTHLY_TOTAL summary row(s) on the last day of the month | Reproduce the behavior. Same approach as W3a. |
| W3c | End-of-quarter boundary | Appends QUARTERLY_TOTAL summary row(s) on fiscal quarter boundaries (e.g., Oct 31) | Reproduce the behavior. Same approach as W3a. |
| W4 | Integer division | Percentages computed as `int / int`, which truncates to 0 | **Reproduce the truncation in output**, but do NOT use integer division in your code. Instead, cast to decimal, compute the correct value, then explicitly truncate: `Math.Truncate((decimal)numerator / denominator)`. Add a comment: `// V1 bug: integer division truncates to 0. Replicated for output equivalence.` |
| W5 | Banker's rounding | Uses `MidpointRounding.ToEven` instead of `AwayFromZero` | Reproduce the behavior. Use `MidpointRounding.ToEven` explicitly. This is actually correct behavior in many financial contexts — just document the choice. |
| W6 | Double epsilon | Accumulates monetary values using `double` instead of `decimal`, causing floating-point errors (e.g., 0.1 + 0.2 ≠ 0.3) | **This is the hardest wrinkle.** If V1's double-precision errors are baked into the output, you must reproduce them. Use `double` for accumulation where V1 does, with a comment: `// V1 uses double (not decimal) for monetary accumulation. Epsilon errors in output are intentional V1 replication.` If you can produce identical output with `decimal`, do that instead and verify with Proofmark. |
| W7 | Trailer inflated count | External module writes CSV directly, trailer row counts INPUT rows (before grouping/filtering) instead of OUTPUT rows | Reproduce the trailer count behavior. Your trailer must show the same inflated count V1 shows. But use the framework's CsvFileWriter with trailer support rather than writing the file manually. |
| W8 | Trailer stale date | Trailer date is hardcoded to `"2024-10-01"` instead of using the current effective date | Reproduce the hardcoded date. Add a comment: `// V1 bug: trailer date hardcoded to 2024-10-01 regardless of effective date.` |
| W9 | Wrong writeMode | Overwrite mode used where Append is appropriate (loses prior days), or Append used where Overwrite is appropriate (creates duplicates) | Reproduce V1's write mode exactly. The wrong mode IS the V1 behavior. Document it: `// V1 uses Overwrite — prior days' data is lost on each run.` |
| W10 | Absurd numParts | Parquet output split into 50 parts for datasets with only a handful of rows | Reproduce the same numParts value. It doesn't affect data correctness. Add a comment noting it's excessive. |
| W12 | Header every append | External module writes CSV in Append mode and re-emits the header row on every execution | Reproduce the behavior. The repeated headers are part of V1's output. Use the framework's CsvFileWriter if it can replicate this behavior; otherwise, an External module is justified for this specific I/O quirk. |

---

## Code-Quality Anti-Patterns

These do NOT affect output data. **Eliminate all of them in V2.**

| ID | Name | V1 Problem | V2 Prescription |
|----|------|------------|-----------------|
| AP1 | Dead-end sourcing | Job config sources tables that are never used in processing logic | **Remove the unused DataSourcing entries from your V2 config.** Do not source data you don't need. |
| AP2 | Duplicated logic | Job re-derives data that another job already computed | **You cannot fix cross-job duplication within a single job's scope.** Document it with a comment, but implement the logic as needed for this job's output. |
| AP3 | Unnecessary External module | V1 uses a C# External module where the framework's DataSourcing + SQL Transformation + Writer chain would produce identical output | **Replace with framework modules.** Use DataSourcing to pull data, Transformation (SQL) for business logic, and the appropriate Writer module for output. External modules are a LAST RESORT — see Module Hierarchy below. |
| AP4 | Unused columns | Job config sources columns that are never referenced in processing | **Remove unused columns from your V2 DataSourcing config.** Only source what you need. |
| AP5 | Asymmetric NULLs | Inconsistent NULL/empty/default handling across similar fields (e.g., null risk → "Unknown" but null value → 0) | **Reproduce V1's exact NULL behavior in output** (this affects data), but document each asymmetry with a comment explaining what V1 does. |
| AP6 | Row-by-row iteration | C# `foreach` loop where a SQL set operation would produce the same result | **Replace with SQL in a Transformation module** where possible. If the logic requires an External module for other reasons, use LINQ or set-based operations instead of nested loops. |
| AP7 | Magic values | Hardcoded thresholds, strings, or boundaries without documentation (e.g., `amount > 500m`, `"High"`, `count < 2`) | **Use named constants** with descriptive names and comments explaining the business meaning. The VALUES stay the same (output must match), but they should be readable: `const decimal FraudThresholdAmount = 500m; // FDIC reporting threshold` |
| AP8 | Complex SQL / unused CTEs | SQL transformations with CTEs or window functions that compute values never used in the final result | **Simplify the SQL.** Remove unused CTEs and window functions. Only compute what the output needs. |
| AP9 | Misleading names | Job or output names that contradict what the job actually produces | **You cannot rename V1 jobs** (output filenames must match). Document the misleading name with a comment in your V2 config or FSD. |
| AP10 | Over-sourcing dates | DataSourcing pulls the full table, then SQL Transformation filters with a WHERE clause on dates | **Use the framework's effective date injection** (`__minEffectiveDate`, `__maxEffectiveDate`) in your DataSourcing config to limit the date range at the source. Only pull what you need. |

---

## Module Hierarchy

When designing your V2 implementation, use the simplest module chain that produces correct output:

### Tier 1: Framework Only (DEFAULT)
`DataSourcing → Transformation (SQL) → Writer`

Use this whenever the job's business logic can be expressed in SQL. This is the majority of jobs. The framework handles effective dates, file I/O, and output formatting. You write SQL.

### Tier 2: Framework + Minimal External (SCALPEL)
`DataSourcing → Transformation (SQL) → External (minimal logic) → Writer`

Use this when ONE specific operation can't be expressed in SQL (e.g., a calculation that requires procedural logic, snapshot fallback queries with DISTINCT ON that SQLite doesn't support). The External module handles ONLY that operation. DataSourcing still pulls the data. The Writer still handles output.

### Tier 3: Full External (LAST RESORT)
`External → Writer`

Use this ONLY when DataSourcing fundamentally cannot support the job's data access pattern (e.g., jobs that need to query across date ranges outside the effective date window, or jobs that need to query multiple tables with complex join patterns that cross snapshot boundaries).

**Even in Tier 3, the External module must be clean code:**
- Use `decimal` for monetary values (unless W6 requires `double` for output equivalence)
- Use named constants for thresholds (AP7)
- Use set-based operations where possible (AP6)
- Do not source unused data (AP1, AP4)
- Document any V1 behavior replication with comments

---

## How to Use This Document

1. **Before writing your FSD**, check which wrinkles and anti-patterns apply to your job. The BRD may reference specific codes (W4, AP1, etc.).
2. **Design your V2 module chain** using the Module Hierarchy above. Start at Tier 1 and only escalate if you have a specific reason.
3. **For each W-code**: reproduce the output behavior with clean, documented code.
4. **For each AP-code**: eliminate it. If you can't eliminate it (AP2 cross-job duplication, AP9 job naming), document why.
5. **When in doubt**: output equivalence wins. If you're unsure whether a change will affect output, keep V1's behavior and document it.
