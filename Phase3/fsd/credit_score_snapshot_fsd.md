# CreditScoreSnapshot -- Functional Specification Document

## Design Approach

**SQL-first with External module guard for empty DataFrames.** The original External module is a pure pass-through (copies every row with no transformation). However, the framework's Transformation module does not register empty DataFrames as SQLite tables, meaning a pure SQL approach would crash on weekends when `credit_scores` has no data. The original gracefully returns empty output on empty input.

To handle this, the V2 uses a minimal External module that:
1. Checks if the credit_scores DataFrame is empty (returning empty output if so)
2. Otherwise passes the DataFrame through directly (no row-by-row copy needed)

This is a framework limitation justification, not a business logic justification. The V2 External module is dramatically simpler than the original.

## Anti-Patterns Eliminated

| AP Code | Present in Original? | Eliminated in V2? | How? |
|---------|---------------------|--------------------|------|
| AP-1    | Y                   | Y                  | Removed unused `branches` DataSourcing module entirely |
| AP-2    | N                   | N/A                | No duplicated transformation logic |
| AP-3    | Y                   | Partial            | External module retained for empty-DataFrame guard only; row-by-row copy eliminated (direct DataFrame pass-through) |
| AP-4    | Y                   | Y                  | Removed unused branches columns (covered by AP-1 removal) |
| AP-5    | N                   | N/A                | No NULL/default handling asymmetry |
| AP-6    | Y                   | Y                  | Eliminated row-by-row copy loop; direct DataFrame assignment instead |
| AP-7    | N                   | N/A                | No magic values |
| AP-8    | N                   | N/A                | No complex SQL |
| AP-9    | Y                   | N (documented)     | Name "Snapshot" slightly misleading for a pass-through; kept for compatibility |
| AP-10   | N                   | N/A                | No inter-job dependencies needed |

## V2 Pipeline Design

1. **DataSourcing** - `credit_scores` from `datalake.credit_scores` with columns: `credit_score_id`, `customer_id`, `bureau`, `score`
2. **External** - `CreditScoreSnapshotV2`: empty-check guard + pass-through
3. **DataFrameWriter** - Write to `credit_score_snapshot` in `double_secret_curated` schema, Overwrite mode

## External Module Design

```
IF credit_scores is empty:
    Return empty DataFrame with columns [credit_score_id, customer_id, bureau, score, as_of]
ELSE:
    Return credit_scores DataFrame directly (it already has all needed columns including as_of)
```

No row-by-row iteration. No transformation. Just a null/empty guard with direct DataFrame pass-through.

## Traceability to BRD

| BRD Requirement | FSD Design Element |
|-----------------|-------------------|
| BR-1 | Direct pass-through: all rows from credit_scores are passed to output unchanged |
| BR-2 | Output columns match source: credit_score_id, customer_id, bureau, score, as_of |
| BR-3 | Empty-check guard returns empty DataFrame when input is empty |
| BR-4 | DataFrameWriter configured with writeMode: Overwrite |
