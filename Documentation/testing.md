# Tests

`Lib.Tests/` -- xUnit test project. Tests do not require a live database. All DataFrame and Transformation tests operate entirely in memory. File writer tests use temporary directories.

| Test Class | Coverage |
|---|---|
| `DataFrameTests` | `DataFrame` API -- Count, Columns, Select, Filter, WithColumn, Drop, OrderBy, Limit, Union, Distinct, Join (inner + left), GroupBy/Count |
| `TransformationTests` | `Transformation` module -- basic SELECT, WHERE, column projection, JOIN across two DataFrames, GROUP BY aggregation, shared state preservation, non-DataFrame entries silently ignored |
| `ModuleFactoryTests` | `ModuleFactory` -- all module types, optional fields (`additionalFilter`, `lookbackDays`, `mostRecentPrior`), both write modes, mutually exclusive date mode validation (lookback+mostRecentPrior, lookback+static dates), unknown type error, missing type field error |
| `DataSourcingTests` | `DataSourcing` date resolution -- lookback range calculation, zero-day lookback, static date passthrough, `__etlEffectiveDate` fallback, missing effective date errors, mutually exclusive mode validation (all conflict combinations), negative lookbackDays |
| `AppConfigTests` | `AppConfig` defaults, `DatabaseSettings` defaults, `TaskQueueSettings` defaults, `ConnectionHelper` string building, env var sourcing for password, negative test proving `appsettings.json` cannot override the password env var |
| `CsvFileWriterTests` | `CsvFileWriter` -- header/data row output, `etl_effective_date` injection, no-header mode, RFC 4180 quoting (commas, double-quotes), null rendering, trailer format tokens, overwrite mode (date partitioning, idempotent reruns), append mode (union with prior partition, trailer stripping with/without trailer format), UTF-8 no BOM, LF/CRLF line endings, directory creation, missing DataFrame/effective date errors, shared state passthrough |
| `ParquetFileWriterTests` | `ParquetFileWriter` -- single/multi-part file output, row count preservation across parts, overwrite mode (deletes existing), directory creation, missing DataFrame/effective date errors, shared state passthrough, null handling, `etl_effective_date` injection, schema validation, native date/datetime types, nullable date columns, append mode (first run, union with prior partition) |

## Running Tests

```bash
dotnet test
```
