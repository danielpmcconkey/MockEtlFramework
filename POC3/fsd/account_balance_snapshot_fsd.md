# AccountBalanceSnapshot — Functional Specification Document

## 1. Overview

AccountBalanceSnapshotV2 produces a daily snapshot of account balances by selecting six key fields from the datalake accounts table and writing them to Parquet in Append mode.

**Tier: 1 (Framework Only)** -- `DataSourcing -> ParquetFileWriter`

**Tier Justification:** The V1 External module (`AccountSnapshotBuilder`) performs nothing beyond column selection -- a passthrough of 5 source columns plus the `as_of` column, with no joins, aggregation, procedural logic, or calculations. DataSourcing natively provides all 6 required output columns: the 5 explicitly requested columns plus `as_of`, which DataSourcing auto-appends when it is not included in the column list [DataSourcing.cs:69-72]. No Transformation module is needed because no SQL logic is required. No External module is needed because DataSourcing already produces the exact output schema.

**Empty-input handling:** When DataSourcing returns zero rows (weekend dates per BR-6), it produces an empty DataFrame. ParquetFileWriter writes this as empty Parquet part files (zero data rows). This matches V1 behavior, where the External module's empty-check [AccountSnapshotBuilder.cs:18-22] produces an empty DataFrame that is also written as empty Parquet. No Transformation module is involved, so the empty-table registration issue in Transformation.cs:46 is entirely avoided.

## 2. V2 Module Chain

| Step | Module Type | Config Key | Details |
|------|------------|------------|---------|
| 1 | DataSourcing | `accounts` | schema=`datalake`, table=`accounts`, columns=`[account_id, customer_id, account_type, account_status, current_balance]`. Effective dates injected by executor. `as_of` auto-appended by DataSourcing. |
| 2 | ParquetFileWriter | -- | source=`accounts`, outputDirectory=`Output/double_secret_curated/account_balance_snapshot/`, numParts=2, writeMode=Append |

### Key Design Decisions

- **Only source the accounts table.** V1 sources branches but never uses it (BR-2, AP1). V2 eliminates this dead-end source.
- **Only source 5 columns from accounts.** V1 sources 8 columns but only uses 5 of them plus `as_of` (BR-1, AP4). V2 eliminates the 3 unused columns (`open_date`, `interest_rate`, `credit_limit`).
- **No External module.** V1's External module is a row-by-row column selector (AP3, AP6). DataSourcing already produces the required 6-column output, making both the External and a Transformation module unnecessary.
- **ParquetFileWriter reads directly from `accounts`.** Since DataSourcing produces the exact output schema, no intermediate `output` DataFrame is needed. The writer reads from the `accounts` key in shared state.

## 3. Anti-Pattern Analysis

### Output-Affecting Wrinkles (W-codes)

**No W-codes apply to this job.** The V1 External module performs pure passthrough column selection with no calculations, rounding, date manipulation, trailer generation, hardcoded values, or unusual write modes.

| W-code | Applicable? | Rationale |
|--------|------------|-----------|
| W1 (Sunday skip) | No | No day-of-week logic in V1 code. |
| W2 (Weekend fallback) | No | No date fallback logic in V1 code. |
| W3a/b/c (Boundary rows) | No | No summary row generation in V1 code. |
| W4 (Integer division) | No | No division operations in V1 code. |
| W5 (Banker's rounding) | No | No rounding operations in V1 code. |
| W6 (Double epsilon) | No | No accumulation or arithmetic in V1 code. |
| W7 (Trailer inflated count) | No | Parquet writer, no trailers. |
| W8 (Trailer stale date) | No | Parquet writer, no trailers. |
| W9 (Wrong writeMode) | No | Append is correct for a daily snapshot time series. |
| W10 (Absurd numParts) | No | 2 parts for ~2,869 rows/day is reasonable. |
| W12 (Header every append) | No | Parquet writer, no header concerns. |

### Code-Quality Anti-Patterns (AP-codes)

| AP-code | Identified? | V1 Problem | V2 Resolution |
|---------|------------|------------|---------------|
| **AP1** (Dead-end sourcing) | **YES** | V1 sources `datalake.branches` but never references it. Evidence: [AccountSnapshotBuilder.cs:8-39] contains no reference to "branches". | **Eliminated.** V2 does not source branches. |
| **AP3** (Unnecessary External) | **YES** | V1 uses `AccountSnapshotBuilder` for logic that is trivial column selection. Evidence: [AccountSnapshotBuilder.cs:10-14] defines 6 output columns; [AccountSnapshotBuilder.cs:27-35] is row-by-row copy. | **Eliminated.** V2 uses DataSourcing directly -- no External or Transformation module needed. |
| **AP4** (Unused columns) | **YES** | V1 sources `open_date`, `interest_rate`, `credit_limit` but never outputs them. Evidence: [account_balance_snapshot.json:10] sources 8 columns; [AccountSnapshotBuilder.cs:10-14] outputs only 6. | **Eliminated.** V2 DataSourcing requests only the 5 columns used in output. |
| **AP6** (Row-by-row iteration) | **YES** | V1 uses `foreach` loop to copy columns. Evidence: [AccountSnapshotBuilder.cs:25-36]. | **Eliminated.** V2 uses no iteration -- DataSourcing produces the output directly as a set-based database query. |
| AP2 (Duplicated logic) | No | Not applicable. |
| AP5 (Asymmetric NULLs) | No | No NULL coalescing in V1 (BR-4: verbatim passthrough). |
| AP7 (Magic values) | No | No hardcoded thresholds. |
| AP8 (Complex SQL) | No | V1 has no SQL; V2 has no SQL. |
| AP9 (Misleading names) | No | Name accurately describes output. |
| AP10 (Over-sourcing dates) | No | V1 uses executor-injected effective dates correctly. |

## 4. Output Schema

| Column | Source Table | Source Column | Transformation | Evidence |
|--------|-------------|---------------|---------------|----------|
| account_id | datalake.accounts | account_id | None (passthrough) | [AccountSnapshotBuilder.cs:29] |
| customer_id | datalake.accounts | customer_id | None (passthrough) | [AccountSnapshotBuilder.cs:30] |
| account_type | datalake.accounts | account_type | None (passthrough) | [AccountSnapshotBuilder.cs:31] |
| account_status | datalake.accounts | account_status | None (passthrough) | [AccountSnapshotBuilder.cs:32] |
| current_balance | datalake.accounts | current_balance | None (passthrough) | [AccountSnapshotBuilder.cs:33] |
| as_of | datalake.accounts | as_of | None (auto-appended by DataSourcing) | [AccountSnapshotBuilder.cs:34], [DataSourcing.cs:69-72] |

**Column order:** account_id, customer_id, account_type, account_status, current_balance, as_of. This matches the order defined in the V1 External module's `outputColumns` list [AccountSnapshotBuilder.cs:10-14]. In V2, the column order is determined by the DataSourcing `columns` array (in order), with `as_of` appended last by the framework -- producing the same order.

**NULL handling:** All column values pass through verbatim. No NULL coalescing is applied. NULL values are written as Parquet nulls (BR-4).

**Empty input (BR-3):** When accounts has zero rows (weekends), DataSourcing returns an empty DataFrame. ParquetFileWriter writes empty Parquet part files. V1 has identical behavior via its explicit empty-check [AccountSnapshotBuilder.cs:18-22].

## 5. SQL Design

**Not applicable.** V2 does not use a Transformation module. DataSourcing provides the exact output schema, and ParquetFileWriter writes it directly.

## 6. V2 Job Config

```json
{
  "jobName": "AccountBalanceSnapshotV2",
  "firstEffectiveDate": "2024-10-01",
  "modules": [
    {
      "type": "DataSourcing",
      "resultName": "accounts",
      "schema": "datalake",
      "table": "accounts",
      "columns": ["account_id", "customer_id", "account_type", "account_status", "current_balance"]
    },
    {
      "type": "ParquetFileWriter",
      "source": "accounts",
      "outputDirectory": "Output/double_secret_curated/account_balance_snapshot/",
      "numParts": 2,
      "writeMode": "Append"
    }
  ]
}
```

### Differences from V1 Config

| Change | V1 | V2 | Reason |
|--------|----|----|--------|
| branches DataSourcing | Present | Removed | AP1: dead-end sourcing |
| accounts columns | 8 columns | 5 columns | AP4: unused columns eliminated |
| External module | AccountSnapshotBuilder | Removed | AP3: unnecessary External; AP6: row-by-row iteration |
| Writer source | `output` | `accounts` | No intermediate DataFrame needed |
| Output directory | `Output/curated/...` | `Output/double_secret_curated/...` | V2 convention |
| Job name | `AccountBalanceSnapshot` | `AccountBalanceSnapshotV2` | V2 naming convention |

## 7. Writer Configuration

| Property | V1 Value | V2 Value | Match? |
|----------|----------|----------|--------|
| type | ParquetFileWriter | ParquetFileWriter | YES |
| numParts | 2 | 2 | YES |
| writeMode | Append | Append | YES |
| source | `output` | `accounts` | Name differs; data content identical |
| outputDirectory | `Output/curated/account_balance_snapshot/` | `Output/double_secret_curated/account_balance_snapshot/` | Changed per V2 convention |

## 8. Proofmark Config Design

**Excluded columns:** None.

**Fuzzy columns:** None.

**Rationale:** All 6 output columns are deterministic passthroughs from the datalake source data. No calculations, rounding, timestamps, or non-deterministic operations are involved. The BRD explicitly states "Non-Deterministic Fields: None identified." There is zero justification for exclusion or fuzzy matching on any column.

**Proofmark config:**
```yaml
comparison_target: "account_balance_snapshot"
reader: parquet
threshold: 100.0
```

## 9. Traceability Matrix

| FSD Decision | BRD Requirement | Evidence |
|-------------|-----------------|----------|
| Source only accounts table (drop branches) | BR-2: branches sourced but never used | [AccountSnapshotBuilder.cs:8-39] -- no "branches" reference |
| Source only 5 columns from accounts | BR-1: only 6 columns in output (5 + as_of) | [AccountSnapshotBuilder.cs:10-14] -- outputColumns list |
| as_of auto-appended by DataSourcing | BR-5: as_of carried through from accounts | [DataSourcing.cs:69-72] -- auto-appends as_of when not in column list |
| No transformation logic (DataSourcing -> Writer) | BR-4: each row is direct passthrough, no transformation | [AccountSnapshotBuilder.cs:27-35] -- verbatim copy |
| Empty DataFrame on zero rows (weekend dates) | BR-3: empty input produces empty output | DataSourcing returns empty DataFrame; ParquetFileWriter writes empty parts |
| numParts=2 | BRD Writer Configuration | [account_balance_snapshot.json:28] |
| writeMode=Append | BRD Writer Configuration | [account_balance_snapshot.json:29] |
| firstEffectiveDate=2024-10-01 | BRD Traceability: first effective date | [account_balance_snapshot.json:3] |
| Eliminate AP1 (branches) | BR-2 + AP1 prescription | Remove unused DataSourcing entries |
| Eliminate AP3 (External module) | BR-4 + AP3 prescription | Replace with framework modules |
| Eliminate AP4 (unused columns) | BR-1 + AP4 prescription | Remove unused columns from DataSourcing |
| Eliminate AP6 (foreach iteration) | BR-4 + AP6 prescription | Replace with set-based operations |
| No Proofmark exclusions or fuzzy | BRD: no non-deterministic fields | BRD Non-Deterministic Fields section |

## 10. External Module Design

**Not applicable.** V2 uses Tier 1 (Framework Only) with a two-module chain: DataSourcing -> ParquetFileWriter. No External module is needed.
