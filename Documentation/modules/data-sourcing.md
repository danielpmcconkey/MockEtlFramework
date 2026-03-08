# DataSourcing

`Lib/Modules/DataSourcing.cs`

Queries a PostgreSQL data lake schema for a specified table, column list, and effective date range. Returns a single flat `DataFrame` with `ifw_effective_date` appended as a column (skipped if the caller already includes it). Supports an optional `additionalFilter` clause.

## Date Resolution Modes

Five mutually exclusive modes. Validated at construction -- mixing modes throws.

| Mode | Config | Behavior |
|---|---|---|
| **Static dates** | `minEffectiveDate` / `maxEffectiveDate` | Fixed date range from config |
| **Lookback** | `lookbackDays: N` | T-N through T-0 relative to `__etlEffectiveDate` |
| **Most recent prior** | `mostRecentPrior: true` | Queries datalake for latest `ifw_effective_date` strictly before T-0 (handles weekends/gaps) |
| **Most recent** | `mostRecent: true` | Queries datalake for latest `ifw_effective_date` on or before T-0 (inclusive) |
| **Default** | No date fields | Both min and max fall back to `__etlEffectiveDate` |

For `mostRecentPrior` and `mostRecent`, if no matching date exists in the datalake, `Execute` returns an empty DataFrame with the correct column schema (including `ifw_effective_date`) instead of throwing.

## Empty Result Handling

When a query returns zero rows (weekends, holidays, filters that exclude all data), `BuildDataFrame` constructs the DataFrame using the known column names (including `ifw_effective_date`) instead of passing an empty row list. This preserves column schema so downstream modules always receive a structurally valid DataFrame. The practical effect: jobs that encounter no-data dates produce header-only CSV output instead of crashing downstream.

## Config Properties

| JSON Property | Required | Description |
|---|---|---|
| `type` | Yes | `"DataSourcing"` |
| `resultName` | Yes | Name to store the DataFrame under in shared state |
| `schema` | Yes | PostgreSQL schema name (e.g., `"datalake"`) |
| `table` | Yes | Table name |
| `columns` | Yes | Array of column names |
| `additionalFilter` | No | Extra SQL WHERE clause |
| `minEffectiveDate` | No | Static min date (use with `maxEffectiveDate`) |
| `maxEffectiveDate` | No | Static max date (use with `minEffectiveDate`) |
| `lookbackDays` | No | Number of days to look back from effective date |
| `mostRecentPrior` | No | Query for latest date strictly before effective date |
| `mostRecent` | No | Query for latest date on or before effective date |

## Examples

```json
// Default: uses __etlEffectiveDate for both min and max
{
  "type": "DataSourcing",
  "resultName": "customers",
  "schema": "datalake",
  "table": "customers",
  "columns": ["id", "first_name", "last_name"]
}

// Lookback: T-3 through T-0 (4 days inclusive)
{
  "type": "DataSourcing",
  "resultName": "recent_txns",
  "schema": "datalake",
  "table": "transactions",
  "columns": ["txn_id", "amount"],
  "lookbackDays": 3
}

// Most recent prior: latest date strictly before T-0
{
  "type": "DataSourcing",
  "resultName": "prior_snapshot",
  "schema": "datalake",
  "table": "balances",
  "columns": ["account_id", "balance"],
  "mostRecentPrior": true
}

// Most recent: latest date on or before T-0
{
  "type": "DataSourcing",
  "resultName": "current_snapshot",
  "schema": "datalake",
  "table": "balances",
  "columns": ["account_id", "balance"],
  "mostRecent": true
}
```
