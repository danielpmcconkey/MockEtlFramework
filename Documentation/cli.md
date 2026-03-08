# JobExecutor CLI

Entry point for running jobs. Defined in `JobExecutor/Program.cs`.

## Usage

```
JobExecutor --service                         # long-running queue executor (polls control.task_queue)
JobExecutor <effective_date>                  # run all active jobs for that date
JobExecutor <effective_date> <job_name>       # run one job for that date
```

- `effective_date` format: `yyyy-MM-dd`. **Required** for non-service invocations.
- `--service` mode delegates to `TaskQueueService`. All other modes delegate to `JobExecutorService`.
- `run_date` is always set to today internally and is never a CLI argument.

## Startup Sequence

1. Load `appsettings.json` from the output directory (copied at build time). If absent, `AppConfig` defaults are used.
2. Fail fast if `ETL_DB_PASSWORD` env var is not set.
3. Call `ConnectionHelper.Initialize(appConfig)` and `PathHelper.Initialize(appConfig)`.
4. Dispatch to the appropriate service based on arguments.

## Build & Run

```bash
export ETL_DB_PASSWORD='your_password'                   # required
dotnet build                                             # compile
dotnet test                                              # run xUnit tests
dotnet run --project JobExecutor -- 2024-10-15           # all jobs for date
dotnet run --project JobExecutor -- 2024-10-15 JobName   # one job for date
dotnet run --project JobExecutor -- --service            # queue executor
```
