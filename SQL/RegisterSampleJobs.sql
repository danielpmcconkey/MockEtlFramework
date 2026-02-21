-- ==============================================================================
-- RegisterSampleJobs.sql
-- Inserts sample job registrations into control.jobs.
-- Run this after CreateControlSchema.sql.
-- Update job_conf_path values if the repository is checked out at a
-- different location on your machine.
-- ==============================================================================

INSERT INTO control.jobs (job_name, description, job_conf_path)
VALUES (
    'CustomerAccountSummary',
    'For each customer, joins the Oct-31 customer and account snapshots and writes '
    'a summary row containing account count and total active balance to curated.',
    '/media/dan/fdrive/codeprojects/MockEtlFramework/JobExecutor/Jobs/customer_account_summary.json'
)
ON CONFLICT (job_name) DO NOTHING;
