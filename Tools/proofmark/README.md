# Proofmark

Data output comparison tool for ETL migration and rewrite validation.

## Overview

Proofmark compares two data outputs — a reference (LHS) and a candidate (RHS) — to verify
equivalence. It supports Parquet and CSV formats with configurable comparison rules.

Proofmark is format-aware, order-independent, and designed for automated validation pipelines
where human review of every row is impractical.

## Key Concepts

- **LHS / RHS**: Left-hand side (reference/expected) and right-hand side (candidate/actual).
  LHS is the baseline you trust. RHS is the output you're validating.

- **Comparison Modes**:
  - **STRICT**: Exact byte-for-byte match required (default for all columns)
  - **FUZZY**: Match within a configurable tolerance (absolute or relative)
  - **EXCLUDED**: Column is ignored during comparison entirely

- **Default Strict**: Every column is compared strictly unless explicitly overridden in config.
  This ensures no silent data differences slip through.

- **Order-Independent**: Row order does not affect comparison results. Proofmark uses
  hash-based row identification, so reordering rows between LHS and RHS is acceptable.

## Supported Formats

| Format | LHS/RHS Path | Notes |
|--------|-------------|-------|
| **Parquet** | Directory containing `part-*.parquet` files | Both LHS and RHS must be directories. Multiple part files are concatenated automatically. |
| **CSV** | Single file | Both LHS and RHS must be files. Configurable header/trailer row handling. |

## CLI Usage

```bash
python3 -m proofmark compare \
  --config <path-to-config.yaml> \
  --left <path-to-lhs> \
  --right <path-to-rhs> \
  [--output <path-to-report.json>]
```

### Arguments

| Argument | Required | Description |
|----------|----------|-------------|
| `--config` | Yes | Path to YAML configuration file defining comparison rules |
| `--left` | Yes | Path to LHS data (file for CSV, directory for Parquet) |
| `--right` | Yes | Path to RHS data (must match LHS format) |
| `--output` | No | Path for JSON report output (default: stdout) |

### Exit Codes

| Code | Meaning | Description |
|------|---------|-------------|
| `0` | **PASS** | Comparison succeeded. Match percentage meets or exceeds configured threshold. |
| `1` | **FAIL** | Comparison completed but data differences exceed threshold. |
| `2` | **ERROR** | Comparison could not complete. Configuration error, missing files, encoding failure, or other operational issue. |

## Examples

### Compare Parquet Directories

```bash
python3 -m proofmark compare \
  --config configs/daily_balance.yaml \
  --left output/baseline/daily_balance/ \
  --right output/candidate/daily_balance/ \
  --output reports/daily_balance.json
```

### Compare CSV Files

```bash
python3 -m proofmark compare \
  --config configs/transaction_summary.yaml \
  --left output/baseline/transaction_summary.csv \
  --right output/candidate/transaction_summary.csv \
  --output reports/transaction_summary.json
```

### Quick Stdout Check

```bash
python3 -m proofmark compare \
  --config configs/simple.yaml \
  --left baseline.csv \
  --right candidate.csv
```

## Configuration

See [CONFIG_GUIDE.md](CONFIG_GUIDE.md) for the full YAML configuration schema, field
reference, and examples for each output type.

## Report Format

When `--output` is specified, Proofmark produces a JSON report containing:

- Configuration echo (full config as applied)
- Column classification summary (which columns are STRICT/FUZZY/EXCLUDED)
- Row counts for LHS and RHS
- Match statistics (matched rows, mismatched rows, match percentage)
- Mismatch detail (specific row/column differences)
- Pass/fail determination against configured threshold

The report is self-contained and interpretable without external context.

## Important Notes

- Proofmark certifies **output equivalence** between LHS and RHS. It does NOT certify
  correctness in an absolute sense. If both LHS and RHS contain the same error,
  Proofmark will report PASS.

- Every EXCLUDED column and every FUZZY tolerance must be justified with a documented
  reason in the configuration file. Start with zero overrides and add only when
  comparison failures provide evidence of legitimate non-determinism.
