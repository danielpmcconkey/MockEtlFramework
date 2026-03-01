# Proofmark Configuration Guide

This document defines the YAML configuration schema for Proofmark comparison configs.

## YAML Schema

### Required Fields

| Field | Type | Description |
|-------|------|-------------|
| `comparison_target` | string | Name identifying what is being compared (e.g., job name, dataset name). Must be non-empty. |
| `reader` | string | Data format reader. Must be `"parquet"` or `"csv"`. Case-sensitive. |

### Optional Fields

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `encoding` | string | `"utf-8"` | Character encoding for CSV files. Passed to the file reader. Ignored for Parquet. |
| `threshold` | float | `100.0` | Pass percentage threshold. Range: 0.0 to 100.0. A value of 100.0 requires every row to match. |
| `csv` | object | (none) | CSV-specific settings. Only applicable when `reader: csv`. |
| `columns` | object | (none) | Column classification overrides. |

### CSV Settings

Only used when `reader: csv`. If omitted, defaults apply.

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `csv.header_rows` | int | `0` | Number of header rows to skip at the top of the file. Set to `1` if the CSV has a column header row. |
| `csv.trailer_rows` | int | `0` | Number of trailer/footer rows to strip from the end of the file. Only strips from the very end — does not affect embedded trailers in Append-mode files. |

### Column Overrides

The `columns` section defines which columns deviate from the default STRICT comparison.

#### Excluded Columns

Columns listed under `columns.excluded` are completely ignored during comparison.

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `name` | string | Yes | Column name to exclude |
| `reason` | string | Yes | Justification for exclusion. Must cite evidence (e.g., "Timestamp generated at runtime — non-deterministic"). |

#### Fuzzy Columns

Columns listed under `columns.fuzzy` use tolerance-based comparison instead of exact match.

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `name` | string | Yes | Column name for fuzzy comparison |
| `tolerance` | float | Yes | Tolerance value. Must be >= 0.0. |
| `tolerance_type` | string | Yes | `"absolute"` or `"relative"`. Case-sensitive. |
| `reason` | string | Yes | Justification for fuzzy matching. Must cite evidence. |

**Tolerance types:**
- `absolute`: Values match if `|LHS - RHS| <= tolerance`. Example: tolerance 0.01 allows up to 1 cent difference.
- `relative`: Values match if `|LHS - RHS| / |LHS| <= tolerance`. Example: tolerance 0.001 allows up to 0.1% difference.

### Validation Rules

1. `comparison_target` must be present and non-empty
2. `reader` must be exactly `"parquet"` or `"csv"`
3. `threshold` must be a number between 0.0 and 100.0
4. Every excluded column must have both `name` and `reason`
5. Every fuzzy column must have `name`, `tolerance`, `tolerance_type`, and `reason`
6. `tolerance` must be >= 0.0
7. `tolerance_type` must be exactly `"absolute"` or `"relative"`
8. No column may appear in both `excluded` and `fuzzy` lists

---

## Examples

### Example 1: Parquet (Default Strict)

Simplest config — all columns compared strictly, 100% match required.

```yaml
comparison_target: "customer_360_snapshot"
reader: parquet
threshold: 100.0
```

### Example 2: CSV with Header (No Trailer)

Standard CSV file with a header row.

```yaml
comparison_target: "card_fraud_flags"
reader: csv
threshold: 100.0
csv:
  header_rows: 1
  trailer_rows: 0
```

### Example 3: CSV with Header and Trailer (Overwrite Mode)

CSV file with a header row and a single trailer row at the end of the file.
Overwrite-mode jobs produce a file with exactly one trailer at the end.

```yaml
comparison_target: "overdraft_daily_summary"
reader: csv
threshold: 100.0
csv:
  header_rows: 1
  trailer_rows: 1
```

### Example 4: CSV with Header and Trailer (Append Mode)

Append-mode jobs add data AND a trailer row on each run. Over multiple days,
the file accumulates multiple trailers embedded throughout — not just at the end.
Set `trailer_rows: 0` because trailers are not exclusively at the file's end.

```yaml
comparison_target: "daily_transaction_summary"
reader: csv
threshold: 100.0
csv:
  header_rows: 1
  trailer_rows: 0
```

### Example 5: Parquet with Fuzzy Column

When a column has known floating-point precision differences (e.g., double arithmetic),
use a fuzzy tolerance.

```yaml
comparison_target: "portfolio_concentration"
reader: parquet
threshold: 100.0
columns:
  fuzzy:
    - name: "concentration_pct"
      tolerance: 0.0001
      tolerance_type: absolute
      reason: "Double-precision arithmetic accumulation — V1 and V2 may differ at epsilon level [ExternalModules/PortfolioConcentrationCalculator.cs:34]"
```

### Example 6: CSV with Excluded Column

When a column contains non-deterministic values that differ between runs (e.g., timestamps
generated at execution time), exclude it from comparison.

```yaml
comparison_target: "execution_audit"
reader: csv
threshold: 100.0
csv:
  header_rows: 1
columns:
  excluded:
    - name: "run_timestamp"
      reason: "Generated at execution time — non-reproducible between V1 and V2 runs"
```

### Example 7: Mixed Overrides

Multiple columns with different treatment.

```yaml
comparison_target: "customer_risk_profile"
reader: csv
threshold: 100.0
csv:
  header_rows: 1
columns:
  excluded:
    - name: "processing_timestamp"
      reason: "Execution-time value, differs between runs"
  fuzzy:
    - name: "risk_score"
      tolerance: 0.01
      tolerance_type: absolute
      reason: "Rounding difference between C# double and decimal arithmetic [ExternalModules/RiskCalculator.cs:78]"
    - name: "confidence_pct"
      tolerance: 0.001
      tolerance_type: relative
      reason: "Percentage calculation uses integer division in V1 [ExternalModules/RiskCalculator.cs:92]"
```

---

## Best Practices

1. **Start strict.** Begin every config with zero exclusions and zero fuzzy overrides.
   Only add overrides when a comparison failure provides concrete evidence of
   legitimate non-determinism or acceptable precision variance.

2. **Document everything.** The `reason` field is not optional decoration — it is your
   audit trail. Every override must cite specific code, configuration, or behavioral
   evidence justifying why strict comparison is inappropriate for that column.

3. **Prefer FUZZY over EXCLUDED.** If a column has small numeric differences, use fuzzy
   matching with a tight tolerance rather than excluding it entirely. Exclusion means
   zero validation. Fuzzy means bounded validation.

4. **Match your CSV settings to the file structure.** If the file has a header row,
   set `header_rows: 1`. If the file has a trailer at the end (Overwrite mode),
   set `trailer_rows: 1`. For Append-mode files with embedded trailers, set
   `trailer_rows: 0` — the trailers are part of the data.

5. **Use 100.0 threshold unless you have a documented reason not to.** Lowering the
   threshold means accepting some percentage of mismatched rows. This should be
   rare and always justified.
