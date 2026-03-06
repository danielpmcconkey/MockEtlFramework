# MockEtlFramework

A C# proof-of-concept replicating a production PySpark ETL Framework. Reads job configurations (JSON), executes module chains, and produces file output.

**For ATC program context, see `AtcStrategy/POC4/ProgramDoctrine/program-doctrine.md`.**

## Building & Running

```bash
dotnet build                                              # Build from repo root
dotnet test                                               # Run all tests
dotnet run --project JobExecutor -- 2024-10-15             # Run all active jobs for date (date REQUIRED)
dotnet run --project JobExecutor -- 2024-10-15 {JobName}   # Run one job for specific date
```

## Database

- PostgreSQL at `172.18.0.1`, user: `claude`, password: `claude`, database: `atc`
- Use this pattern for psql:
  ```bash
  PGPASSWORD=claude psql -h 172.18.0.1 -U claude -d atc -c "..."
  ```
- Schemas:
  - `datalake` — source data (NEVER modify)
  - `control` — job metadata and run history

## Framework Architecture

Read `Documentation/Architecture.md` for the full overview. Key pointers:

- `Lib/Modules/DataSourcing.cs` — reads from datalake, effective dates injected via shared state
- `Lib/Modules/Transformation.cs` — registers DataFrames as SQLite tables, runs user-supplied SQL
- `Lib/Modules/CsvFileWriter.cs` — CSV output with optional trailers and configurable line endings
- `Lib/Modules/ParquetFileWriter.cs` — Parquet output with configurable part count
- `Lib/Modules/External.cs` — loads user-supplied .NET assemblies via reflection
- `Lib/Control/JobExecutorService.cs` — orchestrates job execution for a single effective date
- `Lib/ConnectionHelper.cs` — database connection helper
- `DataSourcing.EtlEffectiveDateKey` = `__etlEffectiveDate`

## Guardrails

- Files in `Lib/` may be modified for POC4 framework changes (date-partitioned writers, column injection, etc.)
- **NEVER** modify or delete data in `datalake` schema
- **NEVER** modify original V1 job configs or V1 External modules — create V2 versions in POC4
- **NEVER** modify anything in `Output/curated/` — V1 baseline directory (reserved, currently empty)
- **NEVER** modify anything in `Tools/proofmark/` — this is a COTS tool, treat it as read-only

## Serena (MCP — Semantic Code Navigation)

Serena is available as an MCP server providing IDE-level code intelligence via Roslyn.
Prefer Serena's tools over grep/read when you need to:

- **Understand a file's structure:** `get_symbols_overview` instead of reading the whole file
- **Find who calls a method:** `find_referencing_symbols` instead of grepping the method name
- **Find a class or method by name:** `find_symbol` instead of glob/grep
- **Rename across the codebase:** `rename_symbol` instead of find-and-replace
- **Replace a method body:** `replace_symbol_body` instead of line-based editing

Serena understands C# semantics — it distinguishes declarations from references, resolves
across projects, and handles overloads. Grep finds text; Serena finds meaning.

Still use grep/read for: searching string literals, config files, JSON, comments, or anything
that isn't a C# code symbol.

## Prior Run Artifacts

All POC3 artifacts and V2 job configs have been cleaned up. If any `*_v2.json` files or `Phase3/` directories appear, they are stale and should be ignored.
