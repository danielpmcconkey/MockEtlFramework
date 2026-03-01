# MockEtlFramework

A C# proof-of-concept replicating a production PySpark ETL Framework. Reads job configurations (JSON), executes module chains, and produces file output.

**Your full mission and workflow are in `POC3/BLUEPRINT.md`. Read it first.**

## Building & Running

```bash
dotnet build                                              # Build from repo root
dotnet test                                               # Run all tests
dotnet run --project JobExecutor -- {JobName}              # Run single job
dotnet run --project JobExecutor                           # Run all active jobs (auto-advance)
dotnet run --project JobExecutor -- 2024-10-15             # Run all for specific date
dotnet run --project JobExecutor -- 2024-10-15 {JobName}   # Run one job for specific date
```

## Database

- PostgreSQL at localhost, user: `dansdev`, database: `atc`
- Password: env var `PGPASS` contains hex-encoded UTF-16 LE string
- Decode: `echo "$PGPASS" | xxd -r -p | iconv -f UTF-16LE -t UTF-8`
- Use this pattern for psql:
  ```bash
  export PGPASSWORD=$(echo "$PGPASS" | xxd -r -p | iconv -f UTF-16LE -t UTF-8) && psql -h localhost -U dansdev -d atc -c "..."
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
- `Lib/Control/JobExecutorService.cs` — orchestrates job execution and auto-advancement
- `Lib/ConnectionHelper.cs` — database connection helper
- `DataSourcing.MinDateKey` = `__minEffectiveDate`
- `DataSourcing.MaxDateKey` = `__maxEffectiveDate`

## Guardrails

- **NEVER** modify files in `Lib/` — the framework is fixed
- **NEVER** modify or delete data in `datalake` schema
- **NEVER** modify original V1 job configs or V1 External modules — create V2 versions
- **NEVER** modify anything in `Output/curated/` — this is the V1 baseline for comparison
- **NEVER** modify anything in `Tools/proofmark/` — this is a COTS tool, treat it as read-only

## Prior Run Artifacts

The `Phase3/` directory and any existing `*_v2.json` files in `JobExecutor/Jobs/` are artifacts from a prior run. **Ignore them entirely.** Do not read, reference, or build upon them. Your work goes in `POC3/`.
