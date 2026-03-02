# LoanRiskAssessment -- Functional Specification Document

## 1. Job Summary

**Job Name:** LoanRiskAssessmentV2
**Config File:** `loan_risk_assessment_v2.json`
**Module Tier:** Tier 2 (Framework + Minimal External)

This job enriches loan account records with each borrower's average credit score (computed across all bureaus and all as_of dates in the effective date range), assigns a risk tier classification based on credit score thresholds, and writes the result as a 2-part Parquet file. Loans whose customer has no credit scores receive a null average score and "Unknown" risk tier. If either the loan_accounts or credit_scores source table is empty, the entire output is empty.

### Tier Justification

Tier 1 (pure SQL) is insufficient for two reasons:

1. **Decimal precision for avg_credit_score:** V1 computes the average credit score using C# `decimal` arithmetic via LINQ `Average()` [LoanRiskCalculator.cs:41]. SQLite's `AVG()` returns a `REAL` (IEEE 754 double), which has only ~15-17 significant digits vs decimal's 28-29. The `ParquetFileWriter.GetParquetType` method [ParquetFileWriter.cs:98] maps `decimal` to `typeof(decimal?)` and `double` to `typeof(double?)`, producing entirely different Parquet column types and potentially different values. This would break byte-identical output.

2. **DateOnly type preservation for as_of:** V1 passes through `loanRow["as_of"]` directly from the DataSourcing DataFrame [LoanRiskCalculator.cs:82], which is a `DateOnly` object. `Transformation.ToSqliteValue` converts `DateOnly` to a `"yyyy-MM-dd"` string [Transformation.cs:110], and `ReaderToDataFrame` returns it as a string [Transformation.cs:90]. `ParquetFileWriter.GetParquetType` maps `DateOnly` to `typeof(DateOnly?)` [ParquetFileWriter.cs:100] and strings to `typeof(string)` [ParquetFileWriter.cs:102], producing a different Parquet column type.

The External module handles ONLY:
1. Decimal type cast for avg_credit_score (double from SQLite -> decimal to match V1 Parquet schema)
2. DateOnly reconstruction for the as_of column (string from SQLite -> DateOnly to match V1 Parquet schema)
3. Empty-input guard (BR-6) when source tables are empty and Transformation cannot execute

All joining, grouping, aggregation, and risk tier classification is handled by SQL in the Transformation step.

---

## 2. V2 Module Chain

```
DataSourcing (loan_accounts)
    -> DataSourcing (credit_scores)
        -> Transformation (SQL: LEFT JOIN + AVG + CASE)
            -> External (decimal cast + DateOnly + empty guard)
                -> ParquetFileWriter
```

### Module 1: DataSourcing -- loan_accounts
- **resultName:** `loan_accounts`
- **schema:** `datalake`
- **table:** `loan_accounts`
- **columns:** `["loan_id", "customer_id", "loan_type", "current_balance", "interest_rate", "loan_status"]`
- Effective dates injected by executor via shared state.
- The `as_of` column is automatically appended by DataSourcing when not in the caller's column list.

### Module 2: DataSourcing -- credit_scores
- **resultName:** `credit_scores`
- **schema:** `datalake`
- **table:** `credit_scores`
- **columns:** `["customer_id", "score"]`
- Effective dates injected by executor via shared state.
- V1 sources `credit_score_id` and `bureau` as well [loan_risk_assessment.json:17], but neither is used in the External module [LoanRiskCalculator.cs:30-31]. V2 eliminates these unused columns (AP4).

### Module 3: Transformation (SQL)
- **resultName:** `sql_output`
- **sql:** See Section 4 for full SQL.
- Performs: LEFT JOIN between loan_accounts and aggregated credit_scores subquery, computes average credit score per customer, assigns risk tiers via CASE expression.
- Result stored as `sql_output` (not `output`) because the External module needs to post-process types before the writer consumes `output`.

### Module 4: External (LoanRiskAssessmentV2Processor)
- **assemblyPath:** `/workspace/MockEtlFramework/ExternalModules/bin/Debug/net8.0/ExternalModules.dll`
- **typeName:** `ExternalModules.LoanRiskAssessmentV2Processor`
- Reads `sql_output` DataFrame from shared state.
- Casts `avg_credit_score` from `double` (SQLite REAL) to `decimal` (or `DBNull.Value` if null).
- Reconstructs `as_of` as a `DateOnly` from its SQLite text representation.
- Produces `output` DataFrame.

### Module 5: ParquetFileWriter
- **source:** `output`
- **outputDirectory:** `Output/double_secret_curated/loan_risk_assessment/`
- **numParts:** 2
- **writeMode:** Overwrite

---

## 3. DataSourcing Config

### loan_accounts

| Property | Value |
|----------|-------|
| resultName | `loan_accounts` |
| schema | `datalake` |
| table | `loan_accounts` |
| columns | `["loan_id", "customer_id", "loan_type", "current_balance", "interest_rate", "loan_status"]` |

Effective dates: Injected via `__minEffectiveDate` / `__maxEffectiveDate` shared state keys by the executor. No hardcoded dates in config.

### credit_scores

| Property | Value |
|----------|-------|
| resultName | `credit_scores` |
| schema | `datalake` |
| table | `credit_scores` |
| columns | `["customer_id", "score"]` |

Effective dates: Same injection mechanism.

### Removed vs V1

| V1 DataSourcing Entry | Reason for Removal | Evidence |
|----------------------|-------------------|----------|
| `customers` table | AP1: Dead-end sourcing. Sourced but never accessed by V1 External module. | [LoanRiskCalculator.cs:16-17] -- only `loan_accounts` and `credit_scores` retrieved; BRD BR-4 |
| `segments` table | AP1: Dead-end sourcing. Sourced but never accessed by V1 External module. | [LoanRiskCalculator.cs:16-17]; BRD BR-5 |
| `credit_score_id` column | AP4: Unused column. Never referenced in V1 processing logic. | [LoanRiskCalculator.cs:30-31] -- only `customer_id` and `score` accessed |
| `bureau` column | AP4: Unused column. Never referenced in V1 processing logic. Scores are averaged across all bureaus without regard to bureau identity. | [LoanRiskCalculator.cs:28-37] -- no bureau filtering or grouping |

---

## 4. Transformation SQL

```sql
SELECT
    la.loan_id,
    la.customer_id,
    la.loan_type,
    la.current_balance,
    la.interest_rate,
    la.loan_status,
    cs_avg.avg_credit_score,
    CASE
        WHEN cs_avg.avg_credit_score >= 750 THEN 'Low Risk'
        WHEN cs_avg.avg_credit_score >= 650 THEN 'Medium Risk'
        WHEN cs_avg.avg_credit_score >= 550 THEN 'High Risk'
        WHEN cs_avg.avg_credit_score IS NOT NULL THEN 'Very High Risk'
        ELSE 'Unknown'
    END AS risk_tier,
    la.as_of
FROM loan_accounts la
LEFT JOIN (
    SELECT customer_id, AVG(score) AS avg_credit_score
    FROM credit_scores
    GROUP BY customer_id
) cs_avg ON la.customer_id = cs_avg.customer_id
```

### SQL Design Rationale

1. **LEFT JOIN**: V1 iterates all loans and looks up credit scores per customer [LoanRiskCalculator.cs:46-53]. When no scores exist for a customer, V1 sets avg_credit_score to DBNull.Value and risk_tier to "Unknown" [LoanRiskCalculator.cs:66-69]. A LEFT JOIN achieves the same effect: every loan row appears in the output, with NULL avg_credit_score when no credit scores exist for that customer.

2. **Subquery aggregation**: The subquery `SELECT customer_id, AVG(score) ... GROUP BY customer_id` computes the average credit score per customer across ALL credit_scores rows in the effective date range. This matches V1's behavior of grouping all scores by customer_id without filtering by as_of [LoanRiskCalculator.cs:28-37 -- no as_of filter on the iteration loop]. The effective date range is already applied by DataSourcing upstream. See BRD Edge Case #4.

3. **CASE expression for risk_tier**: Maps avg_credit_score to risk_tier using the same thresholds as V1 [LoanRiskCalculator.cs:58-64]. The `WHEN cs_avg.avg_credit_score IS NOT NULL THEN 'Very High Risk'` clause handles the catch-all for scores below 550. The `ELSE 'Unknown'` clause handles NULL (no scores for customer) [LoanRiskCalculator.cs:69].

4. **BRD Threshold Correction**: The BRD states BR-2 as `>= 700` for "Low Risk". This is INCORRECT. The V1 source code [LoanRiskCalculator.cs:60] clearly shows `>= 750`. V2 uses the V1 source code value of 750. The BRD should be corrected.

5. **Column order**: The SELECT lists columns in the exact order defined by V1's `outputColumns` [LoanRiskCalculator.cs:10-14].

6. **as_of from loan row**: `la.as_of` in the SELECT ensures the as_of value comes from the loan_accounts row, matching V1 behavior [LoanRiskCalculator.cs:82: `["as_of"] = loanRow["as_of"]`]. The credit_scores subquery does not expose as_of, so there is no ambiguity.

7. **No explicit ORDER BY**: V1 iterates `loanAccounts.Rows` in DataSourcing order, which follows PostgreSQL's natural order for `loan_accounts` (ascending by loan_id within each as_of date). The SQL will return rows in the order produced by the LEFT JOIN, which follows `loan_accounts` row order since it is the driving table. If deterministic ordering is needed for Parquet comparison, the Developer may add `ORDER BY la.as_of, la.loan_id`.

### Empty Input Edge Case

If `credit_scores` has zero rows, `Transformation.RegisterTable` skips it [Transformation.cs:46: `if (!df.Rows.Any()) return;`]. The SQL would then fail with "no such table: credit_scores." Similarly, if `loan_accounts` is empty, the SQL would fail.

V1 handles this with a compound guard [LoanRiskCalculator.cs:19-23] that returns empty output when either table is null or empty. The V2 External module replicates this guard by checking the source DataFrames (still in shared state from DataSourcing) BEFORE reading `sql_output`. If either source is empty, the External produces an empty output DataFrame with the correct schema, bypassing the (possibly failed) Transformation result.

**Critical note**: If the Transformation module throws an exception when a referenced table is missing, the pipeline may abort before the External gets a chance to execute. The Developer must verify whether `JobRunner` catches Transformation exceptions or whether the External module will actually run. If the Transformation throws, the practical mitigation is to structure the SQL to handle missing tables, or to add the empty guard as a pre-Transformation External module. For the operational date range (2024-10-01 through 2024-12-31), credit_scores always has ~6,690 rows per date, so this edge case will not trigger in Phase D testing.

---

## 5. Writer Config

| Property | Value | V1 Match | Evidence |
|----------|-------|----------|----------|
| Writer Type | ParquetFileWriter | Yes | [loan_risk_assessment.json:39] |
| source | `output` | Yes | [loan_risk_assessment.json:40] |
| outputDirectory | `Output/double_secret_curated/loan_risk_assessment/` | Path changed per V2 convention | V1: `Output/curated/loan_risk_assessment/` |
| numParts | `2` | Yes | [loan_risk_assessment.json:42] |
| writeMode | `Overwrite` | Yes | [loan_risk_assessment.json:43] |

**Write mode note**: Overwrite mode means each effective date execution deletes all existing `.parquet` files in the directory and writes fresh output. For multi-day auto-advance runs, only the last effective date's output survives on disk. This matches V1 behavior exactly.

---

## 6. Wrinkle Replication

### Applicable W-codes

| W-code | Name | Applies? | V1 Evidence | V2 Replication |
|--------|------|----------|-------------|----------------|
| W9 | Wrong writeMode | POSSIBLE | V1 uses `Overwrite` [loan_risk_assessment.json:43]. For multi-day auto-advance runs, only the last day's output survives. Whether this is intentional or a bug is unclear. | **REPRODUCED.** V2 uses the same `Overwrite` mode. The behavior is replicated for output equivalence. |

### Non-Applicable W-codes

| W-code | Why Not Applicable |
|--------|--------------------|
| W1 (Sunday skip) | No Sunday check in V1 code. |
| W2 (Weekend fallback) | No weekend date logic in V1 code. |
| W3a/b/c (Boundary rows) | No summary row generation in V1 code. |
| W4 (Integer division) | V1 uses `decimal` arithmetic via LINQ `Average()` [LoanRiskCalculator.cs:41], not integer division. |
| W5 (Banker's rounding) | No explicit rounding in V1 code. The average is not rounded. |
| W6 (Double epsilon) | V1 uses `decimal` for score accumulation [LoanRiskCalculator.cs:31], not `double`. |
| W7 (Trailer inflated count) | Output is Parquet, no trailers. |
| W8 (Trailer stale date) | Output is Parquet, no trailers. |
| W10 (Absurd numParts) | 2 parts for ~894 loan rows is reasonable. |
| W12 (Header every append) | Output is Parquet with Overwrite mode, not CSV Append. |

---

## 7. Anti-Pattern Elimination

| AP-code | Name | Applies? | V1 Evidence | V2 Action |
|---------|------|----------|-------------|-----------|
| AP1 | Dead-end sourcing | YES | V1 sources `customers` [loan_risk_assessment.json:20-24] and `segments` [loan_risk_assessment.json:26-30] but neither is accessed by the External module [LoanRiskCalculator.cs:16-17; BRD BR-4, BR-5]. | **ELIMINATED.** V2 config does not source `customers` or `segments`. |
| AP3 | Unnecessary External module | PARTIAL | V1 uses a full External module [LoanRiskCalculator.cs] for logic that is mostly expressible in SQL (LEFT JOIN, AVG, CASE). Only the decimal type cast and DateOnly reconstruction require procedural code. | **PARTIALLY ELIMINATED.** V2 moves join, aggregation, and risk tier logic to SQL Transformation. External module reduced to decimal type casting, DateOnly reconstruction, and empty-input guard only. |
| AP4 | Unused columns | YES | V1 sources `credit_score_id` and `bureau` from credit_scores [loan_risk_assessment.json:17] but neither is referenced [LoanRiskCalculator.cs:30-31 -- only `customer_id` and `score` accessed]. | **ELIMINATED.** V2 DataSourcing for credit_scores sources only `["customer_id", "score"]`. |
| AP6 | Row-by-row iteration | YES | V1 uses nested foreach loops: one to build score dictionary [LoanRiskCalculator.cs:28-37], another to iterate loans and look up scores [LoanRiskCalculator.cs:46-84]. | **ELIMINATED.** V2 replaces both loops with a single SQL LEFT JOIN + subquery AVG + CASE expression. The External module retains a single loop for type casting, which is a mechanical operation, not business logic. |
| AP7 | Magic values | YES | V1 hardcodes risk tier thresholds as bare literals: `750`, `650`, `550` and string literals `"Low Risk"`, `"Medium Risk"`, `"High Risk"`, `"Very High Risk"`, `"Unknown"` [LoanRiskCalculator.cs:58-64, 69]. | **ELIMINATED in External module.** The V2 External module defines named constants with descriptive names and comments for the threshold values. The SQL Transformation uses inline values (SQL does not support named constants) but each threshold has a SQL comment citing V1 evidence. |

### Non-Applicable AP-codes

| AP-code | Why Not Applicable |
|---------|--------------------|
| AP2 (Duplicated logic) | No cross-job duplication identified for this specific logic. |
| AP5 (Asymmetric NULLs) | NULL handling is consistent: missing credit scores -> DBNull.Value for avg_credit_score, "Unknown" for risk_tier. No asymmetry. |
| AP8 (Complex SQL / unused CTEs) | V1 has no SQL. V2's SQL is straightforward with no unused components. |
| AP9 (Misleading names) | Job name accurately describes what it produces. |
| AP10 (Over-sourcing dates) | V1 uses executor-injected effective dates via DataSourcing, not manual WHERE clause filtering. |

---

## 8. Proofmark Config

### Config File: `POC3/proofmark_configs/loan_risk_assessment.yaml`

```yaml
comparison_target: "loan_risk_assessment"
reader: parquet
threshold: 100.0
```

### Proofmark Design Rationale

- **reader: parquet** -- V1 and V2 both use ParquetFileWriter.
- **threshold: 100.0** -- Strict. All columns are deterministic; no tolerance needed.
- **No EXCLUDED columns** -- No non-deterministic fields identified. BRD confirms: "None identified. All computations are deterministic given the same input data."
- **No FUZZY columns** -- Starting strict per best practices. The V2 External module casts SQLite's double AVG result back to decimal to match V1's Parquet schema. For integer credit score inputs averaged across small sets (typically 3 bureaus), the double-to-decimal conversion should be exact. If Phase D testing reveals precision discrepancies, a fuzzy override on avg_credit_score can be added with evidence.

---

## 9. Open Questions

1. **BRD threshold discrepancy (RESOLVED for V2, BRD needs correction):** BRD BR-2 states `>= 700` for "Low Risk", but V1 source code uses `>= 750` [LoanRiskCalculator.cs:60]. V2 uses the V1 code value (750). The BRD should be corrected upstream.

2. **Empty table Transformation failure:** If either source table is empty, `Transformation.RegisterTable` skips it [Transformation.cs:46], and the SQL will fail with "no such table." If the Transformation throws an exception, the External module never executes, and V2 cannot produce the empty output that V1 would produce. The Developer must verify whether `JobRunner` catches module exceptions or whether the pipeline aborts. If it aborts, a restructuring may be needed (e.g., pre-Transformation empty guard). For the operational date range, this edge case will not trigger.

3. **current_balance and interest_rate type fidelity:** These are `numeric` (decimal) in PostgreSQL, read as `decimal` by DataSourcing, stored as `REAL` (double) in SQLite [Transformation.cs:101], and read back as `double` by `ReaderToDataFrame` [Transformation.cs:90]. V1 passes them through directly from DataSourcing as `decimal`. If ParquetFileWriter detects the first non-null sample as `double` instead of `decimal`, the Parquet column type will differ. The External module should convert these values back to `decimal` if needed. Flagged for Phase D Proofmark validation.

4. **DBNull.Value vs null in Parquet:** V1 uses `DBNull.Value` for missing avg_credit_score [LoanRiskCalculator.cs:68]. `ParquetFileWriter.BuildTypedArray` checks `r[col] is null` [ParquetFileWriter.cs:118], which returns `false` for `DBNull.Value` (DBNull.Value is not null). The Developer must verify whether V1's use of `DBNull.Value` produces the same Parquet output as using `null`. If not, the External module must match V1's exact sentinel value.

---

## 10. Output Schema

| Column | Parquet Type | Source | Transformation | V1 Evidence |
|--------|-------------|--------|---------------|-------------|
| loan_id | int? | loan_accounts.loan_id | Direct pass-through | [LoanRiskCalculator.cs:74] |
| customer_id | int? | loan_accounts.customer_id | Direct pass-through (also join key) | [LoanRiskCalculator.cs:48,75] |
| loan_type | string | loan_accounts.loan_type | Direct pass-through | [LoanRiskCalculator.cs:76] |
| current_balance | decimal? | loan_accounts.current_balance | Direct pass-through | [LoanRiskCalculator.cs:77] |
| interest_rate | decimal? | loan_accounts.interest_rate | Direct pass-through | [LoanRiskCalculator.cs:78] |
| loan_status | string | loan_accounts.loan_status | Direct pass-through | [LoanRiskCalculator.cs:79] |
| avg_credit_score | decimal? | Computed | AVG of all credit_scores.score for this customer_id across all as_of dates; null if no scores exist | [LoanRiskCalculator.cs:41,55-56,68] |
| risk_tier | string | Computed | CASE on avg_credit_score: >= 750 "Low Risk", >= 650 "Medium Risk", >= 550 "High Risk", < 550 "Very High Risk", NULL -> "Unknown" | [LoanRiskCalculator.cs:58-64,69] |
| as_of | DateOnly? | loan_accounts.as_of | Per-row pass-through from loan row | [LoanRiskCalculator.cs:82] |

### Column Order
`loan_id, customer_id, loan_type, current_balance, interest_rate, loan_status, avg_credit_score, risk_tier, as_of` -- matching V1's `outputColumns` definition [LoanRiskCalculator.cs:10-14].

### Non-Deterministic Fields
None. All computations are deterministic given the same input data. [BRD: Non-Deterministic Fields section]

---

## 11. External Module Design

### File: `ExternalModules/LoanRiskAssessmentV2Processor.cs`

### Purpose

Minimal Tier 2 External module that performs type-casting operations required for Parquet schema fidelity and handles the empty-input edge case. This module contains NO business logic -- no joins, no averaging, no threshold comparisons.

### Scope

1. **Empty-input guard (BR-6):** Check if `loan_accounts` or `credit_scores` is null or empty. If so, produce empty output DataFrame with correct schema. Replicates V1's compound guard [LoanRiskCalculator.cs:19-23].
2. **Decimal type cast for avg_credit_score (BR-9):** SQLite AVG returns double; cast to decimal to match V1's Parquet column type.
3. **DateOnly reconstruction for as_of (BR-8):** SQLite TEXT -> DateOnly to match V1's Parquet column type.
4. **Decimal restoration for current_balance and interest_rate:** SQLite REAL -> decimal to match V1's Parquet column types. (See Open Question #3.)

### Pseudocode

```csharp
public class LoanRiskAssessmentV2Processor : IExternalStep
{
    private static readonly List<string> OutputColumns = new()
    {
        "loan_id", "customer_id", "loan_type", "current_balance",
        "interest_rate", "loan_status", "avg_credit_score", "risk_tier", "as_of"
    };

    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        // BR-6: Empty-input guard -- replicates V1 compound null/empty check
        // V1: LoanRiskCalculator.cs:19-23
        var loanAccounts = sharedState.GetValueOrDefault("loan_accounts") as DataFrame;
        var creditScores = sharedState.GetValueOrDefault("credit_scores") as DataFrame;

        if (loanAccounts == null || loanAccounts.Count == 0 ||
            creditScores == null || creditScores.Count == 0)
        {
            sharedState["output"] = new DataFrame(new List<Row>(), OutputColumns);
            return sharedState;
        }

        var sqlOutput = sharedState["sql_output"] as DataFrame;
        if (sqlOutput == null || sqlOutput.Count == 0)
        {
            sharedState["output"] = new DataFrame(new List<Row>(), OutputColumns);
            return sharedState;
        }

        var outputRows = new List<Row>();
        foreach (var row in sqlOutput.Rows)
        {
            var rawAvg = row["avg_credit_score"];

            // BR-9: Cast avg_credit_score from double (SQLite) to decimal (V1 type)
            // BR-3: Null avg -> DBNull.Value [LoanRiskCalculator.cs:68]
            object? avgCreditScore = (rawAvg == null || rawAvg is DBNull)
                ? DBNull.Value
                : Convert.ToDecimal(rawAvg);

            // Reconstruct DateOnly from SQLite text [LoanRiskCalculator.cs:82]
            var asOf = DateOnly.Parse(row["as_of"]?.ToString() ?? "");

            // Restore decimal types for monetary fields
            var currentBalance = Convert.ToDecimal(row["current_balance"]);
            var interestRate = Convert.ToDecimal(row["interest_rate"]);

            outputRows.Add(new Row(new Dictionary<string, object?>
            {
                ["loan_id"] = Convert.ToInt32(row["loan_id"]),
                ["customer_id"] = Convert.ToInt32(row["customer_id"]),
                ["loan_type"] = row["loan_type"]?.ToString(),
                ["current_balance"] = currentBalance,
                ["interest_rate"] = interestRate,
                ["loan_status"] = row["loan_status"]?.ToString(),
                ["avg_credit_score"] = avgCreditScore,
                ["risk_tier"] = row["risk_tier"]?.ToString(),
                ["as_of"] = asOf
            }));
        }

        sharedState["output"] = new DataFrame(outputRows, OutputColumns);
        return sharedState;
    }
}
```

### Design Notes

1. **No business logic in External:** All risk tier classification, joining, and averaging is in the SQL Transformation. The External only handles type conversion and the empty-input guard.

2. **Row iteration is justified:** The External iterates rows to convert types (double -> decimal, string -> DateOnly), which is inherently a per-row operation. This is NOT the AP6 anti-pattern -- AP6 refers to implementing business logic (joins, aggregations) as row-by-row loops instead of set operations. Type casting is a mechanical operation.

3. **DBNull.Value for null avg_credit_score:** V1 uses `DBNull.Value` [LoanRiskCalculator.cs:68], not `null`. The Developer must verify that ParquetFileWriter handles DBNull.Value correctly for nullable decimal columns. See Open Question #4.

4. **current_balance and interest_rate restoration:** These are decimal in PostgreSQL but become double after passing through SQLite. The External converts them back to decimal to ensure ParquetFileWriter produces the correct Parquet column type. See Open Question #3.

---

## 12. Traceability Matrix

| BRD Requirement | FSD Section | Implementation Element |
|-----------------|-------------|----------------------|
| BR-1: Avg credit score per customer across all bureaus/dates | Sec 4 (SQL: subquery AVG + GROUP BY customer_id) | SQL subquery computes AVG(score) grouped by customer_id across all dates |
| BR-2: Risk tier thresholds (CORRECTED: >= 750 Low Risk) | Sec 4 (SQL: CASE expression) | CASE WHEN >= 750 THEN 'Low Risk' etc. **NOTE: BRD states 700, V1 code uses 750** |
| BR-3: Missing scores -> null avg, "Unknown" tier | Sec 4 (SQL: LEFT JOIN + ELSE 'Unknown'), Sec 11 (External: DBNull.Value) | LEFT JOIN produces NULL; CASE assigns "Unknown"; External casts NULL to DBNull.Value |
| BR-4: Customers table unused | Sec 3 (Removed), Sec 7 (AP1) | Customers DataSourcing removed from V2 config |
| BR-5: Segments table unused | Sec 3 (Removed), Sec 7 (AP1) | Segments DataSourcing removed from V2 config |
| BR-6: Empty input produces empty output | Sec 4 (Edge Cases), Sec 11 (External: empty guard) | External checks loan_accounts and credit_scores before processing |
| BR-7: Loan field pass-through | Sec 4 (SQL: la.column), Sec 10 (Output Schema) | SQL SELECT passes through all 6 loan fields |
| BR-8: as_of from loan row, not maxEffectiveDate | Sec 4 (SQL: la.as_of), Sec 11 (External: DateOnly reconstruction) | SQL selects la.as_of; External reconstructs as DateOnly |
| BR-9: Decimal precision for avg_credit_score | Sec 1 (Tier Justification), Sec 11 (External: Convert.ToDecimal) | External casts double -> decimal to match V1 Parquet type |
| Output: Parquet, 2 parts, Overwrite | Sec 5 (Writer Config) | ParquetFileWriter with numParts=2, writeMode=Overwrite |
| Edge Case #3: Multi-bureau averaging | Sec 4 (SQL: GROUP BY customer_id only) | AVG(score) across all bureaus per customer |
| Edge Case #4: Multi-date score accumulation | Sec 4 (SQL: no as_of filter in subquery) | Subquery aggregates across all dates in effective range |

---

## Appendix: V2 Job Config JSON

```json
{
  "jobName": "LoanRiskAssessmentV2",
  "firstEffectiveDate": "2024-10-01",
  "modules": [
    {
      "type": "DataSourcing",
      "resultName": "loan_accounts",
      "schema": "datalake",
      "table": "loan_accounts",
      "columns": ["loan_id", "customer_id", "loan_type", "current_balance", "interest_rate", "loan_status"]
    },
    {
      "type": "DataSourcing",
      "resultName": "credit_scores",
      "schema": "datalake",
      "table": "credit_scores",
      "columns": ["customer_id", "score"]
    },
    {
      "type": "Transformation",
      "resultName": "sql_output",
      "sql": "SELECT la.loan_id, la.customer_id, la.loan_type, la.current_balance, la.interest_rate, la.loan_status, cs_avg.avg_credit_score, CASE WHEN cs_avg.avg_credit_score >= 750 THEN 'Low Risk' WHEN cs_avg.avg_credit_score >= 650 THEN 'Medium Risk' WHEN cs_avg.avg_credit_score >= 550 THEN 'High Risk' WHEN cs_avg.avg_credit_score IS NOT NULL THEN 'Very High Risk' ELSE 'Unknown' END AS risk_tier, la.as_of FROM loan_accounts la LEFT JOIN (SELECT customer_id, AVG(score) AS avg_credit_score FROM credit_scores GROUP BY customer_id) cs_avg ON la.customer_id = cs_avg.customer_id"
    },
    {
      "type": "External",
      "assemblyPath": "/workspace/MockEtlFramework/ExternalModules/bin/Debug/net8.0/ExternalModules.dll",
      "typeName": "ExternalModules.LoanRiskAssessmentV2Processor"
    },
    {
      "type": "ParquetFileWriter",
      "source": "output",
      "outputDirectory": "Output/double_secret_curated/loan_risk_assessment/",
      "numParts": 2,
      "writeMode": "Overwrite"
    }
  ]
}
```

### Config Changes vs V1

| Field | V1 Value | V2 Value | Reason |
|-------|----------|----------|--------|
| jobName | `LoanRiskAssessment` | `LoanRiskAssessmentV2` | V2 naming convention |
| DataSourcing: customers | Present | **Removed** | AP1: dead-end sourcing eliminated |
| DataSourcing: segments | Present | **Removed** | AP1: dead-end sourcing eliminated |
| DataSourcing: credit_scores columns | `["credit_score_id", "customer_id", "bureau", "score"]` | `["customer_id", "score"]` | AP4: unused columns eliminated |
| Transformation | Not present | **Added** | AP3/AP6: SQL replaces most External logic |
| External typeName | `LoanRiskCalculator` | `LoanRiskAssessmentV2Processor` | V2 naming; minimal scope |
| ParquetFileWriter outputDirectory | `Output/curated/loan_risk_assessment/` | `Output/double_secret_curated/loan_risk_assessment/` | V2 output path |

### Config Preserved from V1

| Field | Value | Reason |
|-------|-------|--------|
| firstEffectiveDate | `"2024-10-01"` | Same date range as V1 |
| numParts | `2` | Output equivalence |
| writeMode | `"Overwrite"` | Output equivalence (W9) |
