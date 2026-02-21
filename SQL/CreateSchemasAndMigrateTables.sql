-- Create the two schemas
CREATE SCHEMA IF NOT EXISTS datalake;
CREATE SCHEMA IF NOT EXISTS curated;

-- Move all tables from public into datalake
ALTER TABLE public.accounts           SET SCHEMA datalake;
ALTER TABLE public.addresses          SET SCHEMA datalake;
ALTER TABLE public.customers          SET SCHEMA datalake;
ALTER TABLE public.customers_segments SET SCHEMA datalake;
ALTER TABLE public.segments           SET SCHEMA datalake;
ALTER TABLE public.transactions       SET SCHEMA datalake;

-- Grant dansdev privileges on datalake
GRANT USAGE, CREATE ON SCHEMA datalake TO dansdev;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA datalake TO dansdev;
ALTER DEFAULT PRIVILEGES IN SCHEMA datalake GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO dansdev;

-- Grant dansdev privileges on curated
GRANT USAGE, CREATE ON SCHEMA curated TO dansdev;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA curated TO dansdev;
ALTER DEFAULT PRIVILEGES IN SCHEMA curated GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO dansdev;
